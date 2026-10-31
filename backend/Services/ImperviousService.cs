using System.Text;

namespace FloodRisk.Api.Services;

/// <summary>Imperviousness grid + the raw building points (for snapping the marker).</summary>
public sealed class ImperviousResult
{
    public double[,]? Grid { get; init; }
    public List<(double lng, double lat, string? number)> Buildings { get; init; } = new();
    /// <summary>Cells covered by mapped water (ponds, lakes, streams) — where runoff visibly collects.</summary>
    public bool[,]? Water { get; init; }
    /// <summary>Cells on a street (curb and gutter): suburban runoff that reaches one goes into the storm drains.</summary>
    public bool[,]? Street { get; init; }
    /// <summary>Street centerlines (lng, lat), for exact distances to the street a lot faces.</summary>
    public List<List<(double lng, double lat)>> StreetLines { get; init; } = new();
}

/// <summary>
/// Derives an imperviousness grid (0-100) on demand from OpenFreeMap vector tiles:
/// built-up land-use polygons, building points, and roads. Includes a minimal
/// Mapbox Vector Tile (MVT) decoder, so no external geo dependency is required.
/// </summary>
public sealed class ImperviousService
{
    private readonly HttpClient _http;
    private const int Zoom = 14;
    private const double SizeKm = 8.0;
    private const string TileUrl = "https://tiles.openfreemap.org/planet/20261025_080012_pt/{z}/{x}/{y}.pbf";
    // Public streets with curbs and storm drains (not "service": driveways, parking aisles, alleys).
    private static readonly HashSet<string> StreetClasses = new() { "motorway", "trunk", "primary", "secondary", "tertiary", "minor" };
    private static readonly HashSet<string> BuiltupClasses = new() { "residential", "commercial", "industrial", "retail", "school" };

    public ImperviousService(HttpClient http) => _http = http;

    public async Task<ImperviousResult?> FetchAsync(double centerLat, double centerLng, int gridSize, CancellationToken ct = default)
    {
        double halfLat = (SizeKm / 2.0) / 111.32;
        double halfLng = (SizeKm / 2.0) / (111.32 * Math.Cos(centerLat * Math.PI / 180));
        double south = centerLat - halfLat, north = centerLat + halfLat;
        double west = centerLng - halfLng, east = centerLng + halfLng;

        int x0 = LonToTileX(west), x1 = LonToTileX(east);
        int y0 = LatToTileY(north), y1 = LatToTileY(south);

        var builtup = new List<List<(double lng, double lat)>>();
        var buildings = new List<(double lng, double lat, string? number)>();
        var roads = new List<List<(double lng, double lat)>>();
        var streets = new List<List<(double lng, double lat)>>();
        var water = new List<List<(double lng, double lat)>>();

        // Collect tile jobs, fetch them all in parallel, then decode sequentially.
        var jobs = new List<(int tx, int ty, double lon0, double lon1, double lat0, double lat1)>();
        for (int tx = x0; tx <= x1; tx++)
        {
            double lon0 = tx / Math.Pow(2, Zoom) * 360.0 - 180.0;
            double lon1 = (tx + 1) / Math.Pow(2, Zoom) * 360.0 - 180.0;
            for (int ty = y0; ty <= y1; ty++)
                jobs.Add((tx, ty, lon0, lon1, TileLat(ty), TileLat(ty + 1)));
        }

        var tiles = await Task.WhenAll(jobs.Select(j => FetchTileAsync(j.tx, j.ty, ct)));

        for (int k = 0; k < jobs.Count; k++)
        {
            if (tiles[k] is null) continue;
            var j = jobs[k];
            DecodeTile(tiles[k]!, j.lon0, j.lon1, j.lat0, j.lat1, builtup, buildings, roads, water, streets);
        }

        if (builtup.Count == 0 && buildings.Count == 0 && roads.Count == 0) return null;

        double pixelLat = -(north - south) / gridSize;
        double pixelLng = (east - west) / gridSize;
        var grid = new double[gridSize, gridSize];

        // Built-up land-use polygons.
        foreach (var poly in builtup)
        {
            double minLng = double.MaxValue, maxLng = double.MinValue, minLat = double.MaxValue, maxLat = double.MinValue;
            foreach (var p in poly)
            {
                if (p.lng < minLng) minLng = p.lng; if (p.lng > maxLng) maxLng = p.lng;
                if (p.lat < minLat) minLat = p.lat; if (p.lat > maxLat) maxLat = p.lat;
            }
            int j0 = Math.Max(0, (int)Math.Floor((minLng - west) / pixelLng));
            int j1 = Math.Min(gridSize - 1, (int)Math.Floor((maxLng - west) / pixelLng));
            int i0 = Math.Max(0, (int)Math.Floor((maxLat - north) / pixelLat));
            int i1 = Math.Min(gridSize - 1, (int)Math.Floor((minLat - north) / pixelLat));
            for (int i = i0; i <= i1; i++)
            {
                double clat = north + (i + 0.5) * pixelLat;
                for (int j = j0; j <= j1; j++)
                {
                    double clng = west + (j + 0.5) * pixelLng;
                    if (PointInPolygon(clng, clat, poly)) grid[i, j] = 55;
                }
            }
        }

        // Buildings (points).
        foreach (var b in buildings)
        {
            int j0 = Math.Max(0, (int)Math.Floor((b.lng - 0.00012 - west) / pixelLng));
            int j1 = Math.Min(gridSize - 1, (int)Math.Floor((b.lng + 0.00012 - west) / pixelLng));
            int i0 = Math.Max(0, (int)Math.Floor((b.lat + 0.00012 - north) / pixelLat));
            int i1 = Math.Min(gridSize - 1, (int)Math.Floor((b.lat - 0.00012 - north) / pixelLat));
            for (int i = i0; i <= i1; i++)
            {
                double clat = north + (i + 0.5) * pixelLat;
                for (int j = j0; j <= j1; j++)
                {
                    double clng = west + (j + 0.5) * pixelLng;
                    double dlng = clng - b.lng, dlat = clat - b.lat;
                    if (dlng * dlng + dlat * dlat < 0.00012 * 0.00012) grid[i, j] = 95;
                }
            }
        }

        // Roads (lines) — iterate individual segments for small bounding boxes.
        foreach (var line in roads)
        {
            for (int k = 0; k + 1 < line.Count; k++)
            {
                var a = line[k];
                var b = line[k + 1];
                double minLng = Math.Min(a.lng, b.lng), maxLng = Math.Max(a.lng, b.lng);
                double minLat = Math.Min(a.lat, b.lat), maxLat = Math.Max(a.lat, b.lat);
                int j0 = Math.Max(0, (int)Math.Floor((minLng - 0.0001 - west) / pixelLng));
                int j1 = Math.Min(gridSize - 1, (int)Math.Floor((maxLng + 0.0001 - west) / pixelLng));
                int i0 = Math.Max(0, (int)Math.Floor((maxLat + 0.0001 - north) / pixelLat));
                int i1 = Math.Min(gridSize - 1, (int)Math.Floor((minLat - 0.0001 - north) / pixelLat));
                for (int i = i0; i <= i1; i++)
                {
                    double clat = north + (i + 0.5) * pixelLat;
                    for (int j = j0; j <= j1; j++)
                    {
                        double clng = west + (j + 0.5) * pixelLng;
                        if (DistToSegment(clng, clat, a, b) < 0.0001) grid[i, j] = 95;
                    }
                }
            }
        }
        // Mapped water bodies.
        var waterGrid = new bool[gridSize, gridSize];
        foreach (var poly in water)
        {
            double minLng = double.MaxValue, maxLng = double.MinValue, minLat = double.MaxValue, maxLat = double.MinValue;
            foreach (var p in poly)
            {
                if (p.lng < minLng) minLng = p.lng; if (p.lng > maxLng) maxLng = p.lng;
                if (p.lat < minLat) minLat = p.lat; if (p.lat > maxLat) maxLat = p.lat;
            }
            int j0 = Math.Max(0, (int)Math.Floor((minLng - west) / pixelLng));
            int j1 = Math.Min(gridSize - 1, (int)Math.Floor((maxLng - west) / pixelLng));
            int i0 = Math.Max(0, (int)Math.Floor((maxLat - north) / pixelLat));
            int i1 = Math.Min(gridSize - 1, (int)Math.Floor((minLat - north) / pixelLat));
            for (int i = i0; i <= i1; i++)
            {
                double clat = north + (i + 0.5) * pixelLat;
                for (int j = j0; j <= j1; j++)
                {
                    double clng = west + (j + 0.5) * pixelLng;
                    if (PointInPolygon(clng, clat, poly)) waterGrid[i, j] = true;
                }
            }
        }
        // Streets: cells whose center is within ~half a cell of the centerline, so the street is a continuous band.
        var streetGrid = new bool[gridSize, gridSize];
        double reach = Math.Abs(pixelLat) * 0.55;
        foreach (var line in streets)
        {
            for (int k = 0; k + 1 < line.Count; k++)
            {
                var a = line[k];
                var b = line[k + 1];
                int j0 = Math.Max(0, (int)Math.Floor((Math.Min(a.lng, b.lng) - reach - west) / pixelLng));
                int j1 = Math.Min(gridSize - 1, (int)Math.Floor((Math.Max(a.lng, b.lng) + reach - west) / pixelLng));
                int i0 = Math.Max(0, (int)Math.Floor((Math.Max(a.lat, b.lat) + reach - north) / pixelLat));
                int i1 = Math.Min(gridSize - 1, (int)Math.Floor((Math.Min(a.lat, b.lat) - reach - north) / pixelLat));
                for (int i = i0; i <= i1; i++)
                {
                    double clat = north + (i + 0.5) * pixelLat;
                    for (int j = j0; j <= j1; j++)
                    {
                        double clng = west + (j + 0.5) * pixelLng;
                        if (DistToSegment(clng, clat, a, b) < reach) streetGrid[i, j] = true;
                    }
                }
            }
        }
        return new ImperviousResult { Grid = grid, Buildings = buildings, Water = waterGrid, Street = streetGrid, StreetLines = streets };
    }

    private async Task<byte[]?> FetchTileAsync(int tx, int ty, CancellationToken ct)
    {
        var url = TileUrl.Replace("{z}", Zoom.ToString()).Replace("{x}", tx.ToString()).Replace("{y}", ty.ToString());
        try { return await _http.GetByteArrayAsync(url, ct); }
        catch { return null; }
    }

    // ---------- MVT decoding ----------

    private static void DecodeTile(byte[] data, double lon0, double lon1, double lat0, double lat1,
        List<List<(double, double)>> builtup, List<(double, double, string?)> buildings, List<List<(double, double)>> roads,
        List<List<(double, double)>> water, List<List<(double, double)>> streets)
    {
        // Tile: field 3 = Layer (repeated, wire 2)
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadField(data, ref pos, out int field, out int wire, out byte[]? payload, out _)) break;
            if (field == 3 && wire == 2 && payload != null)
                DecodeLayer(payload, lon0, lon1, lat0, lat1, builtup, buildings, roads, water, streets);
        }
    }

    private static void DecodeLayer(byte[] data, double lon0, double lon1, double lat0, double lat1,
        List<List<(double, double)>> builtup, List<(double, double, string?)> buildings, List<List<(double, double)>> roads,
        List<List<(double, double)>> water, List<List<(double, double)>> streets)
    {
        string name = "";
        var keys = new List<string>();
        var values = new List<string>();
        var featurePayloads = new List<(int type, byte[] tags, byte[] geom)>();

        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadField(data, ref pos, out int field, out int wire, out byte[]? payload, out _)) break;
            if (field == 1 && wire == 2 && payload != null) name = Encoding.UTF8.GetString(payload);
            else if (field == 2 && wire == 2 && payload != null)
            {
                var f = ParseFeature(payload);
                featurePayloads.Add(f);
            }
            else if (field == 3 && wire == 2 && payload != null) keys.Add(Encoding.UTF8.GetString(payload));
            else if (field == 4 && wire == 2 && payload != null) values.Add(ParseValue(payload));
        }

        foreach (var (type, tags, geom) in featurePayloads)
        {
            var coords = DecodeGeometry(geom, type);
            if (coords.Count == 0) continue;

            // Convert tile-local [0, extent] to lon/lat.
            double xf = (lon1 - lon0) / 4096.0;
            double yf = (lat1 - lat0) / 4096.0;

            if (name == "landuse")
            {
                var cls = GetProperty(tags, keys, values, "class");
                if (cls != null && BuiltupClasses.Contains(cls) && coords.Count >= 3)
                    builtup.Add(ToLonLat(coords, lon0, lat0, xf, yf));
            }
            else if (name == "water")
            {
                foreach (var part in DecodeParts(geom))
                    if (part.Count >= 3) water.Add(ToLonLat(part, lon0, lat0, xf, yf));
            }
            else if (name == "housenumber")
            {
                var num = GetProperty(tags, keys, values, "housenumber");
                foreach (var c in coords) buildings.Add((lon0 + c.x * xf, lat0 + c.y * yf, num));
            }
            else if (name == "transportation")
            {
                var cls = GetProperty(tags, keys, values, "class");
                foreach (var part in DecodeParts(geom))
                {
                    if (part.Count < 2) continue;
                    var line = ToLonLat(part, lon0, lat0, xf, yf);
                    roads.Add(line);
                    if (cls != null && StreetClasses.Contains(cls)) streets.Add(line);
                }
            }
        }
    }

    private static (int type, byte[] tags, byte[] geom) ParseFeature(byte[] data)
    {
        int type = 0; byte[] tags = Array.Empty<byte>(); byte[] geom = Array.Empty<byte>();
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadField(data, ref pos, out int field, out int wire, out byte[]? payload, out ulong varValue)) break;
            if (field == 2 && wire == 2 && payload != null) tags = payload;
            else if (field == 3 && wire == 0) type = (int)varValue;
            else if (field == 4 && wire == 2 && payload != null) geom = payload;
        }
        return (type, tags, geom);
    }

    /// <summary>Decodes an MVT geometry into tile-local points.</summary>
    private static List<(int x, int y)> DecodeGeometry(byte[] data, int geomType)
    {
        var result = new List<(int, int)>();
        var values = ReadPackedVarints(data);
        int i = 0, cx = 0, cy = 0;
        while (i < values.Count)
        {
            uint cmd = values[i++];
            int cmdId = (int)(cmd & 0x7);
            int count = (int)(cmd >> 3);
            if (cmdId == 1 || cmdId == 2) // MoveTo or LineTo
            {
                for (int k = 0; k < count && i + 1 < values.Count; k++)
                {
                    cx += ZigZag(values[i++]);
                    cy += ZigZag(values[i++]);
                    result.Add((cx, cy));
                }
            }
            else if (cmdId == 7) // ClosePath
            {
                // no params
            }
        }
        return result;
    }

    /// <summary>
    /// Like DecodeGeometry, but keeps each part separate (a new part starts at every MoveTo).
    /// Multi-part roads and water bodies must not be joined end to end, or the jump between
    /// parts becomes a fake segment cutting across lots.
    /// </summary>
    private static List<List<(int x, int y)>> DecodeParts(byte[] data)
    {
        var parts = new List<List<(int x, int y)>>();
        var values = ReadPackedVarints(data);
        int i = 0, cx = 0, cy = 0;
        List<(int x, int y)>? current = null;
        while (i < values.Count)
        {
            uint cmd = values[i++];
            int cmdId = (int)(cmd & 0x7);
            int count = (int)(cmd >> 3);
            if (cmdId == 1 || cmdId == 2)
            {
                for (int k = 0; k < count && i + 1 < values.Count; k++)
                {
                    cx += ZigZag(values[i++]);
                    cy += ZigZag(values[i++]);
                    if (cmdId == 1) { current = new List<(int x, int y)>(); parts.Add(current); }
                    current?.Add((cx, cy));
                }
            }
        }
        return parts;
    }

    private static string? GetProperty(byte[] tags, List<string> keys, List<string> values, string want)
    {
        var tagValues = ReadPackedVarints(tags);
        for (int i = 0; i + 1 < tagValues.Count; i += 2)
        {
            int ki = (int)tagValues[i], vi = (int)tagValues[i + 1];
            if (ki >= 0 && ki < keys.Count && keys[ki] == want && vi >= 0 && vi < values.Count)
                return values[vi];
        }
        return null;
    }

    private static string ParseValue(byte[] data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadField(data, ref pos, out int field, out int wire, out byte[]? payload, out ulong varValue)) break;
            if (field == 1 && wire == 2 && payload != null) return Encoding.UTF8.GetString(payload);
            if (wire == 0) return varValue.ToString();
        }
        return "";
    }

    // ---------- Protobuf primitives ----------

    private static bool TryReadField(byte[] data, ref int pos, out int field, out int wire, out byte[]? payload, out ulong varValue)
    {
        field = 0; wire = 0; payload = null; varValue = 0;
        if (pos >= data.Length) return false;
        ulong tag = ReadVarint(data, pos, out int np);
        pos = np;
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x7);
        if (wire == 0)
        {
            varValue = ReadVarint(data, pos, out np);
            pos = np;
        }
        else if (wire == 2)
        {
            ulong len = ReadVarint(data, pos, out np);
            pos = np;
            int l = (int)len;
            payload = data.AsSpan(pos, Math.Min(l, data.Length - pos)).ToArray();
            pos += l;
        }
        else if (wire == 5) { pos += 4; }
        else if (wire == 1) { pos += 8; }
        else return false;
        return true;
    }

    private static ulong ReadVarint(byte[] data, int pos, out int next)
    {
        ulong result = 0; int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        next = pos;
        return result;
    }

    private static List<uint> ReadPackedVarints(byte[] data)
    {
        var list = new List<uint>();
        int pos = 0;
        while (pos < data.Length)
            list.Add((uint)ReadVarint(data, pos, out pos));
        return list;
    }

    private static int ZigZag(uint n) => (int)((n >> 1) ^ (uint)-(int)(n & 1));

    // ---------- Rasterization helpers ----------

    private static List<(double lng, double lat)> ToLonLat(List<(int x, int y)> coords, double lon0, double lat0, double xf, double yf)
    {
        var r = new List<(double lng, double lat)>(coords.Count);
        foreach (var c in coords) r.Add((lon0 + c.x * xf, lat0 + c.y * yf));
        return r;
    }

    private static bool PointInAnyPolygon(double lng, double lat, List<List<(double lng, double lat)>> polys)
    {
        foreach (var poly in polys)
            if (PointInPolygon(lng, lat, poly)) return true;
        return false;
    }

    private static bool PointInPolygon(double lng, double lat, List<(double lng, double lat)> poly)
    {
        bool inside = false;
        int n = poly.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = poly[i].lng, yi = poly[i].lat;
            double xj = poly[j].lng, yj = poly[j].lat;
            if ((yi > lat) != (yj > lat) && lng < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static bool NearAnyPoint(double lng, double lat, List<(double lng, double lat)> pts, double r)
    {
        foreach (var p in pts)
        {
            double dLng = p.lng - lng, dLat = p.lat - lat;
            if (dLng * dLng + dLat * dLat < r * r) return true;
        }
        return false;
    }

    private static bool NearAnyLine(double lng, double lat, List<List<(double lng, double lat)>> lines, double r)
    {
        foreach (var line in lines)
            for (int i = 0; i + 1 < line.Count; i++)
                if (DistToSegment(lng, lat, line[i], line[i + 1]) < r) return true;
        return false;
    }

    private static double DistToSegment(double px, double py, (double lng, double lat) a, (double lng, double lat) b)
    {
        double dx = b.lng - a.lng, dy = b.lat - a.lat;
        double lenSq = dx * dx + dy * dy;
        double t = lenSq == 0 ? 0 : Math.Clamp(((px - a.lng) * dx + (py - a.lat) * dy) / lenSq, 0, 1);
        double cx = a.lng + t * dx, cy = a.lat + t * dy;
        double ddx = px - cx, ddy = py - cy;
        return Math.Sqrt(ddx * ddx + ddy * ddy);
    }

    private static int LonToTileX(double lng) => (int)Math.Floor((lng + 180) / 360 * Math.Pow(2, Zoom));
    private static int LatToTileY(double lat) => (int)Math.Floor((1 - Math.Asinh(Math.Tan(lat * Math.PI / 180)) / Math.PI) / 2 * Math.Pow(2, Zoom));
    private static double TileLat(int y)
    {
        double n = Math.PI * (1 - 2.0 * y / Math.Pow(2, Zoom));
        return Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI;
    }
}
