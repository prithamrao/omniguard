using System.Text.Json;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Holds the study-area terrain (real DEM or synthetic fallback), runs the hydrology
/// pipeline once at startup, and serves map data / flow paths / per-parcel features.
/// </summary>
public sealed class TerrainService
{
    private readonly IConfiguration _config;
    private HydrologyResult _h = null!;
    private int _rows = 200, _cols = 200;
    private double _originLat, _originLng, _pixelLat, _pixelLng;

    public TerrainService(IConfiguration config) => _config = config;

    public void Initialize()
    {
        _rows = _config.GetValue("Terrain:Rows", 200);
        _cols = _config.GetValue("Terrain:Cols", 200);
        _originLat = _config.GetValue("Terrain:OriginLat", 38.63);
        _originLng = _config.GetValue("Terrain:OriginLng", -90.35);
        double pixelDeg = _config.GetValue("Terrain:PixelDegrees", 0.0003);

        // North-up geographic grid defaults (used by the synthetic fallback only).
        _pixelLng = pixelDeg;
        _pixelLat = -pixelDeg;

        // Prefer a real DEM (backend/data/terrain.json, baked into the image).
        // Falls back to synthetic terrain if the file is absent.
        if (!TryLoadDem(out var dem, out var impervious))
        {
            (dem, impervious) = GenerateDemoTerrain(_rows, _cols);
        }

        _h = HydrologyService.Compute(dem, impervious, Math.Abs(_pixelLat) * 111_320);
    }

    /// <summary>
    /// Loads a preprocessed DEM from data/terrain.json (produced by tools/prepare_dem.py):
    /// { rows, cols, originLat, originLng, pixelLat, pixelLng, elevations: [row-major] }.
    /// </summary>
    private bool TryLoadDem(out double[,] dem, out double[,]? impervious)
    {
        dem = new double[0, 0];
        impervious = null;

        var file = _config["Terrain:DemFile"] ?? "data/terrain.json";
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, file));
        if (!File.Exists(path)) return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int rows = root.GetProperty("rows").GetInt32();
            int cols = root.GetProperty("cols").GetInt32();
            var elevations = root.GetProperty("elevations");
            if (elevations.GetArrayLength() != rows * cols) return false;

            dem = new double[rows, cols];
            int idx = 0;
            foreach (var e in elevations.EnumerateArray())
            {
                dem[idx / cols, idx % cols] = e.GetDouble();
                idx++;
            }

            // Optional imperviousness layer (0-100), same shape as the DEM.
            if (root.TryGetProperty("imperviousness", out var imp) && imp.GetArrayLength() == rows * cols)
            {
                impervious = new double[rows, cols];
                idx = 0;
                foreach (var v in imp.EnumerateArray())
                {
                    impervious[idx / cols, idx % cols] = v.GetDouble();
                    idx++;
                }
            }

            _rows = rows;
            _cols = cols;
            _originLat = root.GetProperty("originLat").GetDouble();
            _originLng = root.GetProperty("originLng").GetDouble();
            _pixelLat = root.GetProperty("pixelLat").GetDouble();
            _pixelLng = root.GetProperty("pixelLng").GetDouble();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public MapData GetMapData(int stride)
    {
        stride = Math.Max(1, stride);
        var pts = new List<RiskPoint>();
        for (int i = 0; i < _rows; i += stride)
        for (int j = 0; j < _cols; j += stride)
        {
            var (lat, lng) = CellCenter(i, j);
            pts.Add(new RiskPoint
            {
                Lat = lat, Lng = lng, Row = i, Col = j,
                Risk = _h.Risk[i, j],
                FlowAcc = _h.Accumulation[i, j],
                Slope = _h.Slope[i, j],
                Elevation = _h.Dem[i, j],
                Depression = _h.Depression[i, j],
                Impervious = _h.Impervious[i, j],
            });
        }

        return new MapData
        {
            Rows = _rows, Cols = _cols,
            OriginLat = _originLat, OriginLng = _originLng,
            PixelLat = _pixelLat, PixelLng = _pixelLng,
            Points = pts,
        };
    }

    /// <summary>Builds a MapData response from a computed HydrologyResult (used by the dynamic endpoint).</summary>
    public static MapData BuildMapData(HydrologyResult h, int rows, int cols,
        double originLat, double originLng, double pixelLat, double pixelLng, int stride)
    {
        stride = Math.Max(1, stride);
        var pts = new List<RiskPoint>();
        for (int i = 0; i < rows; i += stride)
        for (int j = 0; j < cols; j += stride)
        {
            pts.Add(new RiskPoint
            {
                Lat = originLat + (i + 0.5) * pixelLat,
                Lng = originLng + (j + 0.5) * pixelLng,
                Row = i, Col = j,
                Risk = h.Risk[i, j],
                FlowAcc = h.Accumulation[i, j],
                Slope = h.Slope[i, j],
                Elevation = h.Dem[i, j],
                Depression = h.Depression[i, j],
                Impervious = h.Impervious[i, j],
            });
        }
        return new MapData
        {
            Rows = rows, Cols = cols,
            OriginLat = originLat, OriginLng = originLng,
            PixelLat = pixelLat, PixelLng = pixelLng,
            Points = pts,
        };
    }

    /// <summary>Follow the downhill flow path from a coordinate to its sink.</summary>
    public List<Coord> TraceFlowPath(double lat, double lng)
    {
        var path = new List<Coord>();
        var (row, col) = ToCell(lat, lng);
        int steps = 0, maxSteps = _rows * _cols;
        while (row >= 0 && row < _rows && col >= 0 && col < _cols && steps++ < maxSteps)
        {
            var (clat, clng) = CellCenter(row, col);
            path.Add(new Coord(clat, clng));
            if (_h.IsSink[row, col]) break;
            row += _h.FlowDr[row, col];
            col += _h.FlowDc[row, col];
        }
        return path;
    }

    public FeatureSet GetFeatures(double lat, double lng)
    {
        var (row, col) = ToCell(lat, lng);
        row = Math.Clamp(row, 0, _rows - 1);
        col = Math.Clamp(col, 0, _cols - 1);
        var (clat, clng) = CellCenter(row, col);
        return new FeatureSet
        {
            Lat = clat, Lng = clng,
            Elevation = _h.Dem[row, col],
            Slope = _h.Slope[row, col],
            FlowAccumulation = _h.Accumulation[row, col],
            Depression = _h.Depression[row, col],
            Imperviousness = _h.Impervious[row, col],
            DeterministicRisk = _h.Risk[row, col],
        };
    }

    private (double lat, double lng) CellCenter(int row, int col)
        => (_originLat + (row + 0.5) * _pixelLat, _originLng + (col + 0.5) * _pixelLng);

    private (int row, int col) ToCell(double lat, double lng)
        => ((int)Math.Floor((lat - _originLat) / _pixelLat),
            (int)Math.Floor((lng - _originLng) / _pixelLng));

    /// <summary>
    /// Deterministic synthetic terrain so the app runs even without a real DEM:
    /// a sloping plain, a meandering stream valley, plus two "subdivisions" and a road
    /// with high imperviousness.
    /// </summary>
    private static (double[,] dem, double[,] impervious) GenerateDemoTerrain(int rows, int cols)
    {
        var dem = new double[rows, cols];
        var impervious = new double[rows, cols];
        var rng = new Random(42);

        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            double x = j, y = i;
            double baseSlope = 0.01 * (x + y);
            double center = cols / 2.0 + 20.0 * Math.Sin(y / 30.0);
            double valley = Math.Abs(x - center);
            double raised = 0.08 * valley;
            double channel = 0.2 * Math.Exp(-(valley * valley) / (2.0 * 3.0 * 3.0));
            double noise = rng.NextDouble() * 0.02 - 0.01;
            dem[i, j] = baseSlope + raised - channel + noise;
            impervious[i, j] = 5.0;
        }

        void Pave(int r0, int c0, int r1, int c1, double v)
        {
            for (int i = r0; i <= r1 && i < rows; i++)
            for (int j = c0; j <= c1 && j < cols; j++)
                impervious[i, j] = v;
        }

        Pave(40, 30, 75, 70, 85.0);     // subdivision A
        Pave(120, 120, 160, 165, 90.0); // subdivision B

        for (int j = 0; j < cols; j++)   // a road cutting across
            impervious[rows / 2, j] = 80.0;

        return (dem, impervious);
    }
}
