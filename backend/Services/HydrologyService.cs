namespace FloodRisk.Api.Services;

/// <summary>All derived grids from a single elevation raster.</summary>
public sealed class HydrologyResult
{
    public double[,] Dem { get; init; } = new double[0, 0];
    public double[,] Filled { get; init; } = new double[0, 0];
    public sbyte[,] FlowDr { get; init; } = new sbyte[0, 0];
    public sbyte[,] FlowDc { get; init; } = new sbyte[0, 0];
    public bool[,] IsSink { get; init; } = new bool[0, 0];
    public double[,] Accumulation { get; init; } = new double[0, 0];
    /// <summary>Steepest downhill slope, percent (rise/run * 100).</summary>
    public double[,] Slope { get; init; } = new double[0, 0];
    /// <summary>Ponding depth in meters: how deep water stands here before it can spill out (0 = drains freely).</summary>
    public double[,] Depression { get; init; } = new double[0, 0];
    /// <summary>Meters below the surrounding ~200 m neighborhood average (negative = higher than neighbors).</summary>
    public double[,] RelativeLow { get; init; } = new double[0, 0];
    public double[,] Impervious { get; init; } = new double[0, 0];
    public double[,] Risk { get; init; } = new double[0, 0];
    public double CellSizeM { get; init; }
}

/// <summary>
/// Pure hydrology: depression filling (priority-flood), D8 flow routing, flow accumulation,
/// slope, ponding depth, and a 0-100 flood/runoff risk score. No external GIS dependencies.
/// The score uses fixed physical scales (not per-area min/max), so the same parcel always
/// gets the same score no matter which part of the map was loaded first.
/// It is a screening index, not a calibrated probability.
/// </summary>
public static class HydrologyService
{
    private static readonly int[] Dr = { -1, 1, 0, 0, -1, -1, 1, 1 };
    private static readonly int[] Dc = { 0, 0, -1, 1, -1, 1, -1, 1 };
    private static readonly double[] Dist =
        { 1, 1, 1, 1, Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2), Math.Sqrt(2) };

    /// <summary>Minimum ponding depth (m) treated as a real low spot rather than DEM noise.</summary>
    public const double PondThresholdM = 0.15;

    public static HydrologyResult Compute(double[,] dem, double[,]? impervious = null, double cellSizeM = 10.0)
    {
        int rows = dem.GetLength(0), cols = dem.GetLength(1);

        // 1) Priority-flood depression filling (Barnes et al. 2014) with a tiny epsilon so
        //    flats still drain. Filled - Dem = how deep water ponds in that cell.
        var filled = Fill(dem, 1e-4);

        var flowDr = new sbyte[rows, cols];
        var flowDc = new sbyte[rows, cols];
        var isSink = new bool[rows, cols];
        var slope = new double[rows, cols];
        double big = Max(filled) + 1000.0;

        // 2) D8 flow direction on the filled surface (water crosses ponds and keeps going),
        //    slope on the real ground surface.
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            double best = double.NegativeInfinity, bestGround = 0;
            sbyte bdr = 0, bdc = 0;
            for (int k = 0; k < 8; k++)
            {
                double s = (filled[i, j] - Get(filled, i + Dr[k], j + Dc[k], big)) / Dist[k];
                if (s > best) { best = s; bdr = (sbyte)Dr[k]; bdc = (sbyte)Dc[k]; }
                double g = (dem[i, j] - Get(dem, i + Dr[k], j + Dc[k], dem[i, j])) / (Dist[k] * cellSizeM);
                if (g > bestGround) bestGround = g;
            }
            slope[i, j] = bestGround * 100.0;
            if (best <= 0) isSink[i, j] = true;
            else { flowDr[i, j] = bdr; flowDc[i, j] = bdc; }
        }

        // 3) Flow accumulation: process cells high-to-low, push each into its downstream neighbor.
        var acc = new double[rows, cols];
        var order = new List<(int i, int j, double e)>(rows * cols);
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            acc[i, j] = 1.0;
            order.Add((i, j, filled[i, j]));
        }
        order.Sort((a, b) => b.e.CompareTo(a.e));
        foreach (var (i, j, _) in order)
        {
            if (isSink[i, j]) continue;
            acc[i + flowDr[i, j], j + flowDc[i, j]] += acc[i, j];
        }

        // 4) Ponding depth (true low spots) and relative lowness vs. the neighborhood.
        var localMean = BoxBlur(dem, 7);
        var depression = new double[rows, cols];
        var relLow = new double[rows, cols];
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            double d = filled[i, j] - dem[i, j];
            depression[i, j] = d < 0.01 ? 0 : d;
            relLow[i, j] = localMean[i, j] - dem[i, j];
        }

        var imp = impervious ?? new double[rows, cols];
        var risk = new double[rows, cols];
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
            risk[i, j] = Score(acc[i, j], slope[i, j], depression[i, j], relLow[i, j], imp[i, j]);

        return new HydrologyResult
        {
            Dem = dem,
            Filled = filled,
            FlowDr = flowDr,
            FlowDc = flowDc,
            IsSink = isSink,
            Accumulation = acc,
            Slope = slope,
            Depression = depression,
            RelativeLow = relLow,
            Impervious = imp,
            Risk = risk,
            CellSizeM = cellSizeM,
        };
    }

    /// <summary>
    /// 0-100 risk from fixed physical scales:
    /// upstream cells (log, saturating at ~2000 cells), flatness (0% slope = 1, >=5% = 0),
    /// ponding depth (saturating at 0.5 m), sitting lower than the neighborhood (saturating at 1.5 m),
    /// and paved fraction.
    /// </summary>
    public static double Score(double acc, double slopePct, double pondM, double relLowM, double imperviousPct)
    {
        double accN = Math.Clamp(Math.Log(acc) / Math.Log(2000), 0, 1);
        double flatN = Math.Clamp(1 - slopePct / 5.0, 0, 1);
        double pondN = Math.Clamp(pondM / 0.5, 0, 1);
        double lowN = Math.Clamp(relLowM / 1.5, 0, 1);
        double impN = Math.Clamp(imperviousPct / 100.0, 0, 1);
        double r = 0.30 * accN + 0.20 * flatN + 0.25 * Math.Max(pondN, lowN) + 0.10 * Math.Min(pondN, lowN) + 0.15 * impN;
        return Math.Round(r * 100.0, 1);
    }

    private static double[,] Fill(double[,] dem, double eps)
    {
        int rows = dem.GetLength(0), cols = dem.GetLength(1);
        var filled = (double[,])dem.Clone();
        var closed = new bool[rows, cols];
        var pq = new PriorityQueue<(int i, int j), double>();
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            if (i == 0 || j == 0 || i == rows - 1 || j == cols - 1)
            {
                closed[i, j] = true;
                pq.Enqueue((i, j), filled[i, j]);
            }
        }
        while (pq.TryDequeue(out var c, out _))
        {
            for (int k = 0; k < 8; k++)
            {
                int ni = c.i + Dr[k], nj = c.j + Dc[k];
                if (ni < 0 || nj < 0 || ni >= rows || nj >= cols || closed[ni, nj]) continue;
                closed[ni, nj] = true;
                filled[ni, nj] = Math.Max(filled[ni, nj], filled[c.i, c.j] + eps);
                pq.Enqueue((ni, nj), filled[ni, nj]);
            }
        }
        return filled;
    }

    /// <summary>Mean over a (2r+1)^2 window, via a summed-area table.</summary>
    private static double[,] BoxBlur(double[,] src, int radius)
    {
        int rows = src.GetLength(0), cols = src.GetLength(1);
        var sat = new double[rows + 1, cols + 1];
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
            sat[i + 1, j + 1] = src[i, j] + sat[i, j + 1] + sat[i + 1, j] - sat[i, j];

        var outArr = new double[rows, cols];
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            int i0 = Math.Max(0, i - radius), i1 = Math.Min(rows, i + radius + 1);
            int j0 = Math.Max(0, j - radius), j1 = Math.Min(cols, j + radius + 1);
            double sum = sat[i1, j1] - sat[i0, j1] - sat[i1, j0] + sat[i0, j0];
            outArr[i, j] = sum / ((i1 - i0) * (j1 - j0));
        }
        return outArr;
    }

    private static double Get(double[,] a, int i, int j, double oob)
        => (i >= 0 && i < a.GetLength(0) && j >= 0 && j < a.GetLength(1)) ? a[i, j] : oob;

    private static double Max(double[,] a)
    {
        double m = double.MinValue;
        foreach (double v in a) if (v > m) m = v;
        return m;
    }
}
