using System.Globalization;
using System.Text.Json;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Address → coordinates. Tries Geoapify first when a key is set (building-level points),
/// then the US Census geocoder (interpolated along the street, can land a few houses off),
/// then Photon (OSM) for other regions.
/// Server-side so the browser is never blocked by the geocoders' missing CORS headers.
/// </summary>
public sealed class GeocodingService
{
    private readonly HttpClient _http;
    private readonly string _geoapifyKey;

    public GeocodingService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _geoapifyKey = config["Geoapify:ApiKey"] ?? "";
    }

    public async Task<GeocodeResult?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        var result = await TryGeoapifyAsync(address, ct) ?? await TryCensusAsync(address, ct) ?? await TryPhotonAsync(address, ct);
        if (result is not null) result.HouseNumber ??= LeadingHouseNumber(address);
        return result;
    }

    /// <summary>"123 Main Street, ..." → "123".</summary>
    public static string? LeadingHouseNumber(string address)
    {
        var m = System.Text.RegularExpressions.Regex.Match(address.TrimStart(), @"^(\d+[A-Za-z]?)\b");
        return m.Success ? m.Groups[1].Value : null;
    }

    private async Task<GeocodeResult?> TryGeoapifyAsync(string address, CancellationToken ct)
    {
        if (_geoapifyKey.Length == 0) return null;
        var url = $"https://api.geoapify.com/v1/geocode/search?text={Uri.EscapeDataString(address)}&format=json&limit=1&lang=en&apiKey={_geoapifyKey}";
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var r = results[0];
            // Only trust house-level hits; a street or city centroid is worse than Census.
            var type = r.TryGetProperty("result_type", out var t) ? t.GetString() : null;
            if (type is not ("building" or "amenity")) return null;
            return new GeocodeResult
            {
                Lat = ParseCoord(r.GetProperty("lat")),
                Lng = ParseCoord(r.GetProperty("lon")),
                MatchedAddress = r.TryGetProperty("formatted", out var f) ? f.GetString() ?? "" : "",
                HouseNumber = r.TryGetProperty("housenumber", out var hn) ? hn.GetString() : null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Type-ahead suggestions. Uses Geoapify (US house-number autocomplete) when a key is set, else
    /// Photon, then Nominatim (which rate-limits typing to about one request a second).</summary>
    public async Task<List<SuggestResult>> SuggestAsync(string query, double? biasLat = null, double? biasLng = null, CancellationToken ct = default)
    {
        if (_geoapifyKey.Length > 0)
        {
            var geo = await GeoapifySuggestAsync(query, biasLat, biasLng, ct);
            if (geo.Count > 0) return geo;
        }
        var photon = await PhotonSuggestAsync(query, biasLat, biasLng, ct);
        if (photon.Count > 0) return photon;
        var results = await NominatimSuggestAsync(query, "us", biasLat, biasLng, ct);
        if (results.Count == 0) results = await NominatimSuggestAsync(query, null, biasLat, biasLng, ct);
        return results;
    }

    private async Task<List<SuggestResult>> GeoapifySuggestAsync(string query, double? biasLat, double? biasLng, CancellationToken ct)
    {
        var url = $"https://api.geoapify.com/v1/geocode/autocomplete?text={Uri.EscapeDataString(query)}&format=json&limit=8&lang=en&filter=countrycode:us&apiKey={_geoapifyKey}";
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return new();

            var list = new List<SuggestResult>();
            foreach (var el in results.EnumerateArray())
            {
                var label = el.TryGetProperty("formatted", out var f) ? f.GetString() ?? "" : "";
                if (label.Length == 0) continue;
                if (!el.TryGetProperty("lat", out var latEl) || !el.TryGetProperty("lon", out var lonEl)) continue;
                var hn = el.TryGetProperty("housenumber", out var h) ? h.GetString() : null;
                list.Add(new SuggestResult { Label = label, Lat = ParseCoord(latEl), Lng = ParseCoord(lonEl), HouseNumber = hn });
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    private async Task<List<SuggestResult>> PhotonSuggestAsync(string query, double? biasLat, double? biasLng, CancellationToken ct)
    {
        var bias = biasLat is not null && biasLng is not null
            ? $"&lat={biasLat.Value.ToString(CultureInfo.InvariantCulture)}&lon={biasLng.Value.ToString(CultureInfo.InvariantCulture)}"
            : "";
        var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(query)}&limit=15&lang=en{bias}";
        string? Prop(JsonElement p, string name) => p.TryGetProperty(name, out var v) ? v.GetString() : null;
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("features", out var features)) return new();

            var list = new List<SuggestResult>();
            foreach (var el in features.EnumerateArray())
            {
                var p = el.GetProperty("properties");
                if (Prop(p, "countrycode") != "US") continue;
                var hn = Prop(p, "housenumber");
                var street = Prop(p, "street") ?? Prop(p, "name");
                if (string.IsNullOrEmpty(street)) continue;
                var parts = new[] { hn is null ? street : $"{hn} {street}", Prop(p, "city") ?? Prop(p, "county"), Prop(p, "state") };
                var coords = el.GetProperty("geometry").GetProperty("coordinates");   // [lng, lat]
                var label = string.Join(", ", parts.Where(x => !string.IsNullOrEmpty(x)));
                if (list.Any(x => x.Label == label)) continue;
                list.Add(new SuggestResult { Label = label, Lat = coords[1].GetDouble(), Lng = coords[0].GetDouble(), HouseNumber = hn });
                if (list.Count == 8) break;
            }
            return list;
        }
        catch
        {
            return new();
        }
    }

    private async Task<List<SuggestResult>> NominatimSuggestAsync(string query, string? countryCodes, double? biasLat, double? biasLng, CancellationToken ct)
    {
        var cc = countryCodes is null ? "" : $"&countrycodes={countryCodes}";
        // Softly bias results toward the map center (~100 km viewbox) so nearby addresses rank first.
        var viewbox = (biasLat is not null && biasLng is not null)
            ? $"&viewbox={biasLng - 0.9},{biasLat - 0.45},{biasLng + 0.9},{biasLat + 0.45}"
            : "";
        var url = $"https://nominatim.openstreetmap.org/search?q={Uri.EscapeDataString(query)}&format=jsonv2&limit=8&addressdetails=1{cc}{viewbox}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("flood-risk-map/1.0 (stormwater demo)");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            var results = new List<SuggestResult>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("lat", out var latEl) || !el.TryGetProperty("lon", out var lonEl))
                    continue;

                var label = BuildLabel(el);
                if (label.Length == 0) continue;
                results.Add(new SuggestResult { Label = label, Lat = ParseCoord(latEl), Lng = ParseCoord(lonEl) });
            }
            return results;
        }
        catch
        {
            return new();
        }
    }

    private static double ParseCoord(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String => double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0.0,
            _ => 0.0,
        };

    private static string BuildLabel(JsonElement el)
    {
        if (el.TryGetProperty("address", out var addr))
        {
            var parts = new List<string>();
            if (addr.TryGetProperty("road", out var r) && r.ValueKind == JsonValueKind.String) parts.Add(r.GetString()!);
            if (addr.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.String) parts.Add(c.GetString()!);
            else if (addr.TryGetProperty("town", out var t) && t.ValueKind == JsonValueKind.String) parts.Add(t.GetString()!);
            if (addr.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String) parts.Add(s.GetString()!);
            var joined = string.Join(", ", parts);
            if (joined.Length > 0) return joined;
        }
        return el.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? "" : "";
    }

    private async Task<GeocodeResult?> TryCensusAsync(string address, CancellationToken ct)
    {
        var url = "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress" +
                  $"?address={Uri.EscapeDataString(address)}&benchmark=Public_AR_Current&format=json";
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
            if (!result.TryGetProperty("addressMatches", out var matches) || matches.GetArrayLength() == 0)
                return null;

            var m = matches[0];
            var coords = m.GetProperty("coordinates");
            return new GeocodeResult
            {
                Lat = coords.GetProperty("y").GetDouble(),
                Lng = coords.GetProperty("x").GetDouble(),
                MatchedAddress = m.TryGetProperty("matchedAddress", out var ma) ? ma.GetString() ?? "" : "",
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<GeocodeResult?> TryPhotonAsync(string address, CancellationToken ct)
    {
        var url = $"https://photon.komoot.io/api/?q={Uri.EscapeDataString(address)}&limit=1";
        try
        {
            var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

            if (!doc.RootElement.TryGetProperty("features", out var features) || features.GetArrayLength() == 0)
                return null;

            var coords = features[0].GetProperty("geometry").GetProperty("coordinates"); // [lng, lat]
            return new GeocodeResult
            {
                Lng = coords[0].GetDouble(),
                Lat = coords[1].GetDouble(),
            };
        }
        catch
        {
            return null;
        }
    }
}
