using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Official property lots (county parcel records). Street geocoders interpolate house numbers along
/// the road and often land one lot off; the parcel record is the authoritative location of an address.
/// Source: ReportAll USA (nationwide), configured with "ReportAll:ClientKey".
/// Without a key, or if ReportAll has no lot here, this returns null and the caller falls back to the
/// geocoded point (the UI then marks the location as approximate).
/// </summary>
public sealed class ParcelService
{
    private const string ReportAllUrl = "https://reportallusa.com/api/parcels";
    private const double SearchRadiusM = 250;
    private const string CacheVersion = "v2|";

    private readonly HttpClient _http;
    private readonly ILogger<ParcelService> _log;
    private readonly string _reportAllKey;
    private readonly AssessmentRepository _repo;
    private readonly ConcurrentDictionary<string, Parcel?> _cache = new();

    public ParcelService(HttpClient http, IConfiguration config, AssessmentRepository repo, ILogger<ParcelService> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(12);
        _log = log;
        _repo = repo;
        _reportAllKey = config["ReportAll:ClientKey"] ?? "";
    }

    /// <summary>
    /// Memory → Mongo → ReportAll. Answers (including "no lot here") are saved, so a repeat search or a
    /// restart never pays for the same lookup twice. lookup returns ok=false when the API was unavailable.
    /// </summary>
    private async Task<Parcel?> CachedAsync(string key, Func<Task<(bool ok, Parcel? parcel)>> lookup)
    {
        key = CacheVersion + key;   // bump CacheVersion when Parcel gains fields, so old entries are re-fetched
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var (stored, fromDb) = await _repo.GetParcelAsync(key);
        if (stored) return _cache[key] = fromDb;
        var (ok, parcel) = await lookup();
        if (!ok) return null;   // no key or service down: don't cache, try again next time
        _cache[key] = parcel;
        await _repo.SaveParcelAsync(key, parcel);
        return parcel;
    }

    /// <summary>The lot with this house number (and street, if given) nearest to a geocoded point.</summary>
    public async Task<Parcel?> FindByAddressAsync(double lat, double lng, string houseNumber, string? street, CancellationToken ct = default)
    {
        if (_reportAllKey.Length == 0) return null;
        var num = houseNumber.Trim().ToUpperInvariant().Replace("'", "");
        var wantStreet = StreetWord(street);
        return await CachedAsync($"a|{num}|{wantStreet}|{lat:F4}|{lng:F4}", async () =>
        {
            // Widen step by step: the lot under the geocode, then nearby lots. Small steps keep each
            // request under ReportAll's per-page cap, so the right lot is never cut off.
            foreach (var radius in new[] { 0.0, 80.0, SearchRadiusM })
            {
                var found = await ReportAllQueryAsync(lat, lng, radius, ct);
                if (found is null) return (false, null);
                var best = found
                    .Where(p => HouseNumberOf(p.Address) == num)
                    .Where(p => wantStreet is null || p.Address.ToUpperInvariant().Contains(wantStreet))
                    .OrderBy(p => Distance(lat, lng, p.Lat, p.Lng))
                    .FirstOrDefault();
                if (best is not null) return (true, best);
            }
            return (true, null);
        });
    }

    /// <summary>The lot under a clicked point.</summary>
    public async Task<Parcel?> FindAtAsync(double lat, double lng, CancellationToken ct = default)
    {
        if (_reportAllKey.Length == 0) return null;
        return await CachedAsync($"p|{lat:F5}|{lng:F5}", async () =>
        {
            var found = await ReportAllQueryAsync(lat, lng, 0, ct);
            return found is null ? (false, null) : (true, found.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Address)));
        });
    }

    /// <summary>Lots within radiusM of a point (0 = the lot containing it). Null when unavailable.</summary>
    private async Task<List<Parcel>?> ReportAllQueryAsync(double lat, double lng, double radiusM, CancellationToken ct)
    {
        if (_reportAllKey.Length == 0) return null;
        // Point → the lot containing it; radius → every lot intersecting a small square around the point.
        string wkt;
        if (radiusM <= 0) wkt = $"POINT({F(lng)} {F(lat)})";
        else
        {
            double dLat = radiusM / 111_320, dLng = radiusM / (111_320 * Math.Cos(lat * Math.PI / 180));
            double s = lat - dLat, n = lat + dLat, w = lng - dLng, e = lng + dLng;
            wkt = $"POLYGON(({F(w)} {F(s)},{F(e)} {F(s)},{F(e)} {F(n)},{F(w)} {F(n)},{F(w)} {F(s)}))";
        }
        var url = $"{ReportAllUrl}?client={Uri.EscapeDataString(_reportAllKey)}&v=9" +
                  $"&spatial_intersect={Uri.EscapeDataString(wkt)}&si_srid=4326&rpp={(radiusM > 0 ? 100 : 5)}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("ReportAll returned {Status}: {Body}", (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                _log.LogWarning("ReportAll response had no results: {Body}", body.Length > 300 ? body[..300] : body);
                return null;
            }
            var list = new List<Parcel>();
            foreach (var r in results.EnumerateArray())
            {
                string addr = Str(r, "address");
                if (addr.Length == 0)
                    addr = string.Join(' ', new[] { "addr_number", "addr_street_prefix", "addr_street_name", "addr_street_suffix", "addr_street_type" }
                        .Select(k => Str(r, k)).Where(x => x.Length > 0));
                var ring = FirstRingFromWkt(Str(r, "geom_as_wkt"));
                if (MakeParcel(addr, Str(r, "addr_city"), ring) is not { } p) continue;
                EstimatePaving(p, r);
                if (Num(r, "year_built") is { } yb && yb > 1700) p.YearBuilt = (int)yb;
                list.Add(p);
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "ReportAll parcel lookup failed");
            return null;
        }
    }

    /// <summary>Outer ring of the first polygon in a (MULTI)POLYGON WKT string.</summary>
    private static List<Coord> FirstRingFromWkt(string wkt)
    {
        int open = wkt.IndexOf("((", StringComparison.Ordinal);
        if (open < 0) return new();
        int start = open;
        while (start < wkt.Length && wkt[start] == '(') start++;
        int end = wkt.IndexOf(')', start);
        if (end < 0) return new();
        var ring = new List<Coord>();
        foreach (var pair in wkt[start..end].Split(','))
        {
            var xy = pair.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (xy.Length >= 2
                && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                ring.Add(new Coord(y, x));
        }
        return ring;
    }

    // ---------- paved-surface estimate ----------

    /// <summary>Typical impervious share of each NLCD land-cover class (USGS NLCD class definitions).</summary>
    private static readonly Dictionary<string, double> LandCoverImpervious = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Developed High Intensity"] = 90,
        ["Developed Medium Intensity"] = 65,
        ["Developed Low Intensity"] = 35,
        ["Developed Open Space"] = 10,
        ["Developed, Open Space"] = 10,
    };

    /// <summary>
    /// Paved/roofed share of the lot from county records: building footprint (floor area ÷ ~1.6 for
    /// typical two-story homes) plus ~500 sq ft of driveway and walks, and the federal NLCD land-cover
    /// mix of the lot. Takes the larger, because land cover can predate new construction.
    /// </summary>
    private static void EstimatePaving(Parcel p, JsonElement r)
    {
        double lotSqft = Num(r, "acreage_calc") is { } ac && ac > 0 ? ac * 43_560 : p.LotAreaM2 * 10.7639;
        if (lotSqft <= 0) return;
        p.LotAreaM2 = Math.Round(lotSqft / 10.7639);

        double? fromBuilding = null;
        if (Num(r, "bldg_sqft") is { } bs && bs > 0)
        {
            p.BuildingSqft = bs;
            fromBuilding = Math.Min(95, (bs / 1.6 + 500) / lotSqft * 100);
        }

        double? fromCover = null;
        if (r.TryGetProperty("land_cover", out var lc) && lc.ValueKind == JsonValueKind.Object)
        {
            double total = 0, weighted = 0;
            foreach (var cls in lc.EnumerateObject())
            {
                if (cls.Value.ValueKind != JsonValueKind.Number) continue;
                double acres = cls.Value.GetDouble();
                total += acres;
                weighted += acres * (LandCoverImpervious.TryGetValue(cls.Name, out var pct) ? pct : 0);
            }
            if (total > 0 && weighted > 0) fromCover = weighted / total;
        }

        if (fromBuilding is null && fromCover is null) return;
        bool useBuilding = (fromBuilding ?? -1) >= (fromCover ?? -1);
        p.ImperviousPct = Math.Round(useBuilding ? fromBuilding!.Value : fromCover!.Value);
        double acres2 = lotSqft / 43_560;
        p.ImperviousSource = useBuilding
            ? $"county building record ({p.BuildingSqft:N0} sq ft home on a {acres2:F2}-acre lot) plus driveway"
            : $"federal land-cover data for this {acres2:F2}-acre lot";
    }

    private static double? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    // ---------- helpers ----------

    private static Parcel? MakeParcel(string addr, string city, List<Coord> ring)
    {
        if (ring.Count < 3) return null;
        var (clat, clng) = Centroid(ring);
        return new Parcel
        {
            Address = string.IsNullOrWhiteSpace(city) ? addr.Trim() : $"{addr.Trim()}, {city.Trim()}",
            Lat = clat,
            Lng = clng,
            Outline = ring,
            LotAreaM2 = Math.Round(AreaM2(ring)),
        };
    }

    private static string Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>"123 MAIN ST" → "123".</summary>
    private static string? HouseNumberOf(string address)
    {
        var m = Regex.Match(address.TrimStart(), @"^(\d+[A-Za-z]?)\b");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>"Main Street" → "MAIN" (first word of the street name, enough to tell streets apart).</summary>
    private static string? StreetWord(string? street)
    {
        if (string.IsNullOrWhiteSpace(street)) return null;
        var w = street.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = w.FirstOrDefault(x => x.Length > 2 && !x.All(char.IsDigit) && x.ToUpperInvariant() is not ("NORTH" or "SOUTH" or "EAST" or "WEST"));
        return first?.ToUpperInvariant();
    }

    private static (double lat, double lng) Centroid(List<Coord> ring)
    {
        // Area-weighted polygon centroid (falls back to the vertex mean for degenerate rings).
        double a = 0, cx = 0, cy = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double cross = ring[j].Lng * ring[i].Lat - ring[i].Lng * ring[j].Lat;
            a += cross;
            cx += (ring[j].Lng + ring[i].Lng) * cross;
            cy += (ring[j].Lat + ring[i].Lat) * cross;
        }
        if (Math.Abs(a) < 1e-14) return (ring.Average(p => p.Lat), ring.Average(p => p.Lng));
        return (cy / (3 * a), cx / (3 * a));
    }

    private static double AreaM2(List<Coord> ring)
    {
        double lat0 = ring.Average(p => p.Lat) * Math.PI / 180, a = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            a += (ring[j].Lng * Math.Cos(lat0) * 111_320) * (ring[i].Lat * 111_320) - (ring[i].Lng * Math.Cos(lat0) * 111_320) * (ring[j].Lat * 111_320);
        return Math.Abs(a) / 2;
    }

    private static double Distance(double lat1, double lng1, double lat2, double lng2)
    {
        double dy = (lat2 - lat1) * 111_320, dx = (lng2 - lng1) * 111_320 * Math.Cos(lat1 * Math.PI / 180);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
