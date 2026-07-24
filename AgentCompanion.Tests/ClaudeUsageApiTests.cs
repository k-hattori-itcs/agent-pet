using System.Globalization;
using AgentCompanion.Services;
using Xunit;

namespace AgentCompanion.Tests;

public sealed class ClaudeUsageApiTests
{
    [Fact]
    public void Parse_ReadsFiveHourAndSevenDayPercentages()
    {
        const string json = """
            {
              "five_hour": {
                "utilization": 73.0,
                "resets_at": "2026-07-24T03:40:00.426978+00:00"
              },
              "seven_day": {
                "utilization": 57.0,
                "resets_at": "2026-07-27T15:00:00.426997+00:00"
              }
            }
            """;

        var result = ClaudeUsageApiResponse.Parse(json);

        Assert.Equal(73, result.FiveHourPercent);
        Assert.Equal(57, result.SevenDayPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-07-24T03:40:00.426978+00:00", CultureInfo.InvariantCulture), result.FiveHourResetsAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-27T15:00:00.426997+00:00", CultureInfo.InvariantCulture), result.SevenDayResetsAt);
    }

    [Fact]
    public void Parse_ClampsPercentagesAndAllowsMissingWindows()
    {
        const string json = """
            {
              "five_hour": { "utilization": 120.0, "resets_at": null },
              "seven_day": null
            }
            """;

        var result = ClaudeUsageApiResponse.Parse(json);

        Assert.Equal(100, result.FiveHourPercent);
        Assert.Null(result.SevenDayPercent);
        Assert.Null(result.SevenDayResetsAt);
    }
}
