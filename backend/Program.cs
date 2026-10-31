using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using FloodRisk.Api.Models;
using FloodRisk.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using MongoDB.Driver.GeoJsonObjectModel;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);   // map outlines are large, mostly repeated JSON

builder.Services.AddHttpClient<AiService>();
builder.Services.AddHttpClient<GeocodingService>();
builder.Services.AddHttpClient<ParcelService>();
builder.Services.AddHttpClient<SolarSurfaceService>();
builder.Services.AddHttpClient<TapWaterService>();
builder.Services.AddHttpClient<HazardsService>();
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<DemService>();
builder.Services.AddSingleton<ImperviousService>();
builder.Services.AddSingleton<AreaService>();
builder.Services.AddSingleton<TerrainService>();
builder.Services.AddSingleton<AssessmentRepository>();

// Human check (Cloudflare Turnstile) — one instance, so issued passes stay valid.
builder.Services.AddHttpClient("turnstile");
builder.Services.AddSingleton<CaptchaService>();
builder.Services.AddSingleton(sp => new HumanCheckService(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("turnstile"),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<HumanCheckService>>()));

// Real client IP behind Render's proxy, for per-IP limits.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

// Per-IP rate limits, so even a verified visitor can't drain the paid APIs (AI, parcels, aerial surface).
static string ClientIp(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
static RateLimitPartition<string> PerIp(HttpContext ctx, int perMinute) =>
    RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = perMinute,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
    });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests — please wait a minute and try again." }, ct);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/api") ? PerIp(ctx, 120) : RateLimitPartition.GetNoLimiter("other"));
    o.AddPolicy("expensive", ctx => PerIp(ctx, 20));   // each assessment can call AI + parcel + aerial APIs
    o.AddPolicy("verify", ctx => PerIp(ctx, 10));
    o.AddPolicy("captcha", ctx => PerIp(ctx, 30));
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseCors();
app.UseRateLimiter();

// Every /api call needs a valid session pass once the human check is on (except getting a code and a pass).
var human = app.Services.GetRequiredService<HumanCheckService>();
app.Use(async (ctx, next) =>
{
    if (human.Required
        && ctx.Request.Path.StartsWithSegments("/api")
        && !ctx.Request.Path.StartsWithSegments("/api/session")
        && !ctx.Request.Path.StartsWithSegments("/api/captcha")
        && !HttpMethods.IsOptions(ctx.Request.Method)
        && !human.IsValidPass(ctx.Request.Headers["X-Session"]))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "verification required" });
        return;
    }
    await next();
});

// Human check: the page asks whether it's needed and which kind, then trades a typed code
// (or a Turnstile token) for a session pass.
app.MapGet("/api/session/config", () => Results.Ok(new { required = human.Required, mode = human.Mode, siteKey = human.SiteKey }));

app.MapGet("/api/captcha", (CaptchaService captcha) =>
{
    var (id, image) = captcha.Create();
    return Results.Ok(new { id, image });
}).RequireRateLimiting("captcha");

app.MapPost("/api/session", async (SessionRequest req, HttpContext ctx, CaptchaService captcha, CancellationToken ct) =>
{
    if (!human.Required) return Results.Ok(new { pass = "", expiresAt = DateTimeOffset.UtcNow.Add(HumanCheckService.SessionLifetime) });
    bool passed = human.Mode == "code"
        ? captcha.Verify(req.CaptchaId, req.Answer)
        : await human.VerifyAsync(req.Token, ctx.Connection.RemoteIpAddress?.ToString(), ct);
    if (!passed)
        return Results.Json(new { error = human.Mode == "code" ? "That code didn’t match — here’s a new one." : "Verification failed — please try again." },
            statusCode: StatusCodes.Status403Forbidden);
    var (pass, expires) = human.IssuePass();
    return Results.Ok(new { pass, expiresAt = expires });
}).RequireRateLimiting("verify");

// Precomputed Fuquay-Varina grid (used only by the legacy /api/map).
var terrain = app.Services.GetRequiredService<TerrainService>();
terrain.Initialize();

// AI result cache (per grid cell) so repeat queries are deterministic.
var aiCache = new ConcurrentDictionary<string, AssessmentDoc>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/geocode", async (GeocodingService geo, string address, CancellationToken ct) =>
{
    var result = await geo.GeocodeAsync(address, ct);
    return result is null
        ? Results.Json(new { error = "Address not found" }, statusCode: 404)
        : Results.Ok(result);
});

app.MapGet("/api/suggest", async (GeocodingService geo, string q, double? lat, double? lng, CancellationToken ct) =>
    Results.Ok(await geo.SuggestAsync(q, lat, lng, ct)));

// Legacy fixed grid (Fuquay-Varina).
app.MapGet("/api/map", (TerrainService t, int stride = 3) => t.GetMapData(stride));

// Dynamic grid: elevation + hydrology for any world location, on demand.
app.MapGet("/api/map/area", async (AreaService area, double lat, double lng, int stride = 6, CancellationToken ct = default) =>
{
    var a = await area.GetAsync(lat, lng, ct);
    if (a is null) return Results.Json(new { error = "No elevation data for this location" }, statusCode: 502);
    return Results.Ok(area.BuildMapData(a, stride));
});

app.MapGet("/api/flowpath", async (AreaService area, double lat, double lng, CancellationToken ct = default) =>
{
    var a = await area.GetAsync(lat, lng, ct);
    if (a is null) return Results.Json(new { error = "No elevation data for this location" }, statusCode: 502);
    return Results.Ok(area.TraceFlowPath(a, lat, lng));
});

app.MapPost("/api/assess", async (AreaService area, AiService ai, ParcelService parcels, SolarSurfaceService solar, AssessmentRepository repo, AssessRequest req, CancellationToken ct) =>
{
    var a = await area.GetAsync(req.Lat, req.Lng, ct);
    if (a is null) return Results.Json(new { error = "No elevation data for this location" }, statusCode: 502);

    // Assess the actual house the user meant. Best: the official county lot (exact address for searches,
    // the lot under the cursor for clicks). Fallback: OSM house-number points, then the raw point.
    var parcel = string.IsNullOrWhiteSpace(req.HouseNumber)
        ? await parcels.FindAtAsync(req.Lat, req.Lng, ct)
        : await parcels.FindByAddressAsync(req.Lat, req.Lng, req.HouseNumber, req.Street, ct);
    var snapped = parcel is not null ? (parcel.Lat, parcel.Lng) : area.Snap(a, req.Lat, req.Lng, req.HouseNumber);
    var (lat, lng) = snapped ?? (req.Lat, req.Lng);
    var reports = await repo.GetReportsNearAsync(lat, lng, 1500);
    // Current lot grading from the aerial surface (Google Solar API), when configured and a lot is known.
    LotDrainage? surface = null;
    if (parcel is not null && solar.Enabled)
        surface = await solar.AnalyzeLotAsync(parcel, area.NearestStreet(a, new Coord(parcel.Lat, parcel.Lng)), ct);
    // Rainfall: an explicit amount (0-50 in), else the old scenario names (storm = 2.5 in).
    double rain = req.RainInches is { } r && double.IsFinite(r)
        ? Math.Round(Math.Clamp(r, 0, AreaService.MaxRainInches), 2)
        : string.Equals(req.Scenario, "storm", StringComparison.OrdinalIgnoreCase) ? 2.5 : AreaService.NormalRainInches;
    var opts = new AssessOptions(parcel, rain, reports, surface);
    // Both scenarios are computed: the AI judges them together, the response shows the requested one.
    // The AI judges the ordinary 1-inch rain and the chosen rainfall together; the response shows the chosen one.
    var normal = area.GetFeatures(a, lat, lng, snapped, opts with { RainInches = AreaService.NormalRainInches });
    var storm = rain == AreaService.NormalRainInches ? normal : area.GetFeatures(a, lat, lng, snapped, opts);
    if (!ReferenceEquals(storm, normal)) normal.CacheKey = storm.CacheKey = $"{normal.CacheKey}|{storm.CacheKey}";
    var features = storm;
    var flowPath = features.RoutePath;
    if (parcel is not null)
    {
        foreach (var f in new[] { normal, storm }.Distinct())
        {
            f.Parcel = parcel;
            var home = f.Hotspots.First(h => h.Kind == "home");
            home.Title = $"{parcel.Address} — {home.Title.ToLowerInvariant()}";
        }
    }

    // Same cell + same scenario + same facts -> same AI answer, across clicks and restarts (memory, then Mongo).
    // A new report or lot data changes the facts, so the AI re-evaluates instead of serving a stale answer.
    var aiKey = $"{Math.Round(features.Lat * 1e6)}|{Math.Round(features.Lng * 1e6)}|{features.CacheKey}";
    if (!aiCache.TryGetValue(aiKey, out var previous))
        previous = await repo.FindCachedAsync(features.Lat, features.Lng, AreaService.AlgoVersion, features.CacheKey);
    var aiResult = previous?.Ai;
    if (aiResult is null)
    {
        try
        {
            aiResult = await ai.ScoreAsync(normal, storm, ct);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "AI scoring failed");
            aiResult = new AiAssessment { Available = false, Summary = "AI second opinion is unavailable right now — showing the terrain model only." };
        }
    }

    var doc = previous ?? new AssessmentDoc
    {
        Lat = features.Lat,
        Lng = features.Lng,
        Location = new GeoJsonPoint<GeoJson2DGeographicCoordinates>(
            new GeoJson2DGeographicCoordinates(features.Lng, features.Lat)),
        Features = features,
        Ai = aiResult,
    };
    if (previous is null && aiResult.Available)
    {
        try { doc = await repo.SaveAsync(doc) ?? doc; }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Saving assessment failed"); }
    }
    if (aiResult.Available) aiCache[aiKey] = doc;
    // Respond with a copy: the cached doc is shared, and the scenario view differs per request.
    return Results.Ok(new AssessmentDoc
    {
        Id = doc.Id,
        Lat = doc.Lat,
        Lng = doc.Lng,
        Location = doc.Location,
        Timestamp = doc.Timestamp,
        Features = features,   // always freshly computed (incl. low-spot outline)
        Ai = aiResult.ForRain(features.RainInches),
        FlowPath = flowPath,
    });
}).RequireRateLimiting("expensive");

// Tap-water quality for the public water system serving a point (EPA boundaries, SDWIS, ECHO).
app.MapGet("/api/tapwater", async (TapWaterService tap, double lat, double lng, CancellationToken ct) =>
    Results.Ok(await tap.GetAsync(lat, lng, ct)));

// Air quality, FEMA flood zone, fire perimeters, shelters and hospitals around a point.
app.MapGet("/api/hazards", async (HazardsService hazards, double lat, double lng, CancellationToken ct) =>
    Results.Ok(await hazards.GetAsync(lat, lng, ct)));

// Resident reports: ponds / storm drains newer than the elevation survey and map data.
app.MapGet("/api/reports/near", async (AssessmentRepository repo, double lat, double lng, double radius = 2000) =>
    Results.Ok(await repo.GetReportsNearAsync(lat, lng, Math.Min(radius, 5000))));

app.MapPost("/api/reports", async (AssessmentRepository repo, WaterReport r) =>
{
    if (r.Kind is not ("pond" or "drain")) return Results.BadRequest(new { error = "kind must be 'pond' or 'drain'" });
    if (r.Lat is < -90 or > 90 || r.Lng is < -180 or > 180) return Results.BadRequest(new { error = "invalid location" });
    var saved = await repo.AddReportAsync(new WaterReport { Lat = r.Lat, Lng = r.Lng, Kind = r.Kind });
    return Results.Ok(saved);
}).RequireRateLimiting("expensive");

app.MapDelete("/api/reports/{id}", async (AssessmentRepository repo, string id) =>
    await repo.DeleteReportAsync(id) ? Results.Ok() : Results.NotFound());

app.MapGet("/api/assessments", async (AssessmentRepository repo, int limit = 200) =>
    Results.Ok(await repo.GetRecentAsync(limit)));

app.MapGet("/api/assessments/near", async (AssessmentRepository repo, double lat, double lng, double radius = 800, int limit = 50) =>
    Results.Ok(await repo.GetNearAsync(lat, lng, radius, limit)));

// Port is driven by ASPNETCORE_URLS (docker-compose sets http://+:8080, Render sets http://+:10000).
app.Run();
