using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver.GeoJsonObjectModel;

namespace FloodRisk.Api.Models;

/// <summary>A saved flood-risk assessment (one per map click).</summary>
public sealed class AssessmentDoc
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    public double Lat { get; set; }
    public double Lng { get; set; }

    /// <summary>GeoJSON point for 2dsphere "near me" queries. Order is [lng, lat].</summary>
    [JsonIgnore]
    public GeoJsonPoint<GeoJson2DGeographicCoordinates> Location { get; set; } = default!;

    public FeatureSet Features { get; set; } = new();
    public AiAssessment Ai { get; set; } = new();

    /// <summary>Downhill runoff path from the parcel (recomputed per request, not stored).</summary>
    [BsonIgnore]
    public List<Coord> FlowPath { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
