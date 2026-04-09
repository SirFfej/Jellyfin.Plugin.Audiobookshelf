using System;

namespace Jellyfin.Plugin.Audiobookshelf.Helpers;

/// <summary>
/// Conversion helpers between ABS time units (seconds as double) and
/// Jellyfin time units (ticks as long, where 1 second = 10,000,000 ticks).
/// </summary>
public static class TimeHelper
{
    /// <summary>Converts seconds (ABS) to ticks (Jellyfin).</summary>
    public static long SecondsToTicks(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return 0;
        }

        double ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks > long.MaxValue)
        {
            return long.MaxValue;
        }

        if (ticks < long.MinValue)
        {
            return long.MinValue;
        }

        return (long)ticks;
    }

    /// <summary>Converts ticks (Jellyfin) to seconds (ABS).</summary>
    public static double TicksToSeconds(long ticks)
        => (double)ticks / TimeSpan.TicksPerSecond;

    /// <summary>Converts Unix epoch milliseconds to a <see cref="DateTime"/> (UTC).</summary>
    public static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
