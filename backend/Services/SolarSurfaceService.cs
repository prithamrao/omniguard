using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Current ground/roof surface around a lot from Google's Solar API (photogrammetric DSM, ~0.5 m,
/// from recent aerial imagery). Public lidar can be decades old and predate a subdivision's grading;
/// this shows how the lot is graded now. Configured with "GoogleSolar:ApiKey".
/// </summary>
public sealed class SolarSurfaceService
{
    private const double RadiusM = 100;
    private const double PixelM = 0.5;
    private const int SmoothRadius = 3;          // ~3.5 m box: suppresses shrubs, fences and sensor noise
    private const double SampleSpacingM = 2;     // lot sampled every 2 m

    private readonly HttpClient _http;
    private readonly ILogger<SolarSurfaceService> _log;
    private readonly string _key;
    private readonly ConcurrentDictionary<string, LotDrainage?> _cache = new();

    public SolarSurfaceService(HttpClient http, IConfiguration config, ILogger<SolarSurfaceService> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(25);
        _log = log;
        _key = config["GoogleSolar:ApiKey"] ?? "";
    }

    public bool Enabled => _key.Length > 0;

    /// <summary>
    /// How the lot drains on the current surface: traces runoff from points all over the lot to where it
    /// leaves the lot, and splits it into the street side and the back. Null when unavailable.
    /// </summary>
    public async Task<LotDrainage?> AnalyzeLotAsync(Parcel parcel, Coord? street, CancellationToken ct = default)
    {
        if (!Enabled || parcel.Outline.Count < 3) return null;
        string key = $"{parcel.Lat:F6}|{parcel.Lng:F6}|{street?.Lat:F5}|{street?.Lng:F5}";
        if (_cache.TryGetValue(key, out var hit)) return hit;

        var surface = await FetchSurfaceAsync(parcel.Lat, parcel.Lng, ct);
        if (surface is null) return null;   // don't cache failures
        var result = Analyze(surface, parcel, street);
        _cache[key] = result;
        return result;
    }

    // ---------- download + GeoTIFF ----------

    private sealed record Surface(float[,] Z, double X0, double Y0, double Px, int Zone, bool South, string ImageryDate);

    private async Task<Surface?> FetchSurfaceAsync(double lat, double lng, CancellationToken ct)
    {
        var url = "https://solar.googleapis.com/v1/dataLayers:get" +
                  $"?location.latitude={F(lat)}&location.longitude={F(lng)}" +
                  $"&radiusMeters={F(RadiusM)}&view=DSM_LAYER&requiredQuality=LOW&pixelSizeMeters={F(PixelM)}&key={_key}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Solar dataLayers returned {Status}: {Body}", (int)resp.StatusCode, body.Length > 300 ? body[..300] : body);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("dsmUrl", out var dsmUrl)) return null;
            string date = root.TryGetProperty("imageryDate", out var d)
                ? $"{d.GetProperty("year").GetInt32()}-{d.GetProperty("month").GetInt32():D2}"
                : "";
            var tif = await _http.GetByteArrayAsync($"{dsmUrl.GetString()}&key={_key}", ct);
            return ParseGeoTiff(tif, date);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Solar surface fetch failed");
            return null;
        }
    }

    /// <summary>Single-band float32 GeoTIFF (tiled or stripped; none or Deflate) in UTM (EPSG 326xx/327xx).</summary>
    private static Surface? ParseGeoTiff(byte[] b, string date)
    {
        bool le = b[0] == 'I';
        ushort U16(int o) => le ? (ushort)(b[o] | b[o + 1] << 8) : (ushort)(b[o] << 8 | b[o + 1]);
        uint U32(int o) => le ? (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24) : (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        double F64(int o) { var s = b.AsSpan(o, 8).ToArray(); if (le != BitConverter.IsLittleEndian) Array.Reverse(s); return BitConverter.ToDouble(s); }
        float F32(byte[] src, int o) { var s = src.AsSpan(o, 4).ToArray(); if (le != BitConverter.IsLittleEndian) Array.Reverse(s); return BitConverter.ToSingle(s); }

        int ifd = (int)U32(4), n = U16(ifd);
        var tags = new Dictionary<int, (int type, int count, int pos)>();
        for (int i = 0; i < n; i++)
        {
            int e = ifd + 2 + i * 12;
            tags[U16(e)] = (U16(e + 2), (int)U32(e + 4), e + 8);
        }
        uint Val(int tag) { var (t, _, p) = tags[tag]; return t == 3 ? U16(p) : U32(p); }
        uint[] Arr(int tag)
        {
            var (t, c, p) = tags[tag];
            int size = (t == 3 ? 2 : 4) * c;
            int at = size <= 4 ? p : (int)U32(p);
            return Enumerable.Range(0, c).Select(k => t == 3 ? (uint)U16(at + 2 * k) : U32(at + 4 * k)).ToArray();
        }

        int w = (int)Val(256), h = (int)Val(257);
        int compression = tags.ContainsKey(259) ? (int)Val(259) : 1;
        if (tags.ContainsKey(258) && Val(258) != 32) return null;

        // Georeferencing: ModelTransformation, or PixelScale + Tiepoint.
        double px, x0, y0;
        if (tags.TryGetValue(34264, out var mt))
        {
            int at = (int)U32(mt.pos);
            px = F64(at); x0 = F64(at + 24); y0 = F64(at + 56);
        }
        else if (tags.TryGetValue(33550, out var ps) && tags.TryGetValue(33922, out var tp))
        {
            px = F64((int)U32(ps.pos));
            int at = (int)U32(tp.pos);
            x0 = F64(at + 24); y0 = F64(at + 32);
        }
        else return null;

        // EPSG from GeoKeys (3072 = ProjectedCSTypeGeoKey).
        int epsg = 0;
        if (tags.TryGetValue(34735, out var gk))
        {
            var keys = Arr(34735);
            for (int k = 4; k + 3 < keys.Length; k += 4) if (keys[k] == 3072) epsg = (int)keys[k + 3];
        }
        if (epsg is < 32601 or > 32760) return null;
        int zone = epsg % 100;
        bool south = epsg >= 32701;

        bool tiled = tags.ContainsKey(322);
        int tw = tiled ? (int)Val(322) : w, th = tiled ? (int)Val(323) : (tags.ContainsKey(278) ? (int)Val(278) : h);
        var offsets = Arr(tiled ? 324 : 273);
        var counts = Arr(tiled ? 325 : 279);
        int tilesAcross = (w + tw - 1) / tw;

        var z = new float[h, w];
        for (int t = 0; t < offsets.Length; t++)
        {
            byte[] chunk = b.AsSpan((int)offsets[t], (int)counts[t]).ToArray();
            if (compression is 8 or 32946)
            {
                using var zs = new ZLibStream(new MemoryStream(chunk), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                zs.CopyTo(ms);
                chunk = ms.ToArray();
            }
            else if (compression != 1) return null;
            int r0 = (t / tilesAcross) * th, c0 = (t % tilesAcross) * tw;
            for (int r = 0; r < th && r0 + r < h; r++)
            for (int c = 0; c < tw && c0 + c < w; c++)
            {
                int o = (r * tw + c) * 4;
                if (o + 4 <= chunk.Length) z[r0 + r, c0 + c] = F32(chunk, o);
            }
        }
        return new Surface(z, x0, y0, px, zone, south, date);
    }

    // ---------- drainage analysis ----------

    private static readonly int[] Dr = { -1, 1, 0, 0, -1, -1, 1, 1 };
    private static readonly int[] Dc = { 0, 0, -1, 1, -1, 1, -1, 1 };

    private LotDrainage Analyze(Surface s, Parcel parcel, Coord? street)
    {
        int h = s.Z.GetLength(0), w = s.Z.GetLength(1);
        var fill = Fill(Smooth(s.Z, SmoothRadius));

        (double r, double c) Pix(Coord p)
        {
            var (x, y) = Utm.Forward(p.Lat, p.Lng, s.Zone, s.South);
            return ((s.Y0 - y) / s.Px, (x - s.X0) / s.Px);
        }
        Coord Geo(double r, double c) => Utm.Inverse(s.X0 + (c + 0.5) * s.Px, s.Y0 - (r + 0.5) * s.Px, s.Zone, s.South);

        var poly = parcel.Outline.Select(Pix).ToList();
        bool InLot(double r, double c)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].r > r) != (poly[j].r > r) &&
                    c < (poly[j].c - poly[i].c) * (r - poly[i].r) / (poly[j].r - poly[i].r) + poly[i].c)
                    inside = !inside;
            return inside;
        }

        var center = Pix(new Coord(parcel.Lat, parcel.Lng));
        // "Front" points from the lot toward the street it faces.
        (double r, double c) front = street is { } st ? Pix(st) : (center.r - 1, center.c);
        double fr = front.r - center.r, fc = front.c - center.c, fl = Math.Max(1e-6, Math.Sqrt(fr * fr + fc * fc));
        fr /= fl; fc /= fl;

        List<(int r, int c)> Trace(int r, int c, int maxSteps)
        {
            var path = new List<(int, int)> { (r, c) };
            for (int k = 0; k < maxSteps; k++)
            {
                int br = -1, bc = -1; double best = 0;
                for (int d = 0; d < 8; d++)
                {
                    int nr = r + Dr[d], nc = c + Dc[d];
                    if (nr < 0 || nc < 0 || nr >= h || nc >= w) continue;
                    double sl = (fill[r, c] - fill[nr, nc]) / (d < 4 ? 1 : Math.Sqrt(2));
                    if (sl > best) { best = sl; br = nr; bc = nc; }
                }
                if (br < 0) break;
                r = br; c = bc;
                path.Add((r, c));
            }
            return path;
        }

        // Sample the lot; follow each sample until it leaves the lot; classify the exit.
        int step = Math.Max(1, (int)Math.Round(SampleSpacingM / s.Px));
        int frontCount = 0, backCount = 0;
        (int r, int c, double score)? bestFront = null, bestBack = null;
        for (int r = 0; r < h; r += step)
        for (int c = 0; c < w; c += step)
        {
            if (!InLot(r, c)) continue;
            var p = Trace(r, c, 600);
            var exit = p.FirstOrDefault(q => !InLot(q.r, q.c), p[^1]);
            double dot = (exit.r - center.r) * fr + (exit.c - center.c) * fc;
            double near = Math.Abs(r - center.r) + Math.Abs(c - center.c);
            if (dot >= 0) { frontCount++; if (bestFront is null || near < bestFront.Value.score) bestFront = (r, c, near); }
            else { backCount++; if (bestBack is null || near < bestBack.Value.score) bestBack = (r, c, near); }
        }
        int total = frontCount + backCount;
        if (total == 0) return new LotDrainage { ImageryDate = s.ImageryDate };

        List<Coord> GeoPath(List<(int r, int c)> p) => p.Where((_, i) => i % 4 == 0 || i == p.Count - 1).Select(q => Geo(q.r, q.c)).ToList();
        var frontPath = bestFront is { } bf ? GeoPath(Trace(bf.r, bf.c, 300)) : new();
        var backPath = bestBack is { } bb ? GeoPath(Trace(bb.r, bb.c, 1200)) : new();
        return new LotDrainage
        {
            ImageryDate = s.ImageryDate,
            FrontSharePct = Math.Round(100.0 * frontCount / total),
            BackSharePct = Math.Round(100.0 * backCount / total),
            FrontPath = frontPath,
            BackPath = backPath,
        };
    }

    private static double[,] Smooth(float[,] src, int radius)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var sat = new double[h + 1, w + 1];
        for (int i = 0; i < h; i++)
        for (int j = 0; j < w; j++)
            sat[i + 1, j + 1] = src[i, j] + sat[i, j + 1] + sat[i + 1, j] - sat[i, j];
        var o = new double[h, w];
        for (int i = 0; i < h; i++)
        for (int j = 0; j < w; j++)
        {
            int i0 = Math.Max(0, i - radius), i1 = Math.Min(h, i + radius + 1), j0 = Math.Max(0, j - radius), j1 = Math.Min(w, j + radius + 1);
            o[i, j] = (sat[i1, j1] - sat[i0, j1] - sat[i1, j0] + sat[i0, j0]) / ((i1 - i0) * (j1 - j0));
        }
        return o;
    }

    private static double[,] Fill(double[,] z)
    {
        int h = z.GetLength(0), w = z.GetLength(1);
        var f = (double[,])z.Clone();
        var closed = new bool[h, w];
        var pq = new PriorityQueue<(int, int), double>();
        for (int i = 0; i < h; i++)
        for (int j = 0; j < w; j++)
            if (i == 0 || j == 0 || i == h - 1 || j == w - 1) { closed[i, j] = true; pq.Enqueue((i, j), f[i, j]); }
        while (pq.TryDequeue(out var cur, out _))
        {
            var (i, j) = cur;
            for (int d = 0; d < 8; d++)
            {
                int ni = i + Dr[d], nj = j + Dc[d];
                if (ni < 0 || nj < 0 || ni >= h || nj >= w || closed[ni, nj]) continue;
                closed[ni, nj] = true;
                f[ni, nj] = Math.Max(f[ni, nj], f[i, j] + 1e-5);
                pq.Enqueue((ni, nj), f[ni, nj]);
            }
        }
        return f;
    }

    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);
}

/// <summary>WGS84 ⇄ UTM (standard transverse Mercator series, accurate to well under a meter).</summary>
internal static class Utm
{
    private const double A = 6378137.0, Fl = 1 / 298.257223563, K0 = 0.9996;
    private static readonly double E2 = Fl * (2 - Fl), Ep2 = E2 / (1 - E2);

    public static (double x, double y) Forward(double lat, double lon, int zone, bool south)
    {
        double lon0 = ((zone - 1) * 6 - 180 + 3) * Math.PI / 180, phi = lat * Math.PI / 180, lam = lon * Math.PI / 180;
        double n = A / Math.Sqrt(1 - E2 * Math.Sin(phi) * Math.Sin(phi));
        double t = Math.Tan(phi) * Math.Tan(phi), c = Ep2 * Math.Cos(phi) * Math.Cos(phi), a = Math.Cos(phi) * (lam - lon0);
        double m = Meridian(phi);
        double x = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6 + (5 - 18 * t + t * t + 72 * c - 58 * Ep2) * Math.Pow(a, 5) / 120) + 500000;
        double y = K0 * (m + n * Math.Tan(phi) * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24 + (61 - 58 * t + t * t + 600 * c - 330 * Ep2) * Math.Pow(a, 6) / 720));
        return (x, south ? y + 10_000_000 : y);
    }

    public static Coord Inverse(double x, double y, int zone, bool south)
    {
        double lon0 = ((zone - 1) * 6 - 180 + 3) * Math.PI / 180;
        x -= 500000;
        if (south) y -= 10_000_000;
        double m = y / K0, mu = m / (A * (1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * E2 * E2 * E2 / 256));
        double e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        double phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu) + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                      + (151 * Math.Pow(e1, 3) / 96) * Math.Sin(6 * mu) + (1097 * Math.Pow(e1, 4) / 512) * Math.Sin(8 * mu);
        double n1 = A / Math.Sqrt(1 - E2 * Math.Sin(phi1) * Math.Sin(phi1)), t1 = Math.Tan(phi1) * Math.Tan(phi1);
        double c1 = Ep2 * Math.Cos(phi1) * Math.Cos(phi1), r1 = A * (1 - E2) / Math.Pow(1 - E2 * Math.Sin(phi1) * Math.Sin(phi1), 1.5), d = x / (n1 * K0);
        double lat = phi1 - (n1 * Math.Tan(phi1) / r1) * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * Ep2) * Math.Pow(d, 4) / 24
                     + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * Ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        double lon = lon0 + (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * Ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / Math.Cos(phi1);
        return new Coord(lat * 180 / Math.PI, lon * 180 / Math.PI);
    }

    private static double Meridian(double phi)
        => A * ((1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * E2 * E2 * E2 / 256) * phi
                - (3 * E2 / 8 + 3 * E2 * E2 / 32 + 45 * E2 * E2 * E2 / 1024) * Math.Sin(2 * phi)
                + (15 * E2 * E2 / 256 + 45 * E2 * E2 * E2 / 1024) * Math.Sin(4 * phi)
                - (35 * E2 * E2 * E2 / 3072) * Math.Sin(6 * phi));
}
