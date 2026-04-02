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
        => (long)(seconds * TimeSpan.TicksPerSecond);

    /// <summary>Converts ticks (Jellyfin) to seconds (ABS).</summary>
    public static double TicksToSeconds(long ticks)
        => (double)ticks / TimeSpan.TicksPerSecond;

    /// <summary>Converts Unix epoch milliseconds to a <see cref="DateTime"/> (UTC).</summary>
    public static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
