using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// The other hazards around an address, from free public sources (no keys):
///   Air quality — Open-Meteo (US AQI now + the last 7 days).
///   FEMA flood zone — FEMA National Flood Hazard Layer (the zone at the address, and zone outlines nearby).
///   Fire — NIFC/WFIGS: active fire perimeters and past burn scars nearby.
///   Shelters &amp; hospitals — FEMA National Shelter System and Medicare-registered hospitals.
/// Each source fails soft: one being down leaves that card empty, not the whole page.
/// </summary>
public sealed class HazardsService
{
    private const string AirUrl = "https://air-quality-api.open-meteo.com/v1/air-quality?latitude={0}&longitude={1}&current=us_aqi,pm2_5,ozone&hourly=us_aqi&past_days=7&forecast_days=1&timezone=auto";
    private const string FemaZonesUrl = "https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/28/query";
    private const string FireCurrentUrl = "https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/WFIGS_Interagency_Perimeters_Current/FeatureServer/0/query";
    private const string FireHistoryUrl = "https://services3.arcgis.com/T4QMspbfLg3qTGWY/arcgis/rest/services/WFIGS_Interagency_Perimeters/FeatureServer/0/query";
    private const string SheltersUrl = "https://gis.fema.gov/arcgis/rest/services/NSS/FEMA_NSS/MapServer/5/query";
    private const string OpenSheltersUrl = "https://gis.fema.gov/arcgis/rest/services/NSS/FEMA_NSS/MapServer/0/query";
    private const string HospitalsUrl = "https://services2.arcgis.com/FiaPA4ga0iQKduv3/arcgis/rest/services/Medicare_Hospitals/FeatureServer/0/query";

    private const double FireSearchKm = 80, ShelterSearchKm = 30, ZoneOverlayM = 1200;
    private const int PastFireLimit = 40;   // nearest burn scars kept; a western city has hundreds within 80 km
    private const string FireFields = "poly_IncidentName,poly_GISAcres,attr_FireDiscoveryDateTime,attr_PercentContained,attr_FireOutDateTime";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    // Static: AddHttpClient makes a new HazardsService per request, so an instance cache would never hit.
    private static readonly ConcurrentDictionary<string, (DateTime at, Hazards data)> _cache = new();

    private readonly HttpClient _http;
    private readonly ILogger<HazardsService> _log;

    public HazardsService(HttpClient http, ILogger<HazardsService> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
        // FEMA answers 403 to requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniGuard/1.0");
        _log = log;
    }

    public async Task<Hazards> GetAsync(double lat, double lng, CancellationToken ct = default)
    {
        string key = $"{lat:F4}|{lng:F4}";
        if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < CacheFor) return hit.data;

        var air = AirAsync(lat, lng, ct);
        var zone = FloodZoneAsync(lat, lng, ct);
        var fire = FireAsync(lat, lng, ct);
        var places = PlacesAsync(lat, lng, ct);
        await Task.WhenAll(air, zone, fire, places);

        var result = new Hazards
        {
            Air = await air,
            FloodZone = await zone,
            Fire = await fire,
            Shelters = (await places).shelters,
            Hospitals = (await places).hospitals,
            OpenShelterCount = (await places).openCount,
        };
        // A source that failed gets another try on the next request instead of staying empty for 30 minutes.
        if (result.Air is not null && result.FloodZone?.Overlay is not null && result.Fire is not null && (await places).complete)
        {
            if (_cache.Count > 1000) _cache.Clear();
            _cache[key] = (DateTime.UtcNow, result);
        }
        return result;
    }

    // ---------- air quality ----------

    private async Task<AirQuality?> AirAsync(double lat, double lng, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(string.Format(AirUrl, F(lat), F(lng)), ct));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            int aqi = (int)Math.Round(cur.GetProperty("us_aqi").GetDouble());
            var air = new AirQuality
            {
                Aqi = aqi,
                Category = AqiCategory(aqi),
                Pm25 = cur.TryGetProperty("pm2_5", out var pm) && pm.ValueKind == JsonValueKind.Number ? Math.Round(pm.GetDouble(), 1) : null,
                AsOf = cur.GetProperty("time").GetString() ?? "",
            };
            // Daily maximum AQI for the past 7 days.
            var times = root.GetProperty("hourly").GetProperty("time").EnumerateArray().Select(t => t.GetString() ?? "").ToList();
            var vals = root.GetProperty("hourly").GetProperty("us_aqi").EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN).ToList();
            foreach (var g in times.Select((t, i) => (day: t.Length >= 10 ? t[..10] : t, v: vals[i]))
                                   .Where(x => !double.IsNaN(x.v) && string.CompareOrdinal(x.day, air.AsOf[..Math.Min(10, air.AsOf.Length)]) <= 0)
                                   .GroupBy(x => x.day).OrderBy(g => g.Key).TakeLast(7))
                air.History.Add(new AirDay { Date = g.Key, MaxAqi = (int)Math.Round(g.Max(x => x.v)) });
            return air;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Air quality lookup failed");
            return null;
        }
    }

    /// <summary>EPA AQI bands.</summary>
    public static string AqiCategory(int aqi) => aqi switch
    {
        <= 50 => "Good",
        <= 100 => "Moderate",
        <= 150 => "Unhealthy for sensitive groups",
        <= 200 => "Unhealthy",
        <= 300 => "Very unhealthy",
        _ => "Hazardous",
    };

    // ---------- FEMA flood zone ----------

    private async Task<FloodZoneInfo?> FloodZoneAsync(double lat, double lng, CancellationToken ct)
    {
        try
        {
            var hereTask = ArcGisAsync(FemaZonesUrl, lat, lng, 0, "FLD_ZONE,ZONE_SUBTY,SFHA_TF", geometry: false, ct: ct);
            var nearbyTask = ArcGisGeoJsonAsync(FemaZonesUrl, lat, lng, ZoneOverlayM, "FLD_ZONE,SFHA_TF", 0.0001, ct);
            await Task.WhenAll(hereTask, nearbyTask);
            var here = await hereTask;
            var nearby = await nearbyTask;
            if (here is null) return null;
            bool any = here.Count > 0;
            var first = any ? here[0] : default;
            string zone = any ? Str(first, "FLD_ZONE") : "";
            bool sfha = any && Str(first, "SFHA_TF") == "T";
            return new FloodZoneInfo
            {
                Zone = zone,
                Subtype = any ? Str(first, "ZONE_SUBTY") : "",
                HighRisk = sfha,
                Meaning = zone.Length == 0 ? "No FEMA flood map covers this spot."
                    : sfha ? $"Zone {zone}: FEMA's high-risk flood area (1% or more chance of flooding each year). Flood insurance is usually required with a federally backed mortgage."
                    : zone == "X" && Str(first, "ZONE_SUBTY").Contains("0.2 PCT") ? "Zone X (shaded): outside the 100-year floodplain, inside the 500-year floodplain (0.2% chance each year). Stormwater can still flood here."
                    : zone == "X" ? "Zone X: outside FEMA's 100-year floodplain. FEMA maps rivers and coasts, so stormwater can still flood here."
                    : $"Zone {zone}: see FEMA's flood map for details.",
                Overlay = nearby,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "FEMA flood zone lookup failed");
            return null;
        }
    }

    // ---------- fire ----------

    private async Task<FireInfo?> FireAsync(double lat, double lng, CancellationToken ct)
    {
        try
        {
            double radius = FireSearchKm * 1000;
            var activeTask = ArcGisGeoJsonAsync(FireCurrentUrl, lat, lng, radius, FireFields, 0.0005, ct);
            var pastTask = ArcGisGeoJsonAsync(FireHistoryUrl, lat, lng, radius, FireFields, 0.0005, ct);
            await Task.WhenAll(activeTask, pastTask);
            var active = await activeTask;
            var past = await pastTask;
            if (active is null || past is null) return null;   // unknown, which isn't the same as "no fires nearby"

            // The "current" feed keeps fires for weeks after they're fully contained; those count as past.
            bool StillBurning(JsonNode? f)
            {
                var p = f?["properties"];
                return Num(p?["attr_FireOutDateTime"]) is null && (Num(p?["attr_PercentContained"]) ?? 0) < 100;
            }

            FireEvent Event(JsonNode? f, bool isActive)
            {
                var p = f?["properties"];
                var (dKm, _, _) = NearestOnGeometry(f?["geometry"], lat, lng);
                double? ms = Num(p?["attr_FireDiscoveryDateTime"]);
                return new FireEvent
                {
                    Name = p?["poly_IncidentName"]?.ToString() ?? "Unnamed fire",
                    Acres = Num(p?["poly_GISAcres"]) is { } acres ? Math.Round(acres) : null,
                    Year = ms is { } m ? DateTimeOffset.FromUnixTimeMilliseconds((long)m).Year : null,
                    DistanceKm = Math.Round(dKm, 1),
                    Active = isActive,
                };
            }

            var current = ((JsonArray)active["features"]!).ToList();
            var burning = current.Where(StillBurning).Select(f => (f, ev: Event(f, true))).OrderBy(x => x.ev.DistanceKm).ToList();
            var scars = current.Where(f => !StillBurning(f)).Concat((JsonArray)past["features"]!)
                .Select(f => (f, ev: Event(f, false)))
                .Where(x => !burning.Any(b => b.ev.Name == x.ev.Name))
                .GroupBy(x => (x.ev.Name, x.ev.Year)).Select(g => g.First())   // a fire can be in both feeds
                .OrderBy(x => x.ev.DistanceKm).Take(PastFireLimit).ToList();

            // Map overlay: active perimeters plus past burn scars, tagged.
            var features = new JsonArray();
            foreach (var (f, ev) in burning.Concat(scars))
            {
                var clone = JsonNode.Parse(f!.ToJsonString())!;
                clone["properties"] = new JsonObject { ["active"] = ev.Active, ["name"] = ev.Name, ["year"] = ev.Year };
                features.Add(clone);
            }
            return new FireInfo
            {
                SearchKm = FireSearchKm,
                Active = burning.Select(x => x.ev).ToList(),
                Past = scars.Select(x => x.ev).ToList(),
                Overlay = new JsonObject { ["type"] = "FeatureCollection", ["features"] = features },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Fire lookup failed");
            return null;
        }
    }

    // ---------- shelters & hospitals ----------

    private async Task<(List<Facility> shelters, List<Facility> hospitals, int openCount, bool complete)> PlacesAsync(double lat, double lng, CancellationToken ct)
    {
        double radius = ShelterSearchKm * 1000;
        var sheltersTask = ArcGisAsync(SheltersUrl, lat, lng, radius, "shelter_name,address_1,city,evacuation_capacity", geometry: true, ct: ct);
        var openTask = ArcGisAsync(OpenSheltersUrl, lat, lng, 50_000, "shelter_name", geometry: false, ct: ct);
        var hospitalsTask = ArcGisAsync(HospitalsUrl, lat, lng, radius, "USER_Facility_Name,USER_Address,USER_City_Town,USER_Hospital_Type,USER_Emergency_Services", geometry: true, ct: ct);
        await Task.WhenAll(sheltersTask, openTask, hospitalsTask);

        List<Facility> Build(List<JsonElement>? feats, Func<JsonElement, Facility> make)
            => (feats ?? new()).Select(make).Where(x => x.Lat != 0).OrderBy(x => x.DistanceKm).Take(5).ToList();

        Facility Locate(JsonElement f, Facility x)
        {
            if (f.TryGetProperty("geometry", out var g) && g.TryGetProperty("x", out var gx) && g.TryGetProperty("y", out var gy))
            {
                x.Lng = gx.GetDouble();
                x.Lat = gy.GetDouble();
                x.DistanceKm = Math.Round(Km(lat, lng, x.Lat, x.Lng), 1);
            }
            return x;
        }

        var shelters = Build(await sheltersTask, f =>
        {
            var a = f.GetProperty("attributes");
            int? cap = a.TryGetProperty("evacuation_capacity", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null;
            return Locate(f, new Facility
            {
                Kind = "shelter",
                Name = TitleCase(Str(a, "shelter_name")),
                Address = $"{TitleCase(Str(a, "address_1"))}, {TitleCase(Str(a, "city"))}".Trim(' ', ','),
                Detail = cap is > 0 ? $"Designated shelter · room for {cap:N0}" : "Designated shelter",
            });
        });
        var hospitals = Build(await hospitalsTask, f =>
        {
            var a = f.GetProperty("attributes");
            bool er = Str(a, "USER_Emergency_Services") == "Yes";
            return Locate(f, new Facility
            {
                Kind = "hospital",
                Name = TitleCase(Str(a, "USER_Facility_Name")),
                Address = $"{Str(a, "USER_Address")}, {Str(a, "USER_City_Town")}".Trim(' ', ','),
                Detail = $"{Str(a, "USER_Hospital_Type")}{(er ? " · emergency room" : "")}",
                Emergency = er,
            });
        });
        // Emergency rooms first among the nearest hospitals.
        hospitals = hospitals.OrderByDescending(h => h.Emergency).ThenBy(h => h.DistanceKm).ToList();
        bool complete = await sheltersTask is not null && await openTask is not null && await hospitalsTask is not null;
        return (shelters, hospitals, (await openTask)?.Count ?? 0, complete);
    }

    // ---------- ArcGIS helpers ----------

    private string Query(string baseUrl, double lat, double lng, double radiusM, string fields, bool geometry)
        => baseUrl + $"?geometry={F(lng)},{F(lat)}&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects" +
           (radiusM > 0 ? $"&distance={F(radiusM)}&units=esriSRUnit_Meter" : "") +
           $"&outFields={fields}&returnGeometry={(geometry ? "true" : "false")}&outSR=4326&where=1%3D1";

    private async Task<List<JsonElement>?> ArcGisAsync(string baseUrl, double lat, double lng, double radiusM, string fields, bool geometry, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(Query(baseUrl, lat, lng, radiusM, fields, geometry) + "&f=json", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("features", out var feats)) return null;
            return feats.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "ArcGIS query failed: {Url}", baseUrl);
            return null;
        }
    }

    private async Task<JsonNode?> ArcGisGeoJsonAsync(string baseUrl, double lat, double lng, double radiusM, string fields, double simplifyDeg, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(Query(baseUrl, lat, lng, radiusM, fields, true) +
                $"&maxAllowableOffset={F(simplifyDeg)}&geometryPrecision=6&f=geojson", ct);
            var node = JsonNode.Parse(json);
            return node?["features"] is JsonArray ? node : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "ArcGIS GeoJSON query failed: {Url}", baseUrl);
            return null;
        }
    }

    /// <summary>Distance (km) from a point to the nearest vertex of a GeoJSON polygon/multipolygon.</summary>
    private static (double km, double lat, double lng) NearestOnGeometry(JsonNode? geometry, double lat, double lng)
    {
        double best = double.MaxValue, bLat = 0, bLng = 0;
        void Walk(JsonNode? n)
        {
            if (n is not JsonArray arr || arr.Count == 0) return;
            if (arr[0] is JsonValue && arr.Count >= 2)
            {
                double x = arr[0]!.GetValue<double>(), y = arr[1]!.GetValue<double>();
                double d = Km(lat, lng, y, x);
                if (d < best) { best = d; bLat = y; bLng = x; }
                return;
            }
            foreach (var c in arr) Walk(c);
        }
        Walk(geometry?["coordinates"]);
        return (best == double.MaxValue ? 0 : best, bLat, bLng);
    }

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static double Km(double lat1, double lng1, double lat2, double lng2)
    {
        double dLat = (lat2 - lat1) * Math.PI / 180, dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string Str(JsonElement el, string name)
    {
        var a = el.TryGetProperty("attributes", out var attrs) ? attrs : el;
        if (!a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    }

    private static string TitleCase(string s)
        => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.Trim().ToLowerInvariant());

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}
