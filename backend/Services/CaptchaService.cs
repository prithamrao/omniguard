using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FloodRisk.Api.Services;

/// <summary>
/// Typed-code captcha: the server draws a short random code as a distorted SVG image and keeps the
/// answer in memory. Characters are drawn as wobbly strokes (no text in the SVG), each code expires
/// after 5 minutes and is single use — any attempt, right or wrong, consumes it.
/// </summary>
public sealed class CaptchaService
{
    private const int Length = 5;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    // No look-alikes (0/O, 1/I/L, 8/B, S/5, G/6, Z/2, Q).
    private const string Alphabet = "2345679ACEFHKMNPRTUVWXY";

    private readonly ConcurrentDictionary<string, (string answer, DateTime expires)> _pending = new();

    public (string id, string imageDataUri) Create()
    {
        foreach (var kv in _pending)
            if (kv.Value.expires < DateTime.UtcNow) _pending.TryRemove(kv.Key, out _);

        var code = new string(Enumerable.Range(0, Length).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _pending[id] = (code, DateTime.UtcNow.Add(Lifetime));
        var svg = Draw(code);
        return (id, "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)));
    }

    /// <summary>Checks an answer (case and spaces ignored). Always consumes the code.</summary>
    public bool Verify(string? id, string? answer)
    {
        if (string.IsNullOrEmpty(id) || !_pending.TryRemove(id, out var entry)) return false;
        if (entry.expires < DateTime.UtcNow || answer is null) return false;
        var given = new string(answer.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(entry.answer));
    }

    // ---------- drawing ----------

    // Stroke font on a 10 x 14 grid (y down); each glyph is one or more polylines.
    private static readonly Dictionary<char, int[][]> Glyphs = new()
    {
        ['2'] = new[] { new[] { 1, 3, 3, 1, 7, 1, 9, 3, 9, 5, 1, 13, 9, 13 } },
        ['3'] = new[] { new[] { 1, 2, 4, 1, 8, 1, 9, 3, 9, 5, 7, 7, 4, 7 }, new[] { 7, 7, 9, 9, 9, 11, 7, 13, 3, 13, 1, 12 } },
        ['4'] = new[] { new[] { 7, 13, 7, 1, 1, 9, 9, 9 } },
        ['5'] = new[] { new[] { 9, 1, 2, 1, 1, 6, 6, 6, 9, 8, 9, 11, 7, 13, 3, 13, 1, 12 } },
        ['6'] = new[] { new[] { 8, 1, 4, 1, 1, 5, 1, 11, 3, 13, 7, 13, 9, 11, 9, 8, 7, 6, 3, 6, 1, 8 } },
        ['7'] = new[] { new[] { 1, 1, 9, 1, 4, 13 } },
        ['9'] = new[] { new[] { 9, 6, 7, 8, 3, 8, 1, 6, 1, 3, 3, 1, 7, 1, 9, 3, 9, 9, 6, 13, 2, 13 } },
        ['A'] = new[] { new[] { 1, 13, 5, 1, 9, 13 }, new[] { 3, 8, 7, 8 } },
        ['C'] = new[] { new[] { 9, 3, 7, 1, 3, 1, 1, 4, 1, 10, 3, 13, 7, 13, 9, 11 } },
        ['E'] = new[] { new[] { 9, 1, 1, 1, 1, 13, 9, 13 }, new[] { 1, 7, 7, 7 } },
        ['F'] = new[] { new[] { 9, 1, 1, 1, 1, 13 }, new[] { 1, 7, 7, 7 } },
        ['H'] = new[] { new[] { 1, 1, 1, 13 }, new[] { 9, 1, 9, 13 }, new[] { 1, 7, 9, 7 } },
        ['K'] = new[] { new[] { 1, 1, 1, 13 }, new[] { 9, 1, 1, 8 }, new[] { 3, 6, 9, 13 } },
        ['M'] = new[] { new[] { 1, 13, 1, 1, 5, 8, 9, 1, 9, 13 } },
        ['N'] = new[] { new[] { 1, 13, 1, 1, 9, 13, 9, 1 } },
        ['P'] = new[] { new[] { 1, 13, 1, 1, 7, 1, 9, 3, 9, 5, 7, 7, 1, 7 } },
        ['R'] = new[] { new[] { 1, 13, 1, 1, 7, 1, 9, 3, 9, 5, 7, 7, 1, 7 }, new[] { 5, 7, 9, 13 } },
        ['T'] = new[] { new[] { 1, 1, 9, 1 }, new[] { 5, 1, 5, 13 } },
        ['U'] = new[] { new[] { 1, 1, 1, 10, 3, 13, 7, 13, 9, 10, 9, 1 } },
        ['V'] = new[] { new[] { 1, 1, 5, 13, 9, 1 } },
        ['W'] = new[] { new[] { 1, 1, 3, 13, 5, 6, 7, 13, 9, 1 } },
        ['X'] = new[] { new[] { 1, 1, 9, 13 }, new[] { 9, 1, 1, 13 } },
        ['Y'] = new[] { new[] { 1, 1, 5, 7, 9, 1 }, new[] { 5, 7, 5, 13 } },
    };

    private static readonly string[] Inks = { "#14324d", "#5a1f3a", "#1f4d2e", "#4a2a12", "#2b2b6b" };

    private static double R(double min, double max) => min + RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000.0 * (max - min);

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string Draw(string code)
    {
        const int w = 260, h = 90;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}' viewBox='0 0 {w} {h}'>");
        sb.Append($"<rect width='{w}' height='{h}' fill='#f3f1e9'/>");

        // Background speckle.
        for (int i = 0; i < 70; i++)
            sb.Append($"<circle cx='{F(R(0, w))}' cy='{F(R(0, h))}' r='{F(R(0.6, 1.8))}' fill='#9aa3ad' opacity='{F(R(0.25, 0.6))}'/>");

        // Characters: jittered strokes, each with its own scale, rotation and skew.
        for (int i = 0; i < code.Length; i++)
        {
            double scale = R(3.6, 4.4), cx = 22 + i * 46 + R(-4, 4), cy = h / 2.0 + R(-6, 6);
            double rot = R(-22, 22), skew = R(-12, 12);
            string ink = Inks[RandomNumberGenerator.GetInt32(Inks.Length)];
            sb.Append($"<g transform='translate({F(cx)} {F(cy)}) rotate({F(rot)}) skewX({F(skew)}) scale({F(scale)}) translate(-5 -7)' ");
            sb.Append($"fill='none' stroke='{ink}' stroke-width='{F(R(0.95, 1.25))}' stroke-linecap='round' stroke-linejoin='round'>");
            foreach (var line in Glyphs[code[i]])
            {
                sb.Append("<path d='");
                for (int k = 0; k < line.Length; k += 2)
                    sb.Append(k == 0 ? "M" : " L").Append(F(line[k] + R(-0.45, 0.45))).Append(' ').Append(F(line[k + 1] + R(-0.45, 0.45)));
                sb.Append("'/>");
            }
            sb.Append("</g>");
        }

        // Crossing noise curves over the code.
        for (int i = 0; i < 5; i++)
        {
            sb.Append($"<path d='M{F(R(-10, 30))} {F(R(10, h - 10))} C{F(R(40, 110))} {F(R(-20, h + 20))} {F(R(150, 220))} {F(R(-20, h + 20))} {F(R(230, w + 10))} {F(R(10, h - 10))}' ");
            sb.Append($"fill='none' stroke='{Inks[RandomNumberGenerator.GetInt32(Inks.Length)]}' stroke-width='{F(R(1, 2.2))}' opacity='{F(R(0.35, 0.6))}'/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
