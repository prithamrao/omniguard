using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FloodRisk.Api.Models;

namespace FloodRisk.Api.Services;

/// <summary>
/// Calls an OpenAI-compatible chat endpoint (Groq by default) to score a parcel's
/// flood risk from its terrain features. Fully configurable via "AI" config section.
/// </summary>
public sealed class AiService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;

    public AiService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _baseUrl = (config["AI:BaseUrl"] ?? "https://api.groq.com/openai/v1").TrimEnd('/');
        _apiKey = config["AI:ApiKey"] ?? "";
        _model = config["AI:Model"] ?? "openai/gpt-oss-120b";
    }

    /// <summary>One call judges both scenarios, so the storm score can never contradict the normal one.</summary>
    public async Task<AiAssessment> ScoreAsync(FeatureSet f, FeatureSet storm, CancellationToken ct = default)
    {
        // The AI never sees the terrain model's score, so it gives an independent second opinion.
        // Describe the chosen rainfall so the AI can scale its judgment to it.
        double rain = storm.RainInches;
        string amount = $"{rain.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} inches of rain in 24 hours";
        string severity = rain >= 15 ? "an extreme, record-flood amount — far beyond what neighborhood drainage is designed for"
            : rain >= 6 ? "a severe storm, around a 100-year event for most of the US"
            : rain >= 3 ? "a very heavy storm"
            : rain > 1 ? "a heavy storm"
            : rain < 1 ? "lighter than an ordinary rain"
            : "an ordinary rain";
        string stormRule = rain > 1
            ? "runoff, ponding and overflow all grow with rainfall, so it must be at least riskScore, scaled to how extreme this rainfall is"
            : rain < 1 ? "less water than an ordinary rain, so it must be at most riskScore" : "the same as riskScore";
        string system =
            "You are an experienced stormwater engineer giving a homeowner a quick flood-risk " +
            "second opinion for one property, from measured terrain data. Judge the risk yourself " +
            "using engineering sense, for example: a house in a closed low spot or on a drainage path " +
            "that collects a large uphill area is serious even if the yard is small; steep, high ground " +
            "with little uphill drainage is low risk even when heavily paved; paving matters most when " +
            "water also has nowhere to go. Scale: 0-24 Low, 25-49 Moderate, 50-74 High, 75-100 Severe. " +
            "Return ONE JSON object with keys: riskScore (integer 0-100, for a normal 1-inch rain), " +
            $"stormRiskScore (integer 0-100, for {amount} — {severity}: {stormRule}), " +
            $"stormNote (one short sentence on what changes for this property with {amount}), " +
            "category (Low, Moderate, High or Severe, matching riskScore), summary (one short sentence naming the main reason), " +
            "issues (the specific problems, in plain words a homeowner understands, separated by " +
            "semicolons; use ONLY the facts tagged 'Concern:' under 'Observed facts' and never list an " +
            "'In its favor:' or 'Info:' fact as an issue, or claim a low spot, flat land, heavy " +
            "paving or uphill drainage that the facts do not state; if there are no Concern facts say " +
            "'no significant issues'), and recommendation (one or two practical actions " +
            "aimed at the actual issues: uphill drainage -> swale, berm or French drain to divert it; " +
            "low spot -> regrade, dry well or improve the outlet; heavy paving -> permeable paving, " +
            "downspout disconnection or a rain garden; low risk -> keep gutters and drains clear). " +
            "No jargon such as 'impervious', 'depression' or 'flow accumulation'. Return ONLY JSON.";

        var low = f.LowSpot is { } ls
            ? ls.IsWater
                ? $"runoff reaches a pond/stream {ls.DistanceM:F0} m downhill ({ls.AreaM2:F0} m2 of water)"
                : $"nearest low spot downhill: {ls.DistanceM:F0} m away, up to {ls.DepthM:F2} m deep, {ls.AreaM2:F0} m2"
            : "no closed low spot before the runoff reaches the street";
        if (f.StreetDistanceM is { } sd) low += $"; runoff reaches the street (curb, gutters, storm drains) {sd:F0} m away";
        var stormOnly = storm.Observations.Except(f.Observations).ToList();
        string user =
            $"Measurements: slope={f.Slope:F1}%, land draining through this spot={f.UpstreamAreaM2:F0} m2, " +
            $"standing-water depth at the property={f.Depression:F2} m, " +
            $"height vs. neighborhood average={-f.RelativeLow:+0.00;-0.00} m, " +
            $"paved/built-on ground={f.Imperviousness:F0}%, {low}.\n" +
            "Observed facts (normal 1-inch rain):\n- " + string.Join("\n- ", f.Observations) +
            (stormOnly.Count > 0 ? $"\nAdditional facts with {amount}:\n- " + string.Join("\n- ", stormOnly) : "");

        var body = new
        {
            model = _model,
            temperature = 0.0,
            seed = 42,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI endpoint returned {(int)resp.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        string content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        // Robustly extract the JSON object even if the model wraps it in prose/fences.
        int a = content.IndexOf('{'), b = content.LastIndexOf('}');
        if (a >= 0 && b > a) content = content.Substring(a, b - a + 1);

        using var cd = JsonDocument.Parse(content);
        var root = cd.RootElement;

        int Score(string key, int fallback) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? Math.Clamp((int)Math.Round(v.GetDouble()), 0, 100) : fallback;
        int score = Score("riskScore", (int)Math.Round(f.DeterministicRisk));
        // More rain is never safer, and less than an ordinary rain is never riskier.
        int stormRaw = Score("stormRiskScore", score);
        // Consistency floor: at least the normal-rain score plus the terrain model's rise for this rainfall,
        // so a bigger storm can never read safer than a smaller one.
        int floor = Math.Clamp(score + (int)Math.Round(storm.DeterministicRisk - f.DeterministicRisk), 0, 100);
        int stormScore = rain > 1 ? Math.Max(Math.Max(score, floor), stormRaw)
            : rain < 1 ? Math.Min(score, stormRaw) : score;
        string summary = root.TryGetProperty("summary", out var sm) ? sm.GetString() ?? "" : "";
        string issues = root.TryGetProperty("issues", out var iss) ? iss.GetString() ?? "" : "";
        string recommendation = root.TryGetProperty("recommendation", out var rc) ? rc.GetString() ?? "" : "";
        string stormNote = root.TryGetProperty("stormNote", out var sn) ? sn.GetString() ?? "" : "";

        return new AiAssessment
        {
            RiskScore = score,
            Category = AiAssessment.CategoryFor(score),
            StormRiskScore = stormScore,
            StormNote = stormNote,
            Summary = summary,
            Issues = issues,
            Recommendation = recommendation,
        };
    }
}
