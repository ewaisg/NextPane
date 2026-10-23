using System.Globalization;
using System.Text.RegularExpressions;
using NexPane.Core.Search;

namespace NexPane.Search.Execution;

/// <summary>Inclusive-low, exclusive-high interval over a 64-bit scale (bytes, FILETIME ticks, counts).</summary>
public readonly record struct Interval(long Low, long High)
{
    public static Interval All { get; } = new(long.MinValue, long.MaxValue);

    public bool Contains(long value) => value >= Low && value < High;

    public bool IsEmpty => High <= Low;
}

/// <summary>A set of intervals (disjunction) — e.g. <c>!=x</c> is two intervals.</summary>
public sealed record IntervalSet(IReadOnlyList<Interval> Intervals)
{
    public bool Contains(long value)
    {
        foreach (var interval in Intervals)
        {
            if (interval.Contains(value))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Size values for <c>size:</c> (§8.2): binary units B/KB/MB/GB/TB, decimals, and keywords.</summary>
public static partial class SizeParser
{
    private const long Kb = 1024;
    private const long Mb = Kb * 1024;
    private const long Gb = Mb * 1024;
    private const long Tb = Gb * 1024;

    /// <summary>Keyword buckets: empty (0), tiny (1 B–16 KB), small (16 KB–1 MB), medium (1–128 MB),
    /// large (128 MB–1 GB), huge (1–4 GB), gigantic (≥ 4 GB).</summary>
    public static bool TryKeyword(string text, out Interval interval)
    {
        interval = text.ToLowerInvariant() switch
        {
            "empty" => new Interval(0, 1),
            "tiny" => new Interval(1, 16 * Kb),
            "small" => new Interval(16 * Kb, Mb),
            "medium" => new Interval(Mb, 128 * Mb),
            "large" => new Interval(128 * Mb, Gb),
            "huge" => new Interval(Gb, 4 * Gb),
            "gigantic" => new Interval(4 * Gb, long.MaxValue),
            _ => default,
        };
        return interval != default;
    }

    public static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        var match = SizePattern().Match(text.Trim());
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "K" or "KB" or "KIB" => Kb,
            "M" or "MB" or "MIB" => Mb,
            "G" or "GB" or "GIB" => Gb,
            "T" or "TB" or "TIB" => Tb,
            _ => -1L,
        };
        if (multiplier < 0)
        {
            return false;
        }

        var value = number * multiplier;
        if (value > long.MaxValue)
        {
            return false;
        }

        bytes = (long)decimal.Floor(value);
        return true;
    }

    /// <summary>Converts a <c>size:</c> value into intervals of bytes.</summary>
    public static bool TryParse(QueryValue value, out IntervalSet set, out string? error)
    {
        set = new IntervalSet([]);
        error = null;
        switch (value)
        {
            case TextValue t when TryKeyword(t.Text, out var keyword):
                set = new IntervalSet([keyword]);
                return true;
            case TextValue t when TryParseBytes(t.Text, out var exact):
                set = new IntervalSet([new Interval(exact, exact + 1)]);
                return true;
            case ComparisonValue c when TryPoint(c.Operand.Text, out var point):
                set = Compare(c.Operator, point);
                return true;
            case RangeValue r when TryPoint(r.Low.Text, out var low) && TryPoint(r.High.Text, out var high):
                set = new IntervalSet([new Interval(low.Low, high.High)]);
                return true;
            default:
                error = "Invalid size. Use e.g. 50MB, >1.5GB, 1KB..2MB or empty/tiny/small/medium/large/huge/gigantic.";
                return false;
        }
    }

    /// <summary>A size point is the exact byte count; keywords act as their bucket.</summary>
    private static bool TryPoint(string text, out Interval interval)
    {
        if (TryKeyword(text, out interval))
        {
            return true;
        }

        if (TryParseBytes(text, out var bytes))
        {
            interval = new Interval(bytes, bytes + 1);
            return true;
        }

        return false;
    }

    internal static IntervalSet Compare(ComparisonOperator op, Interval point) => op switch
    {
        ComparisonOperator.Less => new IntervalSet([new Interval(long.MinValue, point.Low)]),
        ComparisonOperator.LessOrEqual => new IntervalSet([new Interval(long.MinValue, point.High)]),
        ComparisonOperator.Greater => new IntervalSet([new Interval(point.High, long.MaxValue)]),
        ComparisonOperator.GreaterOrEqual => new IntervalSet([new Interval(point.Low, long.MaxValue)]),
        ComparisonOperator.Equal => new IntervalSet([point]),
        ComparisonOperator.NotEqual => new IntervalSet([new Interval(long.MinValue, point.Low), new Interval(point.High, long.MaxValue)]),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown operator."),
    };

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*([A-Za-z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SizePattern();
}

/// <summary>
/// Date values for <c>modified:</c>, <c>created:</c>, <c>accessed:</c> (§8.2), evaluated in the local time zone of the
/// injected <see cref="TimeProvider"/> and converted to UTC FILETIME intervals (DST-correct day boundaries).
/// </summary>
public static partial class DateParser
{
    public static bool TryParse(QueryValue value, TimeProvider clock, out IntervalSet set, out string? error)
    {
        ArgumentNullException.ThrowIfNull(clock);
        set = new IntervalSet([]);
        error = null;
        switch (value)
        {
            case TextValue t when TryPeriod(t.Text, clock, out var period):
                set = new IntervalSet([period]);
                return true;
            case ComparisonValue c when TryPeriod(c.Operand.Text, clock, out var point):
                set = SizeParser.Compare(c.Operator, point);
                return true;
            case RangeValue r when TryPeriod(r.Low.Text, clock, out var low) && TryPeriod(r.High.Text, clock, out var high):
                set = new IntervalSet([new Interval(low.Low, high.High)]);
                return true;
            default:
                error = "Invalid date. Use today, yesterday, thisweek, lastweek, thismonth, lastmonth, thisyear, lastyear, lastNd/Nw/Nm/Ny, or ISO dates such as 2026-10-01, 2026-10, 2026 or 2026-10-01T08:30.";
                return false;
        }
    }

    /// <summary>Resolves one date token to a UTC FILETIME interval [start, end).</summary>
    public static bool TryPeriod(string text, TimeProvider clock, out Interval interval)
    {
        interval = default;
        var zone = clock.LocalTimeZone;
        var nowUtc = clock.GetUtcNow().UtcDateTime;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var today = nowLocal.Date;
        var lower = text.Trim().ToLowerInvariant();
        switch (lower)
        {
            case "today":
                interval = LocalRange(today, today.AddDays(1), zone);
                return true;
            case "yesterday":
                interval = LocalRange(today.AddDays(-1), today, zone);
                return true;
            case "thisweek":
            {
                var monday = StartOfWeek(today);
                interval = LocalRange(monday, monday.AddDays(7), zone);
                return true;
            }

            case "lastweek":
            {
                var monday = StartOfWeek(today).AddDays(-7);
                interval = LocalRange(monday, monday.AddDays(7), zone);
                return true;
            }

            case "thismonth":
            {
                var first = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
                interval = LocalRange(first, first.AddMonths(1), zone);
                return true;
            }

            case "lastmonth":
            {
                var first = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(-1);
                interval = LocalRange(first, first.AddMonths(1), zone);
                return true;
            }

            case "thisyear":
            {
                var first = new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
                interval = LocalRange(first, first.AddYears(1), zone);
                return true;
            }

            case "lastyear":
            {
                var first = new DateTime(today.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
                interval = LocalRange(first, first.AddYears(1), zone);
                return true;
            }
        }

        var rolling = RollingPattern().Match(lower);
        if (rolling.Success && int.TryParse(rolling.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 100_000)
        {
            var startLocal = rolling.Groups[2].Value switch
            {
                "d" => nowLocal.AddDays(-n),
                "w" => nowLocal.AddDays(-7 * n),
                "m" => nowLocal.AddMonths(-n),
                _ => nowLocal.AddYears(-Math.Min(n, nowLocal.Year - 1)),
            };
            interval = new Interval(ToUtcTicks(startLocal, zone), nowUtc.ToFileTimeUtc() + 1);
            return true;
        }

        var iso = IsoPattern().Match(text.Trim());
        if (!iso.Success)
        {
            return false;
        }

        var year = int.Parse(iso.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (year < 1601 || year > 9998)
        {
            return false;
        }

        if (!iso.Groups["m"].Success)
        {
            var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
            interval = LocalRange(start, start.AddYears(1), zone);
            return true;
        }

        var month = int.Parse(iso.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
        {
            return false;
        }

        if (!iso.Groups["d"].Success)
        {
            var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            interval = LocalRange(start, start.AddMonths(1), zone);
            return true;
        }

        var day = int.Parse(iso.Groups["d"].Value, CultureInfo.InvariantCulture);
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        var date = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        if (!iso.Groups["h"].Success)
        {
            interval = LocalRange(date, date.AddDays(1), zone);
            return true;
        }

        var hour = int.Parse(iso.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(iso.Groups["min"].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59)
        {
            return false;
        }

        if (iso.Groups["s"].Success)
        {
            var second = int.Parse(iso.Groups["s"].Value, CultureInfo.InvariantCulture);
            if (second > 59)
            {
                return false;
            }

            var exact = date.AddHours(hour).AddMinutes(minute).AddSeconds(second);
            interval = LocalRange(exact, exact.AddSeconds(1), zone);
            return true;
        }

        var at = date.AddHours(hour).AddMinutes(minute);
        interval = LocalRange(at, at.AddMinutes(1), zone);
        return true;
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7; // ISO 8601: weeks start on Monday
        return date.AddDays(-offset);
    }

    private static Interval LocalRange(DateTime startLocal, DateTime endLocal, TimeZoneInfo zone) =>
        new(ToUtcTicks(startLocal, zone), ToUtcTicks(endLocal, zone));

    /// <summary>Local wall time → UTC FILETIME. A wall time skipped by a DST jump maps to the instant of the jump;
    /// an ambiguous wall time (repeated hour) maps to its first occurrence.</summary>
    public static long ToUtcTicks(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var probe = local;
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            local = probe;
        }

        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var largest = offsets.Max();
            return new DateTimeOffset(local, largest).UtcDateTime.ToFileTimeUtc();
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone).ToFileTimeUtc();
    }

    [GeneratedRegex(@"^last(\d+)([dwmy])$", RegexOptions.CultureInvariant)]
    private static partial Regex RollingPattern();

    [GeneratedRegex(@"^(?<y>\d{4})(?:-(?<m>\d{2})(?:-(?<d>\d{2})(?:[T ](?<h>\d{2}):(?<min>\d{2})(?::(?<s>\d{2}))?)?)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoPattern();
}
