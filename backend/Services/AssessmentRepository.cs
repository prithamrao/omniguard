using System.Collections.Concurrent;
using FloodRisk.Api.Models;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace FloodRisk.Api.Services;

/// <summary>A saved parcel lookup (or a confirmed "no lot here"), so repeat searches skip the paid API.</summary>
public sealed class ParcelCacheDoc
{
    [BsonId]
    public string Key { get; set; } = "";
    public bool Found { get; set; }
    public Parcel? Parcel { get; set; }
    /// <summary>Lot outline as [lng, lat] pairs (Parcel.Outline itself is not stored).</summary>
    public List<double[]> Outline { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persists assessments, parcel lookups and user-reported ponds/drains in MongoDB.
/// Fails soft: if Mongo is unreachable, the app still runs (in-memory only).
/// </summary>
public sealed class AssessmentRepository
{
    private readonly IMongoCollection<AssessmentDoc> _col;
    private readonly IMongoCollection<ParcelCacheDoc> _parcels;
    private readonly IMongoCollection<WaterReport> _reports;
    private readonly ConcurrentDictionary<string, WaterReport> _memReports = new();
    public bool Available { get; }

    public AssessmentRepository(IConfiguration config)
    {
        var cs = config["MongoDb:ConnectionString"] ?? "mongodb://localhost:27017";
        var db = config["MongoDb:DatabaseName"] ?? "floodrisk";
        try
        {
            var settings = MongoClientSettings.FromConnectionString(cs);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
            var client = new MongoClient(settings);
            var database = client.GetDatabase(db);
            _col = database.GetCollection<AssessmentDoc>("assessments");
            _parcels = database.GetCollection<ParcelCacheDoc>("parcels");
            _reports = database.GetCollection<WaterReport>("reports");
            _col.Indexes.CreateOne(new CreateIndexModel<AssessmentDoc>(
                Builders<AssessmentDoc>.IndexKeys.Geo2DSphere(x => x.Location)));
            Available = true;
        }
        catch (Exception)
        {
            _col = null!;
            _parcels = null!;
            _reports = null!;
            Available = false;
        }
    }

    public async Task<AssessmentDoc?> SaveAsync(AssessmentDoc doc)
    {
        if (!Available) return null;
        await _col.InsertOneAsync(doc);
        return doc;
    }

    /// <summary>A previous assessment with exactly these inputs (same cell, scenario and facts), if any.</summary>
    public async Task<AssessmentDoc?> FindCachedAsync(double lat, double lng, int algoVersion, string cacheKey)
    {
        if (!Available) return null;
        try
        {
            return await _col.Find(x => x.Lat == lat && x.Lng == lng && x.Features.AlgoVersion == algoVersion
                                        && x.Features.CacheKey == cacheKey && x.Ai.Available)
                .SortByDescending(x => x.Timestamp)
                .FirstOrDefaultAsync();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<List<AssessmentDoc>> GetRecentAsync(int limit = 200)
    {
        if (!Available) return new();
        return await _col.Find(Builders<AssessmentDoc>.Filter.Empty)
            .SortByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToListAsync();
    }

    public async Task<List<AssessmentDoc>> GetNearAsync(double lat, double lng, double radiusMeters, int limit = 50)
    {
        if (!Available) return new();
        var point = new GeoJsonPoint<GeoJson2DGeographicCoordinates>(
            new GeoJson2DGeographicCoordinates(lng, lat));
        var filter = Builders<AssessmentDoc>.Filter.Near(x => x.Location, point, radiusMeters);
        return await _col.Find(filter)
            .SortByDescending(x => x.Timestamp)
            .Limit(limit)
            .ToListAsync();
    }

    // ---------- parcel lookup cache ----------

    /// <summary>(true, lot) or (true, null = confirmed no lot) when cached; (false, null) when unknown.</summary>
    public async Task<(bool hit, Parcel? parcel)> GetParcelAsync(string key)
    {
        if (!Available) return (false, null);
        try
        {
            var d = await _parcels.Find(x => x.Key == key).FirstOrDefaultAsync();
            if (d is null) return (false, null);
            if (!d.Found || d.Parcel is null) return (true, null);
            d.Parcel.Outline = d.Outline.Where(p => p.Length == 2).Select(p => new Coord(p[1], p[0])).ToList();
            return (true, d.Parcel);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    public async Task SaveParcelAsync(string key, Parcel? parcel)
    {
        if (!Available) return;
        try
        {
            var d = new ParcelCacheDoc
            {
                Key = key,
                Found = parcel is not null,
                Parcel = parcel,
                Outline = parcel?.Outline.Select(c => new[] { c.Lng, c.Lat }).ToList() ?? new(),
            };
            await _parcels.ReplaceOneAsync(x => x.Key == key, d, new ReplaceOptions { IsUpsert = true });
        }
        catch (Exception)
        {
            // Cache only; a failed write just means the next lookup goes to the API again.
        }
    }

    // ---------- user-reported ponds / drains ----------

    public async Task<List<WaterReport>> GetReportsNearAsync(double lat, double lng, double radiusM)
    {
        double dLat = radiusM / 111_320, dLng = radiusM / (111_320 * Math.Cos(lat * Math.PI / 180));
        bool Inside(WaterReport r) => Math.Abs(r.Lat - lat) <= dLat && Math.Abs(r.Lng - lng) <= dLng;
        if (!Available) return _memReports.Values.Where(Inside).ToList();
        try
        {
            double s = lat - dLat, n = lat + dLat, w = lng - dLng, e = lng + dLng;
            return await _reports.Find(x => x.Lat >= s && x.Lat <= n && x.Lng >= w && x.Lng <= e).ToListAsync();
        }
        catch (Exception)
        {
            return _memReports.Values.Where(Inside).ToList();
        }
    }

    public async Task<WaterReport> AddReportAsync(WaterReport r)
    {
        _memReports[r.Id] = r;
        if (Available)
        {
            try { await _reports.InsertOneAsync(r); } catch (Exception) { /* kept in memory */ }
        }
        return r;
    }

    public async Task<bool> DeleteReportAsync(string id)
    {
        bool removed = _memReports.TryRemove(id, out _);
        if (Available)
        {
            try { removed |= (await _reports.DeleteOneAsync(x => x.Id == id)).DeletedCount > 0; } catch (Exception) { }
        }
        return removed;
    }
}
