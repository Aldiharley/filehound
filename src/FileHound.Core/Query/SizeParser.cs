using System.Globalization;

namespace FileHound.Core.Query;

/// <summary>Parses <c>size:</c> values: comparisons, ranges, plain values and named buckets (base-1024 units).</summary>
public static class SizeParser
{
    private const long KB = 1024, MB = KB * 1024;

    public static bool TryParse(string text, out long min, out long max)
    {
        min = 0; max = long.MaxValue;
        var s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;

        switch (s)
        {
            case "empty": min = 0; max = 0; return true;
            case "tiny": min = 0; max = 10 * KB - 1; return true;
            case "small": min = 10 * KB; max = 100 * KB - 1; return true;
            case "medium": min = 100 * KB; max = MB - 1; return true;
            case "large": min = MB; max = 16 * MB - 1; return true;
            case "huge": min = 16 * MB; max = 128 * MB - 1; return true;
            case "gigantic": min = 128 * MB; max = long.MaxValue; return true;
        }

        int range = s.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!TryParseValue(s[..range], out min) || !TryParseValue(s[(range + 2)..], out max) || max < min) return false;
            return true;
        }

        string op = s.StartsWith(">=") ? ">=" : s.StartsWith("<=") ? "<=" : s.StartsWith('>') ? ">" : s.StartsWith('<') ? "<" : s.StartsWith('=') ? "=" : "";
        if (!TryParseValue(s[op.Length..], out long v)) return false;
        switch (op)
        {
            case ">": min = v == long.MaxValue ? v : v + 1; max = long.MaxValue; break;
            case ">=": min = v; max = long.MaxValue; break;
            case "<": min = 0; max = Math.Max(0, v - 1); if (v == 0) return false; break;
            case "<=": min = 0; max = v; break;
            default: min = v; max = v; break;
        }
        return true;
    }

    private static bool TryParseValue(string s, out long bytes)
    {
        bytes = 0;
        s = s.Trim();
        int i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return false;
        if (!double.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return false;
        long mult = s[i..].Trim() switch
        {
            "" or "b" => 1,
            "k" or "kb" => KB,
            "m" or "mb" => MB,
            "g" or "gb" => MB * 1024,
            "t" or "tb" => MB * 1024 * 1024,
            _ => -1,
        };
        if (mult < 0) return false;
        double result = number * mult;
        if (result < 0 || result > long.MaxValue) return false;
        bytes = (long)Math.Round(result);
        return true;
    }
}
