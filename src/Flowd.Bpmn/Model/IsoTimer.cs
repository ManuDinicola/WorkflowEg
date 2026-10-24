using System.Globalization;
using System.Xml;

namespace Flowd.Bpmn.Model;

/// <summary>A repeating timer: <c>R3/PT10M</c> (3 times) or <c>R/PT10M</c> (forever), FR-TMR-001.</summary>
public sealed record CycleSpec(int? Repetitions, TimeSpan Interval);

/// <summary>Parsing of the ISO 8601 subset used by timer events (day-time durations, instants, repeating intervals).</summary>
public static class IsoTimer
{
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        try
        {
            duration = XmlConvert.ToTimeSpan(text.Trim());
            return duration >= TimeSpan.Zero;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool TryParseDate(string text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant);

    public static bool TryParseCycle(string text, out CycleSpec cycle)
    {
        cycle = new CycleSpec(null, TimeSpan.Zero);
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !parts[0].StartsWith('R') || !TryParseDuration(parts[1], out var interval) || interval <= TimeSpan.Zero)
        {
            return false;
        }

        var count = parts[0][1..];
        if (count.Length == 0)
        {
            cycle = new CycleSpec(null, interval);
            return true;
        }

        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var repetitions) || repetitions < 1)
        {
            return false;
        }

        cycle = new CycleSpec(repetitions, interval);
        return true;
    }

    /// <summary>Checks a literal (non-expression) timer value of the given kind.</summary>
    public static bool IsValid(TimerKind kind, string text) => kind switch
    {
        TimerKind.Date => TryParseDate(text, out _),
        TimerKind.Duration => TryParseDuration(text, out _),
        TimerKind.Cycle => TryParseCycle(text, out _),
        _ => false, // non-union: enum
    };

    /// <summary>Turns a literal timer value into its first due time (and cycle) relative to <paramref name="now"/> (FR-TMR-001).</summary>
    public static bool TryComputeDue(TimerKind kind, string text, DateTimeOffset now, out DateTimeOffset due, out CycleSpec? cycle)
    {
        due = now;
        cycle = null;
        switch (kind)
        {
            case TimerKind.Date:
                return TryParseDate(text, out due);
            case TimerKind.Duration:
                if (!TryParseDuration(text, out var duration))
                {
                    return false;
                }

                due = now + duration;
                return true;
            case TimerKind.Cycle:
                if (!TryParseCycle(text, out var spec))
                {
                    return false;
                }

                cycle = spec;
                due = now + spec.Interval;
                return true;
        }

        return false;
    }
}
