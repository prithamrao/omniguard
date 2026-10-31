using System.Globalization;
using System.Collections.Concurrent;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>Per-request inputs that refine an assessment beyond the shared terrain grid.</summary>
/// <param name="RainInches">24-hour rainfall for the scenario, 0-50 inches (1 = an ordinary rain).</param>
public sealed record AssessOptions(Parcel? Parcel = null, double RainInches = 1.0, IReadOnlyList<WaterReport>? Reports = null, LotDrainage? Surface = null);

public sealed class AreaResult
{
    public required HydrologyResult H { get; init; }
    public required int Rows { get; init; }
    public required int Cols { get; init; }
    public required double OriginLat { get; init; }
    public required double OriginLng { get; init; }
    public required double PixelLat { get; init; }
    public required double PixelLng { get; init; }
    public List<(double lng, double lat, string? number)> Buildings { get; init; } = new();
    public bool[,]? Water { get; init; }
    public bool[,]? Street { get; init; }
    public int? SurveyYear { get; init; }
    public List<List<(double lng, double lat)>> StreetLines { get; init; } = new();
}

/// <summary>
/// Caches per-area hydrology (fetched on demand via DemService) and exposes
/// grid / flow-path / feature queries against any world location.
/// </summary>
public sealed class AreaService
{
    /// <summary>Bump when the scoring or features change, so stale cached assessments are not reused.</summary>
    public const int AlgoVersion = 11;

    /// <summary>The ordinary-rain baseline every assessment is also judged against, inches.</summary>
    public const double NormalRainInches = 1.0;
    public const double MaxRainInches = 50.0;

    /// <summary>
    /// Runoff depth (inches) from a 24-hour rain, by the NRCS/SCS Curve Number method. The curve number
    /// blends pavement (CN 98) with suburban lawn on clay-loam soil (CN 74) by the paved share, so a
    /// small rain mostly soaks in and a big storm runs off almost entirely.
    /// </summary>
    public static double RunoffInches(double rainInches, double imperviousPct)
    {
        double f = Math.Clamp(imperviousPct / 100.0, 0, 1);
        double cn = 98 * f + 74 * (1 - f);
        double s = 1000 / cn - 10, ia = 0.2 * s;
        return rainInches <= ia ? 0 : (rainInches - ia) * (rainInches - ia) / (rainInches + 0.8 * s);
    }

    /// <summary>
    /// How much a rainfall amount scales the terrain score: sqrt(inches) — 0 with no rain, 1 for an
    /// ordinary inch, about 1.6 for a 2.5-inch storm, about 7 for 50 inches (capped at 100 overall).
    /// </summary>
    public static double RainFactor(double rainInches) => Math.Sqrt(Math.Clamp(rainInches, 0, MaxRainInches));

    private static string RainLabel(double inches)
        => inches == NormalRainInches ? "1-inch rain" : $"{inches.ToString("0.##", CultureInfo.InvariantCulture)}-inch storm";
    private const double ReportedPondRadiusM = 20;
    /// <summary>How far past the street we look for the pond its storm drains likely empty into.</summary>
    private const double OutfallSearchM = 400;

    private const double Bucket = 0.02;          // ~2 km area tiles
    private const double MaxPathM = 800;         // how far downhill we follow runoff (beyond this it is not "your" low spot)
    private const double ClickSnapM = 25;        // a click this close to a house counts as that house
    private const double AddressSnapM = 150;     // a searched house number may be this far from the geocode

    private readonly DemService _dem;
    private readonly ImperviousService _impervious;
    private readonly ConcurrentDictionary<(int, int), Lazy<Task<AreaResult?>>> _cache = new();

    public AreaService(DemService dem, ImperviousService impervious)
    {
        _dem = dem;
        _impervious = impervious;
    }

    public async Task<AreaResult?> GetAsync(double lat, double lng, CancellationToken ct = default)
    {
        // The grid is centered on the tile center, never on the clicked point, so every
        // parcel always lands in the same grid and gets the same numbers.
        var key = ((int)Math.Round(lat / Bucket), (int)Math.Round(lng / Bucket));
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Task<AreaResult?>>(() => BuildAsync(k.Item1 * Bucket, k.Item2 * Bucket)));
        var result = await lazy.Value.WaitAsync(ct);
        if (result is null) _cache.TryRemove(key, out _);   // let transient failures retry
        return result;
    }

    private async Task<AreaResult?> BuildAsync(double lat, double lng)
    {
        bool isUs = DemService.IsUs(lat, lng);
        int gridSize = DemService.GetGridSize(lat, lng);

        // Fetch elevation and imperviousness in parallel. Not tied to one request's
        // cancellation: the result is shared by every request in this area.
        var demTask = isUs ? _dem.FetchUsgsAsync(lat, lng) : _dem.FetchAsync(lat, lng);
        var impTask = _impervious.FetchAsync(lat, lng, gridSize);
        var surveyTask = isUs ? _dem.FetchSurveyYearAsync(lat, lng) : Task.FromResult<int?>(null);
        await Task.WhenAll(demTask, impTask, surveyTask);

        var dem = await demTask;
        if (dem is null) return null;
        var impervious = await impTask;

        double cellM = Math.Abs(dem.PixelLat) * 111_320;
        var h = HydrologyService.Compute(dem.Dem, impervious?.Grid, cellM);
        return new AreaResult
        {
            H = h,
            Rows = dem.Rows,
            Cols = dem.Cols,
            OriginLat = dem.OriginLat,
            OriginLng = dem.OriginLng,
            PixelLat = dem.PixelLat,
            PixelLng = dem.PixelLng,
            Buildings = impervious?.Buildings ?? new(),
            Water = impervious?.Water,
            Street = impervious?.Street,
            SurveyYear = await surveyTask,
            StreetLines = impervious?.StreetLines ?? new(),
        };
    }

    /// <summary>
    /// Snaps to the house the user meant. With a house number (address search), only a house
    /// with that exact number qualifies; for a plain map click, only a house right under the cursor.
    /// </summary>
    public (double lat, double lng)? Snap(AreaResult a, double lat, double lng, string? houseNumber)
    {
        double cosLat = Math.Cos(lat * Math.PI / 180);
        double limit = string.IsNullOrWhiteSpace(houseNumber) ? ClickSnapM : AddressSnapM;
        (double lat, double lng)? best = null;
        double bestD = double.MaxValue;
        foreach (var b in a.Buildings)
        {
            if (!string.IsNullOrWhiteSpace(houseNumber) && !string.Equals(b.number?.Trim(), houseNumber.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            double dy = (b.lat - lat) * 111_320, dx = (b.lng - lng) * 111_320 * cosLat;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < bestD) { bestD = d; best = (b.lat, b.lng); }
        }
        return bestD <= limit ? best : null;
    }

    public MapData BuildMapData(AreaResult a, int stride)
        => TerrainService.BuildMapData(a.H, a.Rows, a.Cols, a.OriginLat, a.OriginLng, a.PixelLat, a.PixelLng, stride);

    /// <summary>Downhill runoff path from a point, stopping where it enters a low spot (or after ~800 m).</summary>
    public List<Coord> TraceFlowPath(AreaResult a, double lat, double lng, IReadOnlyList<WaterReport>? reports = null)
        => FollowRunoff(a, lat, lng, ReportCells(a, reports), NormalRainInches).Path;

    private const double RearPondSearchM = 200;

    /// <summary>The street point a lot faces (closest point on any street centerline), if within 120 m.</summary>
    public Coord? NearestStreet(AreaResult a, Coord p)
        => NearestStreetPoint(a, p) is { } sp && Meters(p, sp) <= 120 ? sp : null;

    /// <summary>
    /// Route from the current aerial surface: the front path is where the street side of the lot drains,
    /// the back path is followed downhill until it reaches a mapped or reported pond (or ends).
    /// </summary>
    private Runoff SurfaceRoute(AreaResult a, Parcel parcel, LotDrainage surf, Dictionary<(int, int), WaterReport> reported)
    {
        var home = new Coord(parcel.Lat, parcel.Lng);
        var path = new List<Coord> { home };
        Coord? streetPt = NearestStreet(a, home);
        if (surf.FrontSharePct > 0 && streetPt is not null && surf.FrontPath.Count > 0)
        {
            // Stop the traced front path where it comes closest to the street, then step onto the street.
            int cut = 0;
            for (int i = 1; i < surf.FrontPath.Count; i++)
                if (Meters(surf.FrontPath[i], streetPt) < Meters(surf.FrontPath[cut], streetPt)) cut = i;
            path.AddRange(surf.FrontPath.Skip(1).Take(cut));
            path.Add(streetPt);
        }

        LowSpot? spot = null;
        double dist = 0;
        for (int i = 0; i < surf.BackPath.Count && spot is null; i++)
        {
            if (i > 0) dist += Meters(surf.BackPath[i - 1], surf.BackPath[i]);
            var (r, c) = ToCell(a, surf.BackPath[i].Lat, surf.BackPath[i].Lng);
            if (r < 0 || c < 0 || r >= a.Rows || c >= a.Cols) continue;
            if (reported.TryGetValue((r, c), out var rep) && rep.Kind == "pond") spot = DescribeReport(a, rep, reported, dist);
            else if (a.Water?[r, c] == true) spot = DescribeWater(a, r, c, dist);
        }
        // Not on the path itself: a pond within ~40 m of where the back-yard water ends up still receives it.
        if (spot is null && surf.BackPath.Count > 0)
        {
            var end = surf.BackPath[^1];
            var (r0, c0) = ToCell(a, end.Lat, end.Lng);
            int rad = (int)Math.Ceiling(40 / a.H.CellSizeM);
            (int r, int c, double d)? best = null;
            for (int dr = -rad; dr <= rad; dr++)
            for (int dc = -rad; dc <= rad; dc++)
            {
                int r = r0 + dr, c = c0 + dc;
                if (r < 0 || c < 0 || r >= a.Rows || c >= a.Cols) continue;
                bool water = a.Water?[r, c] == true || (reported.TryGetValue((r, c), out var rp) && rp.Kind == "pond");
                double d = Meters(end, CellCenter(a, r, c));
                if (water && d <= 40 && (best is null || d < best.Value.d)) best = (r, c, d);
            }
            if (best is { } b)
            {
                spot = reported.TryGetValue((b.r, b.c), out var rep) ? DescribeReport(a, rep, reported, dist + b.d) : DescribeWater(a, b.r, b.c, dist + b.d);
            }
        }
        if (spot is not null)
        {
            spot.DistanceM = Math.Round(Meters(home, new Coord(spot.Lat, spot.Lng)));
            spot.ViaRearYard = true;
        }
        double? streetDist = streetPt is null ? null : Math.Round(Meters(home, streetPt));
        return new Runoff(path, spot, surf.FrontSharePct > 0 ? streetPt : null, surf.FrontSharePct > 0 ? streetDist : null,
            surf.FrontSharePct > 0 && streetPt is not null ? path.Count - 1 : null);
    }

    /// <summary>
    /// For homes built after the elevation survey: the survey shows the land before grading, so instead
    /// of following stale contours across neighboring lots, use how subdivision lots are graded —
    /// roof and front yard drain to the street the lot faces; the back yard drains to the rear, toward
    /// the nearest pond (mapped or reported) if one is close.
    /// </summary>
    private Runoff LotGradingRoute(AreaResult a, Parcel parcel, Dictionary<(int, int), WaterReport> reported)
    {
        var home = new Coord(parcel.Lat, parcel.Lng);
        var (r0, c0) = ToCell(a, parcel.Lat, parcel.Lng);
        double cell = a.H.CellSizeM;

        (int r, int c, double d)? water = null;
        int rad = (int)Math.Ceiling(RearPondSearchM / cell);
        for (int dr = -rad; dr <= rad; dr++)
        for (int dc = -rad; dc <= rad; dc++)
        {
            int r = r0 + dr, c = c0 + dc;
            if (r < 0 || c < 0 || r >= a.Rows || c >= a.Cols) continue;
            var center = CellCenter(a, r, c);
            double d = Meters(home, center);
            bool isWater = a.Water?[r, c] == true || (reported.TryGetValue((r, c), out var rp) && rp.Kind == "pond");
            if (isWater && d <= RearPondSearchM && (water is null || d < water.Value.d)) water = (r, c, d);
        }

        var path = new List<Coord> { home };
        Coord? streetPt = null;
        double? streetDist = null;
        if (NearestStreetPoint(a, home) is { } sp && Meters(home, sp) <= 120)
        {
            streetPt = sp;
            streetDist = Math.Round(Meters(home, sp));
            path.Add(streetPt);
        }

        LowSpot? spot = null;
        if (water is { } w)
        {
            spot = reported.TryGetValue((w.r, w.c), out var rep)
                ? DescribeReport(a, rep, reported, w.d)
                : DescribeWater(a, w.r, w.c, w.d);
            spot.DistanceM = Math.Round(w.d);
            spot.ViaRearYard = true;
        }
        return new Runoff(path, spot, streetPt, streetDist, streetPt is null ? null : 1);
    }

    /// <summary>Closest point on any street centerline (local meters projection around the point).</summary>
    private static Coord? NearestStreetPoint(AreaResult a, Coord p)
    {
        double kx = 111_320 * Math.Cos(p.Lat * Math.PI / 180), ky = 111_320;
        Coord? best = null;
        double bestD = double.MaxValue;
        foreach (var line in a.StreetLines)
        {
            for (int i = 0; i + 1 < line.Count; i++)
            {
                double ax = (line[i].lng - p.Lng) * kx, ay = (line[i].lat - p.Lat) * ky;
                double bx = (line[i + 1].lng - p.Lng) * kx, by = (line[i + 1].lat - p.Lat) * ky;
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                double t = len2 < 1e-9 ? 0 : Math.Clamp(-(ax * dx + ay * dy) / len2, 0, 1);
                double qx = ax + t * dx, qy = ay + t * dy, d = qx * qx + qy * qy;
                if (d < bestD) { bestD = d; best = new Coord(p.Lat + qy / ky, p.Lng + qx / kx); }
            }
        }
        return best;
    }

    private static double Meters(Coord a, Coord b)
    {
        double dy = (b.Lat - a.Lat) * 111_320, dx = (b.Lng - a.Lng) * 111_320 * Math.Cos(a.Lat * Math.PI / 180);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record Runoff(List<Coord> Path, LowSpot? Spot, Coord? Street, double? StreetDistanceM, int? StreetIndex);

    /// <summary>
    /// Follows runoff downhill. Before it reaches a street it can pool in a low spot, a pond or a reported
    /// feature. Once it reaches a street, curbs and gutters carry it into the storm drains — so low spots
    /// beyond (often stale terrain under newer houses) no longer apply, and only a pond the drains likely
    /// empty into is reported.
    /// </summary>
    private Runoff FollowRunoff(AreaResult a, double lat, double lng, Dictionary<(int, int), WaterReport> reported, double rainInches)
    {
        double cell = a.H.CellSizeM, dist = 0;
        var path = new List<Coord>();
        (int r, int c)? prev = null;
        int? streetIdx = null;
        double? streetDist = null;
        foreach (var (row, col) in Walk(a, lat, lng))
        {
            if (prev is { } p) dist += (p.r != row && p.c != col ? Math.Sqrt(2) : 1) * cell;
            prev = (row, col);
            path.Add(CellCenter(a, row, col));
            bool afterStreet = streetIdx is not null;

            if (reported.TryGetValue((row, col), out var rep))
                return Done(DescribeReport(a, rep, reported, dist));
            if (a.Water?[row, col] == true)
                return Done(DescribeWater(a, row, col, dist));
            if (afterStreet)
            {
                if (dist - streetDist!.Value > OutfallSearchM) break;
                continue;
            }
            if (dist > 0 && a.Street?[row, col] == true)
            {
                streetIdx = path.Count - 1;
                streetDist = dist;
                continue;
            }
            if (a.H.Depression[row, col] >= HydrologyService.PondThresholdM)
            {
                var ls = DescribePond(a, row, col, dist);
                // Will it overflow? Curve-number runoff from its whole catchment vs. the volume it can hold.
                double runoffM = RunoffInches(rainInches, CatchmentImpervious(a, row, col)) * 0.0254;
                ls.InflowM3 = Math.Round(runoffM * ls.CatchmentM2);
                ls.Overflows = ls.InflowM3 > ls.CapacityM3;
                return Done(ls);
            }
        }
        // No pond past the street: the drawn path ends where the street takes the water.
        if (streetIdx is { } si) path = path.Take(si + 1).ToList();
        return Done(null);

        Runoff Done(LowSpot? spot)
        {
            if (spot is not null && streetIdx is not null) spot.ViaStreet = true;
            return new Runoff(path, spot, streetIdx is { } i ? path[i] : null, streetDist is { } d ? Math.Round(d) : null, streetIdx);
        }
    }

    /// <summary>Grid cells covered by user reports: a pond covers ~20 m around its pin, a drain its cell and neighbors.</summary>
    private static Dictionary<(int, int), WaterReport> ReportCells(AreaResult a, IReadOnlyList<WaterReport>? reports)
    {
        var map = new Dictionary<(int, int), WaterReport>();
        if (reports is null) return map;
        foreach (var rep in reports)
        {
            var (r0, c0) = ToCell(a, rep.Lat, rep.Lng);
            int rad = rep.Kind == "drain" ? 1 : Math.Max(1, (int)Math.Round(ReportedPondRadiusM / a.H.CellSizeM));
            for (int dr = -rad; dr <= rad; dr++)
            for (int dc = -rad; dc <= rad; dc++)
            {
                int r = r0 + dr, c = c0 + dc;
                if (r < 0 || c < 0 || r >= a.Rows || c >= a.Cols) continue;
                if (rep.Kind != "drain" && (dr * dr + dc * dc) * a.H.CellSizeM * a.H.CellSizeM > ReportedPondRadiusM * ReportedPondRadiusM) continue;
                map[(r, c)] = rep;
            }
        }
        return map;
    }

    public FeatureSet GetFeatures(AreaResult a, double lat, double lng, (double lat, double lng)? snapped = null, AssessOptions? opts = null)
    {
        opts ??= new AssessOptions();
        var reported = ReportCells(a, opts.Reports);
        var (row, col) = ToCell(a, lat, lng);
        row = Math.Clamp(row, 0, a.Rows - 1);
        col = Math.Clamp(col, 0, a.Cols - 1);
        var center = CellCenter(a, row, col);
        double cell = a.H.CellSizeM;
        var f = new FeatureSet
        {
            Lat = center.Lat,
            Lng = center.Lng,
            Elevation = Math.Round(a.H.Dem[row, col], 2),
            Slope = Math.Round(a.H.Slope[row, col], 2),
            FlowAccumulation = a.H.Accumulation[row, col],
            UpstreamAreaM2 = Math.Round(a.H.Accumulation[row, col] * cell * cell),
            Depression = Math.Round(a.H.Depression[row, col], 2),
            RelativeLow = Math.Round(a.H.RelativeLow[row, col], 2),
            // Lot-level paving from county records beats the coarse map-derived grid.
            Imperviousness = Math.Round(opts.Parcel?.ImperviousPct ?? a.H.Impervious[row, col]),
            SnappedLat = snapped?.lat,
            SnappedLng = snapped?.lng,
            // Filled in from the runoff route just below.
            AlgoVersion = AlgoVersion,
            Scenario = opts.RainInches == NormalRainInches ? "normal" : "storm",
            RainInches = opts.RainInches,
        };
        f.Parcel = opts.Parcel;
        f.SurveyYear = a.SurveyYear;
        f.TerrainPredatesHome = opts.Parcel?.YearBuilt is { } built && a.SurveyYear is { } surveyed && built > surveyed;
        f.Surface = opts.Surface;
        var route = opts.Surface is { } surf && opts.Parcel is { } lot
            ? SurfaceRoute(a, lot, surf, reported)
            : f.TerrainPredatesHome
                ? LotGradingRoute(a, opts.Parcel!, reported)
                : FollowRunoff(a, lat, lng, reported, opts.RainInches);
        if (f.TerrainPredatesHome)
        {
            // A dip in the pre-construction ground is graded away under a newer home.
            f.Depression = 0;
        }
        f.LowSpot = route.Spot;
        f.Street = route.Street;
        f.StreetDistanceM = route.StreetDistanceM;
        f.StreetPathIndex = route.StreetIndex;
        f.RoutePath = route.Path;
        double baseRisk = HydrologyService.Score(f.FlowAccumulation, f.Slope, f.Depression, f.RelativeLow, f.Imperviousness);
        // Scale by the rainfall (the map uses the same factor), plus a bump when the low spot this property
        // drains to overflows.
        double risk = baseRisk * RainFactor(opts.RainInches);
        if (f.LowSpot is { Overflows: true } ov) risk += ov.DistanceM <= 100 ? 10 : 4;
        f.DeterministicRisk = Math.Round(Math.Min(100, risk), 1);
        f.Observations = Observe(f);
        f.CacheKey = $"{f.RainInches.ToString("0.##", CultureInfo.InvariantCulture)}in|{StableHash(string.Join("|", f.Observations))}";
        f.Hotspots = Hotspots(a, f, row, col);
        return f;
    }

    /// <summary>Puts each issue where it physically is: at the house, at the low spot, and where uphill water comes from.</summary>
    private static List<Hotspot> Hotspots(AreaResult a, FeatureSet f, int row, int col)
    {
        var list = new List<Hotspot>();

        var home = new List<string>();
        if (f.Imperviousness >= 40) home.Add($"About {f.Imperviousness:F0}% of the lot is roof, driveway or pavement — rain runs off instead of soaking in.");
        if (f.Slope < 1) home.Add($"Nearly flat ground ({f.Slope:F1}% slope) — water drains away slowly.");
        if (f.Depression >= HydrologyService.PondThresholdM) home.Add($"The house sits in a closed low spot; water can stand about {f.Depression:F2} m deep.");
        else if (f.RelativeLow >= 0.5) home.Add($"About {f.RelativeLow:F1} m lower than the surrounding neighborhood.");
        if (f.UpstreamAreaM2 >= 2_000) home.Add($"Runoff from {FormatArea(f.UpstreamAreaM2)} uphill passes through here.");
        list.Add(new Hotspot
        {
            Kind = "home",
            Lat = f.SnappedLat ?? f.Lat,
            Lng = f.SnappedLng ?? f.Lng,
            Title = home.Count > 0 ? "Problems at this property" : "This property",
            Details = home.Count > 0 ? home : new() { "No significant drainage problems right at the house." },
        });

        if (f.Street is { } st && f.StreetDistanceM is { } sdm)
        {
            list.Add(new Hotspot
            {
                Kind = "street",
                Lat = st.Lat,
                Lng = st.Lng,
                Title = "Your runoff reaches the street here",
                Details = new()
                {
                    $"Rain from the property flows {sdm:F0} m{(sdm > 30 ? " across neighboring yards" : "")} to this street.",
                    "Curbs and gutters carry it to the storm drains, so it doesn't pool in yards beyond.",
                    "Keep the curb inlets clear of leaves — a blocked inlet floods the street first.",
                },
            });
        }

        if (f.Surface is { BackSharePct: > 0, BackPath.Count: > 1 } back && f.LowSpot is not { ViaRearYard: true })
        {
            var end = back.BackPath[^1];
            list.Add(new Hotspot
            {
                Kind = "rear",
                Lat = end.Lat,
                Lng = end.Lng,
                Title = "Your back-yard runoff heads here",
                Details = new()
                {
                    $"About {back.BackSharePct:F0}% of the lot drains to the back (aerial surface, {back.ImageryDate}).",
                    "If there's a pond, swale or drain here that the map doesn't show, add it with + Pond / + Storm drain.",
                },
                Path = back.BackPath,
            });
        }

        if (f.LowSpot is { } ls)
        {
            list.Add(new Hotspot
            {
                Kind = ls.IsDrain ? "drain" : "lowspot",
                Lat = ls.Lat,
                Lng = ls.Lng,
                Title = ls.ViaRearYard ? "Your back yard drains toward this pond"
                    : ls.ViaStreet ? "The storm drains likely empty into this pond"
                    : ls.IsDrain ? "Your runoff reaches this storm drain"
                    : ls.Reported ? "Your runoff drains into this pond (reported by a resident)"
                    : ls.IsWater ? "Your runoff drains into this pond / stream"
                    : ls.DistanceM == 0 ? "Low spot under this property" : "Low spot where your runoff collects",
                Details = ls.ViaRearYard
                    ? new()
                    {
                        $"About {ls.DistanceM:F0} m from the house{(ls.Reported ? " (pond reported by a resident)" : "")}.",
                        "Newer lots are graded so the back yard sheds water to the rear, toward ponds and swales like this one.",
                        "In a heavy storm its level rises — keep the rear swale and any yard drains clear.",
                    }
                    : ls.ViaStreet
                    ? new()
                    {
                        $"Street runoff from this area flows on toward this pond, about {ls.DistanceM:F0} m from the property.",
                        "Route shown past the street is approximate — underground pipes aren't in public data.",
                    }
                    : ls.IsDrain
                    ? new()
                    {
                        $"Rain from the property flows {ls.DistanceM:F0} m downhill and leaves through this drain.",
                        "Keep it clear of leaves and debris, or water backs up toward the houses.",
                    }
                    : ls.IsWater
                    ? new()
                    {
                        $"Rain from the property flows {ls.DistanceM:F0} m downhill and enters the water here (about {FormatArea(ls.AreaM2)} of water surface).",
                        "In a big storm the water level rises — ground beside it floods first.",
                    }
                    : new()
                    {
                        $"Water can stand up to {ls.DepthM:F2} m ({ls.DepthM * 39.37:F0} in) deep over about {FormatArea(ls.AreaM2)}.",
                        ls.DistanceM == 0 ? "Rain that falls on the property has nowhere to drain until this fills." : $"Rain from the property flows {ls.DistanceM:F0} m downhill and pools here.",
                        ls.CapacityM3 is { } cap && ls.InflowM3 is { } inflow
                            ? (ls.Overflows
                                ? $"⚠ In this rain ~{inflow:N0} m³ of runoff arrives but it holds only ~{cap:N0} m³ — it overflows."
                                : $"In this rain ~{inflow:N0} m³ of runoff arrives; it holds ~{cap:N0} m³.")
                            : "In a heavy storm this is the first place to flood.",
                    },
            });
        }

        // Follow the biggest incoming stream uphill to show where outside water comes from.
        if (f.UpstreamAreaM2 >= 2_000)
        {
            var path = new List<Coord> { CellCenter(a, row, col) };
            int r = row, c = col;
            int maxSteps = (int)(300 / a.H.CellSizeM);
            for (int s = 0; s < maxSteps; s++)
            {
                int br = -1, bc = -1; double best = 0;
                for (int dr = -1; dr <= 1; dr++)
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nr = r + dr, nc = c + dc;
                    if ((dr == 0 && dc == 0) || nr < 0 || nc < 0 || nr >= a.Rows || nc >= a.Cols) continue;
                    // Only neighbors that actually drain into (r, c).
                    if (nr + a.H.FlowDr[nr, nc] != r || nc + a.H.FlowDc[nr, nc] != c || a.H.IsSink[nr, nc]) continue;
                    if (a.H.Accumulation[nr, nc] > best) { best = a.H.Accumulation[nr, nc]; br = nr; bc = nc; }
                }
                if (br < 0 || best < 5) break;
                r = br; c = bc;
                path.Add(CellCenter(a, r, c));
            }
            if (path.Count > 2)
            {
                var top = path[^1];
                double upArea = a.H.Accumulation[r, c] * a.H.CellSizeM * a.H.CellSizeM;
                list.Add(new Hotspot
                {
                    Kind = "inflow",
                    Lat = top.Lat,
                    Lng = top.Lng,
                    Title = "Uphill water flows in from here",
                    Details = new()
                    {
                        $"Runoff from {FormatArea(f.UpstreamAreaM2)} of land drains toward the property in total.",
                        $"The main stream of it comes this way (about {FormatArea(upArea)} already collected at this point).",
                        "A swale, berm or French drain across this path can divert it.",
                    },
                    Path = path,
                });
            }
        }
        return list;
    }

    /// <summary>FNV-1a: stable across restarts (string.GetHashCode is randomized per process).</summary>
    private static string StableHash(string text)
    {
        ulong h = 14695981039346656037;
        foreach (char ch in text) { h ^= ch; h *= 1099511628211; }
        return h.ToString("X16");
    }

    private static string FormatArea(double m2)
        => m2 >= 4046.86 ? $"{m2 / 4046.86:F1} acres" : $"{m2 * 10.7639:N0} sq ft";

    /// <summary>Follows runoff downhill and returns the first real low spot it pools in.</summary>
    /// <summary>Average paved share around a low spot (~200 m), standing in for its catchment's paving.</summary>
    private static double CatchmentImpervious(AreaResult a, int row, int col)
    {
        int rad = Math.Max(2, (int)Math.Round(200 / a.H.CellSizeM));
        double sum = 0; int n = 0;
        for (int r = Math.Max(0, row - rad); r <= Math.Min(a.Rows - 1, row + rad); r++)
        for (int c = Math.Max(0, col - rad); c <= Math.Min(a.Cols - 1, col + rad); c++)
        { sum += a.H.Impervious[r, c]; n++; }
        return n == 0 ? 0 : sum / n;
    }

    private static LowSpot DescribeReport(AreaResult a, WaterReport rep, Dictionary<(int, int), WaterReport> reported, double distM)
    {
        var cells = reported.Where(kv => kv.Value.Id == rep.Id).Select(kv => CellCenter(a, kv.Key.Item1, kv.Key.Item2)).ToList();
        bool drain = rep.Kind == "drain";
        return new LowSpot
        {
            Lat = rep.Lat,
            Lng = rep.Lng,
            IsWater = !drain,
            IsDrain = drain,
            Reported = true,
            AreaM2 = drain ? 0 : Math.Round(Math.PI * ReportedPondRadiusM * ReportedPondRadiusM),
            DistanceM = Math.Round(distM),
            Cells = drain ? new() : cells,
        };
    }

    private static bool CollectsWater(AreaResult a, int row, int col)
        => a.Water?[row, col] == true || a.H.Depression[row, col] >= HydrologyService.PondThresholdM;

    /// <summary>A mapped pond/lake/stream the runoff reaches: its extent and a pin where the runoff enters it.</summary>
    private static LowSpot DescribeWater(AreaResult a, int r0, int c0, double distM)
    {
        const int maxCells = 4000;
        var seen = new HashSet<(int, int)> { (r0, c0) };
        var queue = new Queue<(int r, int c)>();
        queue.Enqueue((r0, c0));
        var cells = new List<(int r, int c)>();
        while (queue.Count > 0 && cells.Count < maxCells)
        {
            var (r, c) = queue.Dequeue();
            cells.Add((r, c));
            for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nc < 0 || nr >= a.Rows || nc >= a.Cols) continue;
                if (a.Water![nr, nc] != true || !seen.Add((nr, nc))) continue;
                queue.Enqueue((nr, nc));
            }
        }
        var entry = CellCenter(a, r0, c0);
        return new LowSpot
        {
            Lat = entry.Lat,
            Lng = entry.Lng,
            IsWater = true,
            AreaM2 = Math.Round(cells.Count * a.H.CellSizeM * a.H.CellSizeM),
            DistanceM = Math.Round(distM),
            Cells = cells.Select(x => CellCenter(a, x.r, x.c)).ToList(),
        };
    }

    /// <summary>Flood-fills the connected ponding area around a cell: deepest point, area, outline cells.</summary>
    private static LowSpot DescribePond(AreaResult a, int r0, int c0, double distM)
    {
        const double edge = 0.05;
        const int maxCells = 4000;
        var seen = new HashSet<(int, int)> { (r0, c0) };
        var queue = new Queue<(int r, int c)>();
        queue.Enqueue((r0, c0));
        var cells = new List<(int r, int c)>();
        (int r, int c) deepest = (r0, c0);
        while (queue.Count > 0 && cells.Count < maxCells)
        {
            var (r, c) = queue.Dequeue();
            cells.Add((r, c));
            if (a.H.Depression[r, c] > a.H.Depression[deepest.r, deepest.c]) deepest = (r, c);
            for (int dr = -1; dr <= 1; dr++)
            for (int dc = -1; dc <= 1; dc++)
            {
                int nr = r + dr, nc = c + dc;
                if (nr < 0 || nc < 0 || nr >= a.Rows || nc >= a.Cols) continue;
                if (a.H.Depression[nr, nc] < edge || !seen.Add((nr, nc))) continue;
                queue.Enqueue((nr, nc));
            }
        }
        var center = CellCenter(a, deepest.r, deepest.c);
        double cellArea = a.H.CellSizeM * a.H.CellSizeM;
        return new LowSpot
        {
            Lat = center.Lat,
            Lng = center.Lng,
            CapacityM3 = Math.Round(cells.Sum(x => a.H.Depression[x.r, x.c]) * cellArea),
            CatchmentM2 = cells.Max(x => a.H.Accumulation[x.r, x.c]) * cellArea,
            DepthM = Math.Round(a.H.Depression[deepest.r, deepest.c], 2),
            AreaM2 = Math.Round(cells.Count * a.H.CellSizeM * a.H.CellSizeM),
            DistanceM = Math.Round(distM),
            Cells = cells.Select(x => CellCenter(a, x.r, x.c)).ToList(),
        };
    }

    /// <summary>
    /// Plain-language facts, using only thresholds the numbers actually support.
    /// Each is tagged "Concern:", "In its favor:" or "Info:" so positives are never reported as issues.
    /// </summary>
    private static List<string> Observe(FeatureSet f)
    {
        var o = new List<string>();
        if (f.Imperviousness >= 80) o.Add($"Concern: about {f.Imperviousness:F0}% of the ground here is roof, driveway or road, so rain runs off instead of soaking in");
        else if (f.Imperviousness >= 40) o.Add($"Concern: about {f.Imperviousness:F0}% of the ground here is paved or built on");
        else o.Add("In its favor: most of the ground here is unpaved and can soak up rain");

        if (f.Slope < 1) o.Add($"Concern: the land is nearly flat ({f.Slope:F1}% slope), so water drains away slowly");
        else if (f.Slope >= 5) o.Add($"In its favor: the land slopes ({f.Slope:F1}%), so water moves away quickly");

        if (f.Depression >= HydrologyService.PondThresholdM)
            o.Add($"Concern: the property sits in a closed low spot where water can stand about {f.Depression:F2} m deep before it drains");
        else if (f.RelativeLow >= 0.5)
            o.Add($"Concern: the property sits about {f.RelativeLow:F1} m lower than the surrounding neighborhood");
        else if (f.RelativeLow <= -0.5)
            o.Add($"In its favor: the property sits about {-f.RelativeLow:F1} m higher than the surrounding neighborhood");

        if (f.UpstreamAreaM2 >= 20_000) o.Add($"Concern: runoff from about {f.UpstreamAreaM2 / 4046.86:F1} acres uphill flows through this spot");
        else if (f.UpstreamAreaM2 >= 2_000) o.Add($"Concern: runoff from about {f.UpstreamAreaM2:N0} m² of land uphill drains through this spot");
        else o.Add("In its favor: very little water drains in from uphill");

        string rain = RainLabel(f.RainInches);
        if (f.RainInches > 0)
        {
            double runoff = RunoffInches(f.RainInches, f.Imperviousness);
            double share = runoff / f.RainInches * 100;
            string tag = f.RainInches >= 6 || share >= 50 ? "Concern" : "Info";
            o.Add(f.RainInches >= 6
                ? $"{tag}: with {f.RainInches:0.##} inches of rain the ground saturates and about {share:F0}% runs off this lot ({runoff:0.#} inches of water) — yards, streets and storm drains everywhere overflow at this amount"
                : $"{tag}: in a {rain} about {share:F0}% of the water runs off this lot ({runoff:0.##} inches) and the rest soaks in");
        }
        if (f.Surface is { } sf)
            o.Add($"Info: on the current aerial surface ({sf.ImageryDate}), about {sf.FrontSharePct:F0}% of the lot drains toward the street and {sf.BackSharePct:F0}% toward the back");
        else if (f.TerrainPredatesHome)
            o.Add($"Info: the public elevation survey here is from {f.SurveyYear} but this home was built in {f.Parcel?.YearBuilt}, so its lot grading is not in the survey; runoff follows standard lot grading — roof and front yard to the street, back yard toward the rear");
        if (f.StreetDistanceM is { } sd)
            o.Add(sd <= 30
                ? $"In its favor: runoff reaches the street {sd:F0} m away, where curbs and gutters carry it to the storm drains"
                : $"Info: runoff crosses about {sd:F0} m of neighboring yards before reaching the street and its storm drains");
        if (f.LowSpot is { IsDrain: true } dr)
            o.Add($"In its favor: runoff from here reaches a storm drain {dr.DistanceM:F0} m downhill (reported by a resident)");
        else if (f.LowSpot is { ViaRearYard: true } rear)
            o.Add($"{(rear.DistanceM <= 100 ? "Concern" : "Info")}: the back yard drains toward a pond about {rear.DistanceM:F0} m away{(rear.Reported ? " (reported by a resident)" : "")}, which rises in heavy storms");
        else if (f.LowSpot is { ViaStreet: true } outfall)
            o.Add($"Info: the street's storm drains likely empty into a pond about {outfall.DistanceM:F0} m away");
        else if (f.LowSpot is { } ls && ls.DistanceM > 0)
        {
            var near = ls.DistanceM <= 100 ? "Concern" : "Info";
            var who = ls.Reported ? " (reported by a resident)" : "";
            o.Add(ls.IsWater
                ? $"{near}: runoff from here flows {ls.DistanceM:F0} m downhill into a pond or stream{who} that can overflow in heavy storms"
                : $"{near}: runoff from here flows {ls.DistanceM:F0} m downhill into a low spot where water collects (up to {ls.DepthM:F2} m deep, about {ls.AreaM2:N0} m²)");
        }
        if (f.LowSpot is { CapacityM3: { } cap, InflowM3: { } inflow } spot && !spot.IsWater && !spot.IsDrain)
        {
            o.Add(spot.Overflows
                ? $"{(spot.DistanceM <= 100 ? "Concern" : "Info")}: in a {rain} about {inflow:N0} m³ of runoff drains into that low spot, but it holds only about {cap:N0} m³, so it fills to the brim and spills over toward the surrounding lots"
                : $"Info: in a {rain} about {inflow:N0} m³ of runoff drains into that low spot, which can hold about {cap:N0} m³");
        }
        return o;
    }

    private IEnumerable<(int row, int col)> Walk(AreaResult a, double lat, double lng)
    {
        var (row, col) = ToCell(a, lat, lng);
        int maxSteps = (int)(MaxPathM / a.H.CellSizeM);
        for (int steps = 0; row >= 0 && row < a.Rows && col >= 0 && col < a.Cols && steps <= maxSteps; steps++)
        {
            yield return (row, col);
            if (a.H.IsSink[row, col]) yield break;
            int nr = row + a.H.FlowDr[row, col], nc = col + a.H.FlowDc[row, col];
            row = nr; col = nc;
        }
    }

    private static Coord CellCenter(AreaResult a, int row, int col)
        => new(a.OriginLat + (row + 0.5) * a.PixelLat, a.OriginLng + (col + 0.5) * a.PixelLng);

    private static (int row, int col) ToCell(AreaResult a, double lat, double lng)
        => ((int)Math.Floor((lat - a.OriginLat) / a.PixelLat),
            (int)Math.Floor((lng - a.OriginLng) / a.PixelLng));
}
