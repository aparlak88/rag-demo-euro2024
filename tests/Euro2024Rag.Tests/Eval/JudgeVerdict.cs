using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Euro2024Rag.Tests.Eval;

public sealed record JudgeVerdict(bool Correct, bool Faithful, string Reason)
{
    /// <summary>
    /// Finds the first balanced {...} object in <paramref name="text"/> (tolerating prose and code fences)
    /// and reads correct/faithful/reason case-insensitively.
    /// </summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out JudgeVerdict? verdict)
    {
        verdict = null;
        var json = FirstObject(text);
        if (json is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            bool? correct = null, faithful = null;
            var reason = "";
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("correct", StringComparison.OrdinalIgnoreCase))
                    correct = Bool(p.Value);
                else if (p.Name.Equals("faithful", StringComparison.OrdinalIgnoreCase))
                    faithful = Bool(p.Value);
                else if (p.Name.Equals("reason", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    reason = p.Value.GetString() ?? "";
            }

            if (correct is null || faithful is null) return false;
            verdict = new JudgeVerdict(correct.Value, faithful.Value, reason);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string? FirstObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }
        return null;
    }
}
