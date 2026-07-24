using System.Globalization;
using AgentCompanion.Services;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class ClaudeUsagePolicyTests
{
    [Fact]
    public void ApiValueBeatsStatuslineAndEstimate()
    {
        var now = Now();
        var cache = Cache("C:\\Users\\test\\.claude", now, fiveHour: 73, sevenDay: 57);

        var result = ClaudeUsagePolicy.Resolve(
            cache,
            cache.ClaudeHome,
            ClaudeUsageWindow.FiveHour,
            statuslinePercent: 100,
            estimatedPercent: 25,
            now);

        Assert.Equal(73, result.Percent);
        Assert.True(result.IsExact);
    }

    [Fact]
    public void ExpiredWindowFallsBackToStatusline()
    {
        var now = Now();
        var response = new ClaudeUsageApiResponse(73, now.AddMinutes(-2), 57, now.AddDays(1));
        var cache = new ClaudeUsageApiCache("C:\\Users\\test\\.claude", response, now);

        var result = ClaudeUsagePolicy.Resolve(
            cache,
            cache.ClaudeHome,
            ClaudeUsageWindow.FiveHour,
            statuslinePercent: 61,
            estimatedPercent: 25,
            now);

        Assert.Equal(61, result.Percent);
        Assert.True(result.IsExact);
    }

    [Fact]
    public void MissingApiAndStatuslineUsesEstimate()
    {
        var now = Now();

        var result = ClaudeUsagePolicy.Resolve(
            cache: null,
            claudeHome: "C:\\Users\\test\\.claude",
            ClaudeUsageWindow.SevenDay,
            statuslinePercent: null,
            estimatedPercent: 42,
            now);

        Assert.Equal(42, result.Percent);
        Assert.False(result.IsExact);
    }

    [Fact]
    public void DifferentClaudeHomeDoesNotReuseCache()
    {
        var now = Now();
        var cache = Cache("C:\\Users\\first\\.claude", now, fiveHour: 73, sevenDay: 57);

        var result = ClaudeUsagePolicy.Resolve(
            cache,
            "C:\\Users\\second\\.claude",
            ClaudeUsageWindow.SevenDay,
            statuslinePercent: null,
            estimatedPercent: 19,
            now);

        Assert.Equal(19, result.Percent);
        Assert.False(result.IsExact);
    }

    [Fact]
    public async Task SingleFlightGate_AllowsOnlyOneConcurrentEntry()
    {
        var gate = new SingleFlightGate();
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return gate.TryEnter();
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, entered => entered);
        gate.Exit();
        Assert.True(gate.TryEnter());
    }

    private static DateTimeOffset Now()
    {
        return DateTimeOffset.Parse("2026-07-24T03:00:00+00:00", CultureInfo.InvariantCulture);
    }

    private static ClaudeUsageApiCache Cache(
        string claudeHome,
        DateTimeOffset now,
        double fiveHour,
        double sevenDay)
    {
        var response = new ClaudeUsageApiResponse(
            fiveHour,
            now.AddHours(1),
            sevenDay,
            now.AddDays(1));
        return new ClaudeUsageApiCache(claudeHome, response, now);
    }
}
