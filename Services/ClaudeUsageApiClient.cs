using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentCompanion.Services;

public sealed record ClaudeUsageApiResponse(
    double? FiveHourPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayPercent,
    DateTimeOffset? SevenDayResetsAt)
{
    public static ClaudeUsageApiResponse Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var fiveHour = ParseWindow(root, "five_hour");
        var sevenDay = ParseWindow(root, "seven_day");
        return new ClaudeUsageApiResponse(
            fiveHour.Percent,
            fiveHour.ResetsAt,
            sevenDay.Percent,
            sevenDay.ResetsAt);
    }

    private static (double? Percent, DateTimeOffset? ResetsAt) ParseWindow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
            return (null, null);

        double? percent = null;
        if (window.TryGetProperty("utilization", out var utilization)
            && utilization.ValueKind == JsonValueKind.Number
            && utilization.TryGetDouble(out var value))
        {
            percent = Math.Clamp(value, 0, 100);
        }

        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("resets_at", out var reset)
            && reset.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                reset.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedReset))
        {
            resetsAt = parsedReset;
        }

        return (percent, resetsAt);
    }
}

internal sealed class ClaudeUsageApiClient
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<ClaudeUsageApiResponse?> FetchAsync(string claudeHome, CancellationToken cancellationToken)
    {
        var accessToken = ReadAccessToken(claudeHome);
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("AgentCompanion/1.0");

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ClaudeUsageApiResponse.Parse(json);
    }

    private static string? ReadAccessToken(string claudeHome)
    {
        var credentialsPath = Path.Combine(claudeHome, ".credentials.json");
        if (!File.Exists(credentialsPath))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialsPath, Encoding.UTF8));
            var root = document.RootElement;
            if (!root.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token)
                || token.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return token.GetString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Warning($"Claude usage credentials could not be read: {ex.GetType().Name}");
            return null;
        }
    }
}
