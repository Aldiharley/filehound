using System.Globalization;

namespace FileHound.Core.Query;

/// <summary>
/// Parses <c>dm:</c> values into a half-open UTC ticks range [min, max).
/// Keywords: today, yesterday, week (last 7 days), month (last 30 days), year (last 365 days).
/// Dates: YYYY, YYYY-MM, YYYY-MM-DD (local calendar), with optional &gt; &gt;= &lt; &lt;= prefix or a..b range.
/// </summary>
public static class DateRangeParser
{
    public static bool TryParse(string text, IClock clock, out long minUtc, out long maxUtc)
    {
        minUtc = 0; maxUtc = long.MaxValue;
        var s = text.Trim().ToLowerInvariant();
        if (s.Length == 0) return false;
        var now = clock.Now;
        long nowUtc = now.UtcTicks;
        var todayLocal = now.ToLocalTime().Date;

        switch (s)
        {
            case "today": return Day(todayLocal, out minUtc, out maxUtc);
            case "yesterday": return Day(todayLocal.AddDays(-1), out minUtc, out maxUtc);
            case "week" or "thisweek" or "lastweek": minUtc = nowUtc - 7 * TimeSpan.TicksPerDay; maxUtc = long.MaxValue; return true;
            case "month" or "thismonth" or "lastmonth": minUtc = nowUtc - 30 * TimeSpan.TicksPerDay; maxUtc = long.MaxValue; return true;
            case "year" or "thisyear" or "lastyear": minUtc = nowUtc - 365 * TimeSpan.TicksPerDay; maxUtc = long.MaxValue; return true;
        }

        int range = s.IndexOf("..", StringComparison.Ordinal);
        if (range >= 0)
        {
            if (!TryPeriod(s[..range], out var aStart, out _) || !TryPeriod(s[(range + 2)..], out _, out var bEnd)) return false;
            minUtc = ToUtc(aStart); maxUtc = ToUtc(bEnd);
            return maxUtc > minUtc;
        }

        string op = s.StartsWith(">=") ? ">=" : s.StartsWith("<=") ? "<=" : s.StartsWith('>') ? ">" : s.StartsWith('<') ? "<" : "";
        if (!TryPeriod(s[op.Length..], out var start, out var end)) return false;
        switch (op)
        {
            case ">": minUtc = ToUtc(end); maxUtc = long.MaxValue; break;
            case ">=": minUtc = ToUtc(start); maxUtc = long.MaxValue; break;
            case "<": minUtc = 0; maxUtc = ToUtc(start); break;
            case "<=": minUtc = 0; maxUtc = ToUtc(end); break;
            default: minUtc = ToUtc(start); maxUtc = ToUtc(end); break;
        }
        return true;
    }

    private static bool Day(DateTime localDate, out long min, out long max)
    {
        min = ToUtc(localDate);
        max = ToUtc(localDate.AddDays(1));
        return true;
    }

    /// <summary>Parses YYYY, YYYY-MM or YYYY-MM-DD into a local [start, end) period.</summary>
    private static bool TryPeriod(string s, out DateTime start, out DateTime end)
    {
        start = end = default;
        var inv = CultureInfo.InvariantCulture;
        try
        {
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", inv, DateTimeStyles.None, out var d)) { start = d; end = d.AddDays(1); return true; }
            if (DateTime.TryParseExact(s, "yyyy-MM", inv, DateTimeStyles.None, out d)) { start = d; end = d.AddMonths(1); return true; }
            if (s.Length == 4 && DateTime.TryParseExact(s, "yyyy", inv, DateTimeStyles.None, out d)) { start = d; end = d.AddYears(1); return true; }
        }
        catch (ArgumentOutOfRangeException)
        {
            // End of the calendar (year 9999): the period's end is unrepresentable.
            end = DateTime.MaxValue;
            return start != default;
        }
        return false;
    }

    private static long ToUtc(DateTime local) =>
        local == DateTime.MaxValue ? long.MaxValue : DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().Ticks;
}
