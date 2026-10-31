using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Tap-water quality for an address, from public EPA data (no keys):
///   1. Which community water system serves the point — EPA's Community Water System Service Area
///      Boundaries; if the point is just outside every mapped boundary, the nearest system within 2 km.
///   2. That system's violation history — EPA Envirofacts SDWIS (all years) for health-based
///      violations, plus EPA ECHO's detailed facility report (last 5 years) for contaminant names and
///      lead / copper 90th-percentile results.
/// Testing and reporting lapses are counted separately: they are paperwork, not contamination.
/// </summary>
public sealed class TapWaterService
{
    private const string BoundariesUrl = "https://services.arcgis.com/cJ9YHowT8TU7DUyn/arcgis/rest/services/Water_System_Boundaries/FeatureServer/0/query";
    private const string ViolationsUrl = "https://data.epa.gov/efservice/VIOLATION/PWSID/{0}/JSON";
    private const string EchoReportUrl = "https://echodata.epa.gov/echo/dfr_rest_services.get_dfr?p_id={0}&p_system=SDWIS&output=JSON";
    private const double NearestSystemM = 2000;
    private const int HealthWindowYears = 10;
    // EPA Lead and Copper Rule action levels (90th percentile of home tap samples), mg/L.
    private const double LeadActionLevel = 0.015, CopperActionLevel = 1.3;

    /// <summary>
    /// Names for SDWIS contaminant codes seen in health-based violations. Only codes whose meaning is
    /// certain are listed; anything else is shown by its code with a link to EPA's full report.
    /// </summary>
    private static readonly Dictionary<string, string> ContaminantNames = new()
    {
        ["2950"] = "Total trihalomethanes (TTHM, disinfection byproduct)",
        ["2456"] = "Haloacetic acids (HAA5, disinfection byproduct)",
        ["3100"] = "Coliform bacteria (Total Coliform Rule)",
        ["8000"] = "Bacteria (Revised Total Coliform Rule)",
        ["1040"] = "Nitrate",
        ["1041"] = "Nitrite",
        ["1005"] = "Arsenic",
        ["PB90"] = "Lead",
        ["CU90"] = "Copper",
        ["4000"] = "Gross alpha radiation",
        ["4006"] = "Uranium",
        ["4010"] = "Combined radium (Ra-226 + Ra-228)",
    };

    private static readonly Dictionary<string, string> CategoryNames = new()
    {
        ["MCL"] = "exceeded a legal limit",
        ["MRDL"] = "exceeded the disinfectant limit",
        ["TT"] = "missed a required treatment step",
        ["MR"] = "missed or late testing or reporting",
        ["MON"] = "missed or late testing",
        ["RPT"] = "missed or late reporting",
        ["Other"] = "other rule violation",
    };

    private readonly HttpClient _http;
    private readonly ILogger<TapWaterService> _log;
    private readonly ConcurrentDictionary<string, (DateTime at, TapWater result)> _cache = new();

    public TapWaterService(HttpClient http, ILogger<TapWaterService> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(45);
        _log = log;
    }

    public async Task<TapWater> GetAsync(double lat, double lng, CancellationToken ct = default)
    {
        var (system, inside) = await FindSystemAsync(lat, lng, ct);
        if (system is null)
        {
            return new TapWater
            {
                Status = "no-system",
                Headline = "No public water system mapped here — the home likely uses a private well.",
                Notes = { "EPA tap-water rules don't cover private wells. Test a well at least once a year for bacteria and nitrate (your county health department can help)." },
            };
        }

        // Violation history changes slowly: cache per system for a day.
        if (_cache.TryGetValue(system.Value.pwsid, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromHours(24))
            return WithLocation(hit.result, inside);

        var violationsTask = FetchViolationsAsync(system.Value.pwsid, ct);
        var echoTask = FetchEchoAsync(system.Value.pwsid, ct);
        await Task.WhenAll(violationsTask, echoTask);
        var violations = await violationsTask;
        var echo = await echoTask;

        var result = Summarize(system.Value, violations, echo);
        if (violations is not null) _cache[system.Value.pwsid] = (DateTime.UtcNow, result);
        return WithLocation(result, inside);
    }

    private static TapWater WithLocation(TapWater r, bool inside)
    {
        var copy = JsonSerializer.Deserialize<TapWater>(JsonSerializer.Serialize(r))!;
        copy.InsideBoundary = inside;
        if (!inside)
            copy.Notes.Insert(0, "This address is just outside every mapped service boundary; this is the nearest public system — confirm with your water bill.");
        return copy;
    }

    // ---------- 1. which system ----------

    private async Task<((string pwsid, string name, int? population, string reportUrl)? system, bool inside)> FindSystemAsync(double lat, double lng, CancellationToken ct)
    {
        var inside = await QueryBoundariesAsync(lat, lng, 0, ct);
        if (inside.Count > 0) return (inside.OrderBy(s => s.population ?? int.MaxValue).First(), true);
        var near = await QueryBoundariesAsync(lat, lng, NearestSystemM, ct);
        return near.Count > 0 ? (near.OrderByDescending(s => s.population ?? 0).First(), false) : (null, false);
    }

    private async Task<List<(string pwsid, string name, int? population, string reportUrl)>> QueryBoundariesAsync(double lat, double lng, double radiusM, CancellationToken ct)
    {
        var url = BoundariesUrl +
                  $"?geometry={F(lng)},{F(lat)}&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects" +
                  (radiusM > 0 ? $"&distance={F(radiusM)}&units=esriSRUnit_Meter" : "") +
                  "&outFields=PWSID,PWS_Name,Population_Served_Count,Detailed_Facility_Report&returnGeometry=false&f=json";
        var list = new List<(string, string, int?, string)>();
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            if (!doc.RootElement.TryGetProperty("features", out var feats)) return list;
            foreach (var f in feats.EnumerateArray())
            {
                var a = f.GetProperty("attributes");
                string id = Str(a, "PWSID");
                if (id.Length == 0) continue;
                int? pop = a.TryGetProperty("Population_Served_Count", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
                string report = Str(a, "Detailed_Facility_Report");
                list.Add((id, TitleCase(Str(a, "PWS_Name")), pop,
                    report.Length > 0 ? report : $"https://echo.epa.gov/detailed-facility-report?fid={id}&sys=SDWIS"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Water system boundary lookup failed");
        }
        return list;
    }

    // ---------- 2. records ----------

    private sealed record Violation(string Id, string ContaminantCode, string Category, bool HealthBased, DateTime? Begin, DateTime? End, DateTime? Resolved, string Status);

    private async Task<List<Violation>?> FetchViolationsAsync(string pwsid, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(string.Format(ViolationsUrl, pwsid), ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var list = new List<Violation>();
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                list.Add(new Violation(
                    Str(v, "violation_id"),
                    Str(v, "contaminant_code"),
                    Str(v, "violation_category_code"),
                    Str(v, "is_health_based_ind") == "Y",
                    Date(v, "compl_per_begin_date"),
                    Date(v, "compl_per_end_date"),
                    Date(v, "rtc_date"),
                    Str(v, "compliance_status_code")));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Envirofacts violation lookup failed for {Pwsid}", pwsid);
            return null;
        }
    }

    /// <summary>ECHO data: extract date, latest lead/copper, and contaminant names by violation id.</summary>
    private sealed record Echo(string ExtractDate, (double mgL, string period)? Lead, (double mgL, string period)? Copper, Dictionary<string, string> NamesById);

    private async Task<Echo?> FetchEchoAsync(string pwsid, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(string.Format(EchoReportUrl, pwsid), ct));
            if (!doc.RootElement.TryGetProperty("Results", out var r)) return null;
            string extract = "";
            if (r.TryGetProperty("SystemExtractDates", out var sd) && sd.TryGetProperty("Dates", out var dates))
                foreach (var d in dates.EnumerateArray())
                    if (Str(d, "EPASystem") == "SDWIS") extract = Str(d, "SystemExtractDate");

            (double, string)? Latest(JsonElement samples, string valueKey, string datesKey)
            {
                (double, string)? best = null;
                string bestPeriod = "";
                foreach (var s in samples.EnumerateArray())
                {
                    if (!double.TryParse(Str(s, valueKey), NumberStyles.Float, CultureInfo.InvariantCulture, out var mg)) continue;
                    string period = Str(s, datesKey);
                    if (best is null || string.CompareOrdinal(period, bestPeriod) > 0) { best = (mg, period); bestPeriod = period; }
                }
                return best;
            }

            (double, string)? lead = null, copper = null;
            if (r.TryGetProperty("LeadAndCopperRule5Yr", out var lcr) && lcr.TryGetProperty("Sources", out var srcs) && srcs.GetArrayLength() > 0)
            {
                var src = srcs[0];
                if (src.TryGetProperty("LeadSamples", out var ls) && ls.ValueKind == JsonValueKind.Array) lead = Latest(ls, "PB90Value", "PB90Dates");
                if (src.TryGetProperty("CopperSamples", out var cs) && cs.ValueKind == JsonValueKind.Array) copper = Latest(cs, "CU90Value", "CU90Dates");
            }
            var names = new Dictionary<string, string>();
            if (r.TryGetProperty("ViolationsEnforcementActions", out var vea) && vea.TryGetProperty("Sources", out var vs))
                foreach (var source in vs.EnumerateArray())
                    if (source.TryGetProperty("Violations", out var vl) && vl.ValueKind == JsonValueKind.Array)
                        foreach (var v in vl.EnumerateArray())
                            if (Str(v, "ViolationID") is { Length: > 0 } id && Str(v, "ContaminantName") is { Length: > 0 } nm)
                                names[id] = nm;
            return new Echo(extract, lead, copper, names);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "ECHO report lookup failed for {Pwsid}", pwsid);
            return null;
        }
    }

    // ---------- 3. plain-language summary ----------

    private static TapWater Summarize((string pwsid, string name, int? population, string reportUrl) sys, List<Violation>? violations, Echo? echo)
    {
        var t = new TapWater
        {
            Status = "ok",
            SystemName = sys.name,
            Pwsid = sys.pwsid,
            PopulationServed = sys.population,
            ReportUrl = sys.reportUrl,
            DataAsOf = echo?.ExtractDate ?? "",
            HealthWindowYears = HealthWindowYears,
        };

        if (violations is null)
        {
            t.Status = "unavailable";
            t.Headline = $"{sys.name} — EPA's violation records couldn't be reached right now.";
            return t;
        }

        // ECHO names the recent violations; a contaminant code means the same thing on older ones, so learn
        // code → name from any violation ECHO names and reuse it for the rest.
        var namesByCode = new Dictionary<string, string>();
        if (echo is not null)
            foreach (var v in violations)
                if (v.ContaminantCode.Length > 0 && echo.NamesById.TryGetValue(v.Id, out var known))
                    namesByCode.TryAdd(v.ContaminantCode, known);

        var since = DateTime.UtcNow.AddYears(-HealthWindowYears);
        foreach (var v in violations.Where(v => v.HealthBased && (v.Begin ?? DateTime.MinValue) >= since)
                                    .OrderByDescending(v => v.Begin))
        {
            bool resolved = v.Resolved is not null || v.Status == "R";
            // Name: EPA ECHO's own name for this violation, else ECHO's name for the same code,
            // else a code we know for certain, else the code.
            string name = echo?.NamesById.TryGetValue(v.Id, out var en) == true ? en
                : namesByCode.TryGetValue(v.ContaminantCode, out var learned) ? learned
                : ContaminantNames.TryGetValue(v.ContaminantCode, out var n) ? n
                : $"Contaminant code {v.ContaminantCode}";
            var entry = new TapWaterViolation
            {
                Contaminant = name,
                What = CategoryNames.TryGetValue(v.Category, out var c) ? c : v.Category,
                Period = Period(v.Begin, v.End),
                Resolved = resolved,
                ResolvedOn = v.Resolved?.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                Count = 1,
            };
            // Systems often log one violation per facility or sample point: show identical ones once, with a count.
            var same = t.HealthViolations.FirstOrDefault(x => x.Contaminant == entry.Contaminant && x.What == entry.What
                && x.Period == entry.Period && x.Resolved == entry.Resolved && x.ResolvedOn == entry.ResolvedOn);
            if (same is not null) same.Count++;
            else t.HealthViolations.Add(entry);
        }
        t.OpenHealthViolations = t.HealthViolations.Where(v => !v.Resolved).Sum(v => v.Count);
        t.PaperworkLapses5Yr = violations.Count(v => !v.HealthBased && (v.Begin ?? DateTime.MinValue) >= DateTime.UtcNow.AddYears(-5));

        if (echo?.Lead is { } lead)
            t.Lead = new TapWaterMetal { Ppb = Math.Round(lead.mgL * 1000, 1), ActionLevelPpb = LeadActionLevel * 1000, Period = lead.period, OverActionLevel = lead.mgL > LeadActionLevel };
        if (echo?.Copper is { } cu)
            t.Copper = new TapWaterMetal { Ppb = Math.Round(cu.mgL * 1000, 1), ActionLevelPpb = CopperActionLevel * 1000, Period = cu.period, OverActionLevel = cu.mgL > CopperActionLevel };

        bool metalProblem = t.Lead?.OverActionLevel == true || t.Copper?.OverActionLevel == true;
        if (t.OpenHealthViolations > 0)
        {
            t.Level = "alert";
            t.Headline = $"{sys.name} has {t.OpenHealthViolations} unresolved health-based violation{(t.OpenHealthViolations == 1 ? "" : "s")}.";
        }
        else if (metalProblem)
        {
            t.Level = "alert";
            t.Headline = $"{sys.name}: home tap samples were above EPA's action level for {(t.Lead?.OverActionLevel == true ? "lead" : "copper")} in the latest period.";
        }
        else if (t.HealthViolations.Count > 0)
        {
            t.Level = "watch";
            int n = t.HealthViolations.Sum(v => v.Count);
            t.Headline = $"{sys.name} had {n} health-based violation{(n == 1 ? "" : "s")} in the last {HealthWindowYears} years — all resolved.";
        }
        else
        {
            t.Level = "good";
            t.Headline = $"{sys.name}: no health-based violations in the last {HealthWindowYears} years.";
        }

        if (t.PaperworkLapses5Yr > 0)
            t.Notes.Add($"{t.PaperworkLapses5Yr} testing or reporting lapse{(t.PaperworkLapses5Yr == 1 ? "" : "s")} in 5 years — paperwork, not contamination found in the water.");
        t.Notes.Add("This is the utility's record. Water in your home can still pick up lead from old pipes or fixtures — your utility's annual Consumer Confidence Report has the full test results.");
        return t;
    }

    // ---------- helpers ----------

    private static string Period(DateTime? begin, DateTime? end)
    {
        if (begin is null) return "";
        string b = begin.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        if (end is null || end.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture) == b) return b;
        return $"{b} – {end.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";
    }

    private static DateTime? Date(JsonElement el, string name)
        => DateTime.TryParse(Str(el, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    /// <summary>"FUQUAY-VARINA, TOWN OF" → "Town of Fuquay-Varina".</summary>
    private static string TitleCase(string s)
    {
        var t = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());
        foreach (var suffix in new[] { ", Town Of", ", City Of", ", Village Of", ", County Of" })
            if (t.EndsWith(suffix, StringComparison.Ordinal))
                return $"{suffix[2..].Replace(" Of", " of")} {t[..^suffix.Length]}";
        return t.Replace(" Of ", " of ").Replace(" And ", " and ").Replace(" & ", " & ");
    }

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}
