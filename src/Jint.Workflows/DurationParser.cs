using System.Globalization;
using System.Text.RegularExpressions;

namespace Jint.Workflows;

/// <summary>
/// Utility for parsing duration strings into <see cref="DateTimeOffset"/> values.
/// Supports formats like <c>"5d"</c>, <c>"2h"</c>, <c>"30m"</c>, <c>"10s"</c>,
/// or a raw number of milliseconds.
/// </summary>
public static class DurationParser
{
    /// <summary>
    /// Parse a duration value and return the absolute time at which
    /// the duration will have elapsed, relative to the given <paramref name="timeProvider"/>.
    /// Accepts a duration string (e.g. <c>"5d"</c>, <c>"30m"</c>) or a numeric
    /// millisecond value (int, long, double, or numeric string).
    /// </summary>
    public static DateTimeOffset Parse(object? duration, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        if (duration is string s)
        {
            return ParseString(s, now);
        }

        if (duration is IConvertible)
        {
            var ms = Convert.ToDouble(duration, CultureInfo.InvariantCulture);
            return now.AddMilliseconds(ms);
        }

        throw new ArgumentException($"Invalid duration: {duration}. Use a number (ms) or string like '5d', '2h', '30m', '10s'.");
    }

    private static DateTimeOffset ParseString(string duration, DateTimeOffset now)
    {
        var str = duration.Trim();

        if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && !str.Any(char.IsLetter))
        {
            return now.AddMilliseconds(ms);
        }

        var match = Regex.Match(str, @"^(\d+(?:\.\d+)?)\s*([dhms])$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return match.Groups[2].Value.ToLowerInvariant() switch
            {
                "d" => now.AddDays(value),
                "h" => now.AddHours(value),
                "m" => now.AddMinutes(value),
                "s" => now.AddSeconds(value),
                _ => throw new ArgumentException($"Unknown duration unit: {match.Groups[2].Value}")
            };
        }

        throw new ArgumentException($"Invalid duration: {duration}. Use a number (ms) or string like '5d', '2h', '30m', '10s'.");
    }
}
