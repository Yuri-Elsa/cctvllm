using System.Text;
using System.Text.Json;

public sealed record LogprobToken(string Token, double LogProb, IReadOnlyList<(string Token, double LogProb)> Top);

public static class Scoring
{
    public const int TopLogprobs = 5;

    public static (string Decision, double? Score) Decide(string modelAnswer, double? score, double threshold) =>
        score is { } s ? (s >= threshold ? "yes" : "no", s) : (modelAnswer, null);

    public static double Effective(string modelAnswer, double? score) =>
        score ?? (modelAnswer == "yes" ? 1d : 0d);

    public static double? PYes(JsonElement choice, string key)
    {
        var tokens = ReadTokens(choice);
        return tokens is null ? null : PYes(tokens, key);
    }

    public static IReadOnlyList<LogprobToken>? ReadTokens(JsonElement choice)
    {
        if (!choice.TryGetProperty("logprobs", out var logprobs) || logprobs.ValueKind != JsonValueKind.Object ||
            !logprobs.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;
        var tokens = new List<LogprobToken>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                return null;
            var top = new List<(string Token, double LogProb)>();
            if (item.TryGetProperty("top_logprobs", out var alternatives) && alternatives.ValueKind == JsonValueKind.Array)
                foreach (var alt in alternatives.EnumerateArray())
                    if (alt.ValueKind == JsonValueKind.Object && alt.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                        top.Add((t.GetString() ?? "", Number(alt)));
            tokens.Add(new LogprobToken(token.GetString() ?? "", Number(item), top));
        }
        return tokens;
    }

    private static double Number(JsonElement element) =>
        element.TryGetProperty("logprob", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : double.NegativeInfinity;

    public static double? PYes(IReadOnlyList<LogprobToken> tokens, string key)
    {
        if (tokens.Count == 0) return null;
        var text = new StringBuilder();
        var starts = new int[tokens.Count];
        for (var i = 0; i < tokens.Count; i++) { starts[i] = text.Length; text.Append(tokens[i].Token); }
        var all = text.ToString();

        var at = all.LastIndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (at < 0) return null;
        var colon = all.IndexOf(':', at + key.Length + 2);
        if (colon < 0) return null;
        var quote = all.IndexOf('"', colon + 1);
        if (quote < 0) return null;
        var valueStart = quote + 1;

        var index = -1;
        for (var i = 0; i < tokens.Count; i++)
            if (starts[i] <= valueStart && valueStart < starts[i] + tokens[i].Token.Length) { index = i; break; }
        if (index < 0) return null;

        var candidates = tokens[index].Top.Count > 0
            ? tokens[index].Top
            : new List<(string Token, double LogProb)> { (tokens[index].Token, tokens[index].LogProb) };
        double yes = 0, no = 0;
        foreach (var (token, logProb) in candidates)
        {
            if (double.IsNaN(logProb)) continue;
            var mass = Math.Exp(logProb);
            switch (Classify(token))
            {
                case true: yes += mass; break;
                case false: no += mass; break;
            }
        }
        var total = yes + no;
        return total > 0 ? yes / total : null;
    }

    private static bool? Classify(string token)
    {
        var t = token.Trim().Trim('"').Trim().ToLowerInvariant();
        return t switch { "yes" => true, "no" => false, _ => null };
    }
}