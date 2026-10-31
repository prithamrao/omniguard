using System.Globalization;
using StbImageSharp;

namespace FloodRisk.Api.Services;

public sealed class DemResult
{
    public required double[,] Dem { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required double OriginLat { get; init; }
    public required double OriginLng { get; init; }
    public required double PixelLat { get; init; }
    public required double PixelLng { get; init; }
}

/// <summary>
/// Fetches elevation on-demand for any location (global) from the free AWS "terrarium"
/// terrain tiles (SRTM, ~30 m) and assembles a regular lon/lat grid around a center.
/// </summary>
public sealed class DemService
{
    private readonly HttpClient _http;
    private const int Zoom = 13;        // ~2.4 km/tile, ~9.4 m/pixel sampling
    private const int TileSize = 256;
    private const double SizeKm = 8.0;  // study-area extent (~8 km square)
    private const int GridSize = 400;   // output grid (~20 m cells)
    private const int UsgsGridSize = 600; // 3DEP output grid (~13 m cells)

    public DemService(HttpClient http) => _http = http;

    public async Task<DemResult?> FetchAsync(double centerLat, double centerLng, CancellationToken ct = default)
    {
        double halfLat = (SizeKm / 2.0) / 111.32;
        double halfLng = (SizeKm / 2.0) / (111.32 * Math.Cos(centerLat * Math.PI / 180));
        double south = centerLat - halfLat, north = centerLat + halfLat;
        double west = centerLng - halfLng, east = centerLng + halfLng;

        int x0 = LonToTileX(west), x1 = LonToTileX(east);
        int y0 = LatToTileY(north), y1 = LatToTileY(south);

        // Fetch all needed tiles in parallel.
        var jobs = new List<(int tx, int ty)>();
        for (int tx = x0; tx <= x1; tx++)
            for (int ty = y0; ty <= y1; ty++)
                jobs.Add((tx, ty));

        var tiles = new Dictionary<(int, int), (int w, int h, byte[] rgb)>();
        await Task.WhenAll(jobs.Select(async j =>
        {
            try
            {
                var url = $"https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{Zoom}/{j.tx}/{j.ty}.png";
                var bytes = await _http.GetByteArrayAsync(url, ct);
                var img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlue);
                lock (tiles) tiles[j] = (img.Width, img.Height, img.Data);
            }
            catch
            {
                // Tile beyond coverage (e.g. ocean edge) — leave unset.
            }
        }));

        if (tiles.Count == 0) return null;

        double pixelLat = -(north - south) / GridSize;
        double pixelLng = (east - west) / GridSize;
        var dem = new double[GridSize, GridSize];
        double n = Math.Pow(2, Zoom);

        for (int i = 0; i < GridSize; i++)
        {
            double lat = north + (i + 0.5) * pixelLat;
            double yf = (1 - Math.Asinh(Math.Tan(lat * Math.PI / 180)) / Math.PI) / 2 * n;
            int ty = (int)Math.Floor(yf);
            int py = (int)((yf - ty) * TileSize);
            if (py >= TileSize) py = TileSize - 1;

            for (int j = 0; j < GridSize; j++)
            {
                double lng = west + (j + 0.5) * pixelLng;
                double xf = (lng + 180) / 360 * n;
                int tx = (int)Math.Floor(xf);
                int px = (int)((xf - tx) * TileSize);
                if (px >= TileSize) px = TileSize - 1;

                if (tiles.TryGetValue((tx, ty), out var tile))
                {
                    int idx = (py * tile.w + px) * 3;
                    byte r = tile.rgb[idx], g = tile.rgb[idx + 1], b = tile.rgb[idx + 2];
                    // Terrarium encoding: elevation = (R*256 + G + B/256) - 32768
                    dem[i, j] = (r * 256 + g + b / 256.0) - 32768;
                }
                else
                {
                    dem[i, j] = 0;
                }
            }
        }

        return new DemResult
        {
            Dem = dem,
            Rows = GridSize,
            Cols = GridSize,
            OriginLat = north,
            OriginLng = west,
            PixelLat = pixelLat,
            PixelLng = pixelLng,
        };
    }

    private static int LonToTileX(double lng) => (int)Math.Floor((lng + 180) / 360 * Math.Pow(2, Zoom));
    private static int LatToTileY(double lat) => (int)Math.Floor((1 - Math.Asinh(Math.Tan(lat * Math.PI / 180)) / Math.PI) / 2 * Math.Pow(2, Zoom));

    /// <summary>
    /// Year of the newest public elevation survey covering a point. Checks the USGS product catalog
    /// (TNM Access: 1 m DEMs and lidar point clouds, year taken from the project name, e.g.
    /// "NC_HurricaneFlorence_2020"), then the 3DEP lidar index, which misses some newer projects.
    /// Lets the app tell when a home was built after the terrain was surveyed.
    /// </summary>
    public async Task<int?> FetchSurveyYearAsync(double lat, double lng, CancellationToken ct = default)
    {
        var years = await Task.WhenAll(
            CatalogYearAsync(lat, lng, "Digital Elevation Model (DEM) 1 meter", ct),
            CatalogYearAsync(lat, lng, "Lidar Point Cloud (LPC)", ct),
            IndexYearAsync(lat, lng, ct));
        return years.Max();
    }

    private static readonly System.Text.RegularExpressions.Regex YearInTitle = new(@"(?<![0-9])(19[5-9][0-9]|20[0-4][0-9])(?![0-9])");

    private async Task<int?> CatalogYearAsync(double lat, double lng, string dataset, CancellationToken ct)
    {
        string F(double v) => v.ToString(CultureInfo.InvariantCulture);
        var url = "https://tnmaccess.nationalmap.gov/api/v1/products" +
                  $"?bbox={F(lng - 0.0005)},{F(lat - 0.0005)},{F(lng + 0.0005)},{F(lat + 0.0005)}" +
                  $"&datasets={Uri.EscapeDataString(dataset)}&max=50";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var doc = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync(url, cts.Token));
            int? best = null;
            foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                foreach (System.Text.RegularExpressions.Match m in YearInTitle.Matches(title))
                {
                    int year = int.Parse(m.Value, CultureInfo.InvariantCulture);
                    if (best is null || year > best) best = year;
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> IndexYearAsync(double lat, double lng, CancellationToken ct)
    {
        var url = "https://index.nationalmap.gov/arcgis/rest/services/3DEPElevationIndex/MapServer/8/query" +
                  $"?geometry={lng.ToString(CultureInfo.InvariantCulture)},{lat.ToString(CultureInfo.InvariantCulture)}" +
                  "&geometryType=esriGeometryPoint&inSR=4326&spatialRel=esriSpatialRelIntersects" +
                  "&outFields=collect_end&returnGeometry=false&f=json";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await _http.GetStringAsync(url, ct));
            int? best = null;
            foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
            {
                if (f.GetProperty("attributes").TryGetProperty("collect_end", out var ce) && ce.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    int year = DateTimeOffset.FromUnixTimeMilliseconds(ce.GetInt64()).Year;
                    if (best is null || year > best) best = year;
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True for locations covered by USGS 3DEP (US + Alaska + Hawaii).</summary>
    public static bool IsUs(double lat, double lng)
    {
        if (lat >= 24 && lat <= 50 && lng >= -125 && lng <= -66) return true;   // contiguous US
        if (lat >= 51 && lat <= 72 && lng >= -168 && lng <= -130) return true;  // Alaska
        if (lat >= 18 && lat <= 23 && lng >= -160 && lng <= -154) return true;  // Hawaii
        return false;
    }

    /// <summary>Output grid size for a location (3DEP=800, SRTM=400).</summary>
    public static int GetGridSize(double lat, double lng) => IsUs(lat, lng) ? UsgsGridSize : GridSize;

    /// <summary>Fetches a USGS 3DEP (10 m, bare-earth) DEM as an uncompressed float32 GeoTIFF.</summary>
    public async Task<DemResult?> FetchUsgsAsync(double centerLat, double centerLng, CancellationToken ct = default)
    {
        double halfLat = (SizeKm / 2.0) / 111.32;
        double halfLng = (SizeKm / 2.0) / (111.32 * Math.Cos(centerLat * Math.PI / 180));
        double south = centerLat - halfLat, north = centerLat + halfLat;
        double west = centerLng - halfLng, east = centerLng + halfLng;

        var url = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage" +
                  $"?bbox={west.ToString(CultureInfo.InvariantCulture)},{south.ToString(CultureInfo.InvariantCulture)}," +
                  $"{east.ToString(CultureInfo.InvariantCulture)},{north.ToString(CultureInfo.InvariantCulture)}" +
                  "&bboxSR=4326" +
                  $"&size={UsgsGridSize},{UsgsGridSize}" +
                  "&imageSR=4326" +
                  "&format=tiff" +
                  "&pixelType=F32" +
                  "&f=image";

        // USGS sometimes fails the first request for an area, then answers the next one.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var bytes = await _http.GetByteArrayAsync(url, ct);
                var dem = ParseFloat32Tiff(bytes);
                int rows = dem.GetLength(0), cols = dem.GetLength(1);
                return new DemResult
                {
                    Dem = dem,
                    Rows = rows,
                    Cols = cols,
                    OriginLat = north,
                    OriginLng = west,
                    PixelLat = -(north - south) / rows,
                    PixelLng = (east - west) / cols,
                };
            }
            catch when (!ct.IsCancellationRequested)
            {
                if (attempt < 3) await Task.Delay(500 * attempt, ct);
            }
        }
        return null;
    }

    private static double[,] ParseFloat32Tiff(byte[] bytes)
    {
        // Minimal TIFF parser for the USGS 3DEP export: little-endian, uncompressed,
        // single-sample 32-bit float, tiled or stripped.
        uint ifdOffset = BitConverter.ToUInt32(bytes, 4);
        int offset = (int)ifdOffset;
        ushort numEntries = BitConverter.ToUInt16(bytes, offset);
        offset += 2;

        uint width = 0, height = 0, tileWidth = 0, tileLength = 0, rowsPerStrip = 0;
        uint[] tileOffsets = Array.Empty<uint>();
        uint[] stripOffsets = Array.Empty<uint>();

        for (int i = 0; i < numEntries; i++)
        {
            ushort tag = BitConverter.ToUInt16(bytes, offset);
            uint count = BitConverter.ToUInt32(bytes, offset + 4);
            uint valueOrOffset = BitConverter.ToUInt32(bytes, offset + 8);

            // All needed tags are LONG (type 4): inline if count == 1, else an array offset.
            uint[] ReadLongs()
            {
                var vals = new uint[count];
                if (count == 1) vals[0] = valueOrOffset;
                else for (uint k = 0; k < count; k++) vals[k] = BitConverter.ToUInt32(bytes, (int)valueOrOffset + (int)k * 4);
                return vals;
            }

            switch (tag)
            {
                case 256: width = valueOrOffset; break;         // ImageWidth
                case 257: height = valueOrOffset; break;        // ImageLength
                case 322: tileWidth = valueOrOffset; break;     // TileWidth
                case 323: tileLength = valueOrOffset; break;    // TileLength
                case 324: tileOffsets = ReadLongs(); break;     // TileOffsets
                case 273: stripOffsets = ReadLongs(); break;    // StripOffsets
                case 278: rowsPerStrip = valueOrOffset; break;  // RowsPerStrip
            }
            offset += 12;
        }

        var dem = new double[height, width];

        if (tileOffsets.Length > 0)
        {
            if (tileWidth == 0) tileWidth = width;
            if (tileLength == 0) tileLength = height;
            int tilesPerRow = (int)((width + tileWidth - 1) / tileWidth);
            for (int y = 0; y < height; y++)
            {
                int tileY = y / (int)tileLength, yy = y % (int)tileLength;
                for (int x = 0; x < width; x++)
                {
                    int tileX = x / (int)tileWidth, xx = x % (int)tileWidth;
                    int tileIndex = tileY * tilesPerRow + tileX;
                    if (tileIndex >= tileOffsets.Length) continue;
                    float v = BitConverter.ToSingle(bytes, (int)tileOffsets[tileIndex] + (yy * (int)tileWidth + xx) * 4);
                    dem[y, x] = float.IsFinite(v) ? v : 0.0;
                }
            }
        }
        else if (stripOffsets.Length > 0)
        {
            if (rowsPerStrip == 0) rowsPerStrip = height;
            for (int y = 0; y < height; y++)
            {
                int strip = y / (int)rowsPerStrip, rowInStrip = y % (int)rowsPerStrip;
                if (strip >= stripOffsets.Length) continue;
                int baseOffset = (int)stripOffsets[strip] + rowInStrip * (int)width * 4;
                for (int x = 0; x < width; x++)
                {
                    float v = BitConverter.ToSingle(bytes, baseOffset + x * 4);
                    dem[y, x] = float.IsFinite(v) ? v : 0.0;
                }
            }
        }

        return dem;
    }
}
