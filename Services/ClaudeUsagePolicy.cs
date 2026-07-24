namespace AgentCompanion.Services;

internal enum ClaudeUsageWindow
{
    FiveHour,
    SevenDay
}

internal sealed record ClaudeUsageApiCache(
    string ClaudeHome,
    ClaudeUsageApiResponse Response,
    DateTimeOffset FetchedAtUtc);

internal readonly record struct ClaudeUsageValue(double Percent, bool IsExact);

internal static class ClaudeUsagePolicy
{
    private static readonly TimeSpan ApiUsageMaxAge = TimeSpan.FromMinutes(5);

    public static ClaudeUsageValue Resolve(
        ClaudeUsageApiCache? cache,
        string claudeHome,
        ClaudeUsageWindow window,
        double? statuslinePercent,
        double estimatedPercent,
        DateTimeOffset now)
    {
        var apiPercent = GetCurrentApiPercent(cache, claudeHome, window, now);
        if (apiPercent.HasValue)
            return new ClaudeUsageValue(apiPercent.Value, true);
        if (statuslinePercent.HasValue)
            return new ClaudeUsageValue(statuslinePercent.Value, true);
        return new ClaudeUsageValue(estimatedPercent, false);
    }

    private static double? GetCurrentApiPercent(
        ClaudeUsageApiCache? cache,
        string claudeHome,
        ClaudeUsageWindow window,
        DateTimeOffset now)
    {
        if (cache == null
            || !string.Equals(cache.ClaudeHome, claudeHome, StringComparison.OrdinalIgnoreCase)
            || now - cache.FetchedAtUtc > ApiUsageMaxAge)
        {
            return null;
        }

        var (percent, resetsAt) = window switch
        {
            ClaudeUsageWindow.FiveHour => (cache.Response.FiveHourPercent, cache.Response.FiveHourResetsAt),
            ClaudeUsageWindow.SevenDay => (cache.Response.SevenDayPercent, cache.Response.SevenDayResetsAt),
            _ => (null, null)
        };
        if (resetsAt.HasValue && resetsAt.Value < now.AddMinutes(-1))
            return null;
        return percent;
    }
}

internal sealed class SingleFlightGate
{
    private int _entered;

    public bool TryEnter()
    {
        return Interlocked.CompareExchange(ref _entered, 1, 0) == 0;
    }

    public void Exit()
    {
        Interlocked.Exchange(ref _entered, 0);
    }
}
