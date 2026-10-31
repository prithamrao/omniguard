using MongoDB.Bson.Serialization.Attributes;

namespace FloodRisk.Api.Models;

public sealed record Coord(double Lat, double Lng);

public sealed class RiskPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }
    public double Risk { get; set; }
    public double FlowAcc { get; set; }
    public double Slope { get; set; }
    public double Elevation { get; set; }
    public double Depression { get; set; }
    public double Impervious { get; set; }
}

public sealed class MapData
{
    public int Rows { get; set; }
    public int Cols { get; set; }
    public double OriginLat { get; set; }
    public double OriginLng { get; set; }
    public double PixelLat { get; set; }
    public double PixelLng { get; set; }
    public List<RiskPoint> Points { get; set; } = new();
}

/// <summary>Proof of a passed human check, traded for a session pass: a typed code, or a Turnstile token.</summary>
public sealed class SessionRequest
{
    public string Token { get; set; } = "";
    public string CaptchaId { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class AssessRequest
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    /// <summary>House number from a searched address, so the marker snaps to that exact house (not a neighbor).</summary>
    public string? HouseNumber { get; set; }
    /// <summary>Street name from a searched address (e.g. "Main Street"), to pick the right lot.</summary>
    public string? Street { get; set; }
    /// <summary>"normal" (1 in of rain) or "storm" (2.5 in) — used when RainInches isn't given.</summary>
    public string? Scenario { get; set; }
    /// <summary>24-hour rainfall to assess, 0-50 inches. Overrides Scenario.</summary>
    public double? RainInches { get; set; }
}

/// <summary>A pond or storm drain a user marked on the map (newer than the elevation survey / map data).</summary>
public sealed class WaterReport
{
    [BsonId]
    [BsonRepresentation(MongoDB.Bson.BsonType.ObjectId)]
    public string Id { get; set; } = MongoDB.Bson.ObjectId.GenerateNewId().ToString();
    public double Lat { get; set; }
    public double Lng { get; set; }
    /// <summary>"pond" or "drain".</summary>
    public string Kind { get; set; } = "pond";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>An official property lot (county parcel record).</summary>
public sealed class Parcel
{
    public string Address { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    [BsonIgnore]
    public List<Coord> Outline { get; set; } = new();
    public double LotAreaM2 { get; set; }
    public int? YearBuilt { get; set; }
    /// <summary>Building floor area from county records, sq ft (null when unknown).</summary>
    public double? BuildingSqft { get; set; }
    /// <summary>Estimated paved/roofed share of the lot, percent (null when there is nothing to estimate from).</summary>
    public double? ImperviousPct { get; set; }
    /// <summary>How ImperviousPct was estimated, in plain words.</summary>
    public string ImperviousSource { get; set; } = "";
}

/// <summary>A closed low spot where runoff ponds before it can drain away.</summary>
public sealed class LowSpot
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    /// <summary>Deepest standing-water depth in the low spot, meters.</summary>
    public double DepthM { get; set; }
    public double AreaM2 { get; set; }
    /// <summary>True when this is a mapped pond, lake or stream (depth unknown) rather than a dry low spot found in the terrain.</summary>
    public bool IsWater { get; set; }
    /// <summary>True when runoff reaches a storm drain (water leaves the surface here).</summary>
    public bool IsDrain { get; set; }
    /// <summary>True when this came from a user report rather than map / elevation data.</summary>
    public bool Reported { get; set; }
    /// <summary>Approximate volume the low spot holds before it overflows, m³ (dry low spots only).</summary>
    public double? CapacityM3 { get; set; }
    /// <summary>Approximate runoff reaching it in the selected rain scenario, m³.</summary>
    public double? InflowM3 { get; set; }
    public bool Overflows { get; set; }
    /// <summary>True when the runoff reaches this pond through the street's storm drains (likely outfall).</summary>
    public bool ViaStreet { get; set; }
    /// <summary>True when the back yard drains toward this pond (lot-grading route for homes newer than the survey).</summary>
    public bool ViaRearYard { get; set; }
    /// <summary>Land draining into this low spot, m².</summary>
    public double CatchmentM2 { get; set; }
    /// <summary>Distance along the runoff path from the parcel, meters (0 = the parcel itself sits in it).</summary>
    public double DistanceM { get; set; }
    /// <summary>Cell centers covered by the low spot, for drawing its outline (not stored).</summary>
    [BsonIgnore]
    public List<Coord> Cells { get; set; } = new();
}

/// <summary>The other hazards around an address (air, FEMA flood zone, fire, shelters/hospitals).</summary>
public sealed class Hazards
{
    public AirQuality? Air { get; set; }
    public FloodZoneInfo? FloodZone { get; set; }
    public FireInfo? Fire { get; set; }
    public List<Facility> Shelters { get; set; } = new();
    public List<Facility> Hospitals { get; set; } = new();
    /// <summary>FEMA shelters open right now within 50 km (during a declared emergency).</summary>
    public int OpenShelterCount { get; set; }
}

public sealed class AirQuality
{
    public int Aqi { get; set; }
    public string Category { get; set; } = "";
    public double? Pm25 { get; set; }
    public string AsOf { get; set; } = "";
    /// <summary>Daily maximum US AQI for the last 7 days, oldest first.</summary>
    public List<AirDay> History { get; set; } = new();
}

public sealed class AirDay
{
    public string Date { get; set; } = "";
    public int MaxAqi { get; set; }
}

public sealed class FloodZoneInfo
{
    public string Zone { get; set; } = "";
    public string Subtype { get; set; } = "";
    /// <summary>True for FEMA's Special Flood Hazard Area (the 1%-a-year, "100-year" floodplain).</summary>
    public bool HighRisk { get; set; }
    public string Meaning { get; set; } = "";
    /// <summary>FEMA zone outlines around the address (GeoJSON), for the map.</summary>
    public System.Text.Json.Nodes.JsonNode? Overlay { get; set; }
}

public sealed class FireInfo
{
    public double SearchKm { get; set; }
    public List<FireEvent> Active { get; set; } = new();
    public List<FireEvent> Past { get; set; } = new();
    /// <summary>Fire perimeters (GeoJSON, properties.active true/false), for the map.</summary>
    public System.Text.Json.Nodes.JsonNode? Overlay { get; set; }
}

public sealed class FireEvent
{
    public string Name { get; set; } = "";
    public double? Acres { get; set; }
    public int? Year { get; set; }
    public double DistanceKm { get; set; }
    public bool Active { get; set; }
}

public sealed class Facility
{
    /// <summary>"shelter" or "hospital".</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool Emergency { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double DistanceKm { get; set; }
}

/// <summary>Tap-water quality summary for the public water system serving an address (EPA data).</summary>
public sealed class TapWater
{
    /// <summary>"ok", "no-system" (likely a private well) or "unavailable" (EPA records unreachable).</summary>
    public string Status { get; set; } = "ok";
    /// <summary>"good", "watch" (past, resolved problems) or "alert" (open problem or metals over the action level).</summary>
    public string Level { get; set; } = "";
    public string Headline { get; set; } = "";
    public string SystemName { get; set; } = "";
    public string Pwsid { get; set; } = "";
    public int? PopulationServed { get; set; }
    /// <summary>False when the address is outside every mapped boundary and the nearest system was used.</summary>
    public bool InsideBoundary { get; set; } = true;
    public int HealthWindowYears { get; set; }
    public List<TapWaterViolation> HealthViolations { get; set; } = new();
    public int OpenHealthViolations { get; set; }
    public int PaperworkLapses5Yr { get; set; }
    public TapWaterMetal? Lead { get; set; }
    public TapWaterMetal? Copper { get; set; }
    public List<string> Notes { get; set; } = new();
    public string ReportUrl { get; set; } = "";
    /// <summary>When EPA last refreshed the drinking-water records (MM/DD/YYYY).</summary>
    public string DataAsOf { get; set; } = "";
}

public sealed class TapWaterViolation
{
    public string Contaminant { get; set; } = "";
    public string What { get; set; } = "";
    public string Period { get; set; } = "";
    public bool Resolved { get; set; }
    public string? ResolvedOn { get; set; }
    /// <summary>How many identical records (e.g. one per facility) this line stands for.</summary>
    public int Count { get; set; } = 1;
}

/// <summary>90th percentile of home tap samples in the latest monitoring period, vs EPA's action level.</summary>
public sealed class TapWaterMetal
{
    public double Ppb { get; set; }
    public double ActionLevelPpb { get; set; }
    public string Period { get; set; } = "";
    public bool OverActionLevel { get; set; }
}

/// <summary>How a lot drains on the current surface (Google Solar API aerial DSM).</summary>
public sealed class LotDrainage
{
    /// <summary>Year-month of the aerial imagery the surface was built from.</summary>
    public string ImageryDate { get; set; } = "";
    /// <summary>Share of the lot whose runoff leaves toward the street it faces, percent.</summary>
    public double FrontSharePct { get; set; }
    /// <summary>Share of the lot whose runoff leaves toward the back and sides, percent.</summary>
    public double BackSharePct { get; set; }
    [BsonIgnore]
    public List<Coord> FrontPath { get; set; } = new();
    [BsonIgnore]
    public List<Coord> BackPath { get; set; } = new();
}

/// <summary>One issue pinned to the place on the map it actually refers to.</summary>
public sealed class Hotspot
{
    /// <summary>"home" (problems at the house), "lowspot" (where water pools) or "inflow" (where uphill water comes from).</summary>
    public string Kind { get; set; } = "";
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string Title { get; set; } = "";
    public List<string> Details { get; set; } = new();
    /// <summary>Optional line to draw with the pin (e.g. the uphill path water takes to reach the house).</summary>
    public List<Coord> Path { get; set; } = new();
}

public sealed class FeatureSet
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double Elevation { get; set; }
    /// <summary>Slope, percent.</summary>
    public double Slope { get; set; }
    public double FlowAccumulation { get; set; }
    /// <summary>Land draining through this spot, m².</summary>
    public double UpstreamAreaM2 { get; set; }
    /// <summary>Standing-water depth at the parcel, meters (0 = drains freely).</summary>
    public double Depression { get; set; }
    /// <summary>Meters below the ~200 m neighborhood average (negative = higher than neighbors).</summary>
    public double RelativeLow { get; set; }
    public double Imperviousness { get; set; }
    public double DeterministicRisk { get; set; }
    public double? SnappedLat { get; set; }
    public double? SnappedLng { get; set; }
    public LowSpot? LowSpot { get; set; }
    /// <summary>Where runoff first reaches a street (curb and gutter → storm drains), if it does.</summary>
    public Coord? Street { get; set; }
    public double? StreetDistanceM { get; set; }
    /// <summary>Index in the flow path where it reaches the street; points after it follow the storm drains (approximate).</summary>
    public int? StreetPathIndex { get; set; }
    /// <summary>Year of the newest public elevation survey here (USGS 3DEP lidar index).</summary>
    public int? SurveyYear { get; set; }
    /// <summary>True when the home was built after the survey, so the survey shows the land before grading.</summary>
    public bool TerrainPredatesHome { get; set; }
    /// <summary>Lot drainage from the current aerial surface, when available.</summary>
    public LotDrainage? Surface { get; set; }
    /// <summary>Runoff route drawn on the map (not stored).</summary>
    [BsonIgnore]
    public List<Coord> RoutePath { get; set; } = new();
    /// <summary>The official lot that was assessed, when parcel data covers this location.</summary>
    public Parcel? Parcel { get; set; }
    /// <summary>Plain-language facts derived from the numbers (ground truth for the AI's wording).</summary>
    public List<string> Observations { get; set; } = new();
    /// <summary>Issues placed on the map (recomputed per request, not stored).</summary>
    [BsonIgnore]
    public List<Hotspot> Hotspots { get; set; } = new();
    public int AlgoVersion { get; set; }
    /// <summary>"normal" (the 1-inch baseline) or "storm" (any other rainfall).</summary>
    public string Scenario { get; set; } = "normal";
    /// <summary>24-hour rainfall this assessment is for, inches.</summary>
    public double RainInches { get; set; } = 1.0;
    /// <summary>Identifies the exact inputs the AI saw, so a cached AI answer is reused only for identical facts.</summary>
    public string CacheKey { get; set; } = "";
}

public sealed class AiAssessment
{
    public bool Available { get; set; } = true;
    public int RiskScore { get; set; }
    /// <summary>The AI's score for the chosen rainfall (never below RiskScore when it's more than 1 inch,
    /// never above it when less).</summary>
    public int StormRiskScore { get; set; }
    /// <summary>What changes for this property at the chosen rainfall.</summary>
    public string StormNote { get; set; } = "";

    public static string CategoryFor(int score) => score >= 75 ? "Severe" : score >= 50 ? "High" : score >= 25 ? "Moderate" : "Low";

    /// <summary>The view for a rainfall: at anything other than the 1-inch baseline the headline score is the
    /// score for that rainfall.</summary>
    public AiAssessment ForRain(double rainInches) => rainInches == 1.0 || !Available ? this : new AiAssessment
    {
        Available = Available, RiskScore = StormRiskScore, StormRiskScore = StormRiskScore, StormNote = StormNote,
        Category = CategoryFor(StormRiskScore), Summary = Summary, Issues = Issues, Recommendation = Recommendation,
    };
    public string Category { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Issues { get; set; } = "";
    public string Recommendation { get; set; } = "";
}

public sealed class AssessResponse
{
    public FeatureSet Features { get; set; } = new();
    public AiAssessment Ai { get; set; } = new();
}

public sealed class GeocodeResult
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string MatchedAddress { get; set; } = "";
    public string? HouseNumber { get; set; }
}

public sealed class SuggestResult
{
    public string Label { get; set; } = "";
    public string? HouseNumber { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
}
