using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenPet.Services;

public sealed record CodexStatusSnapshot(
    string Summary,
    bool IsRunning,
    double? TokenUsagePercent,
    long? LastTurnTokens,
    DateTime UpdatedAt,
    string? RolloutPath,
    double? SecondaryTokenUsagePercent = null,
    string? TokenUsageLabel = null,
    string? SecondaryTokenUsageLabel = null);

public sealed class CodexStatusService
{
    private static readonly TimeSpan LatestFileRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly string _codexHome;
    private string? _latestRolloutPath;
    private DateTime _latestRolloutWriteUtc;
    private DateTime _lastLatestFileRefreshUtc = DateTime.MinValue;
    private CodexStatusSnapshot _lastSnapshot = new("Codex status unavailable", false, null, null, DateTime.MinValue, null);

    public CodexStatusService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"))
    {
    }

    public CodexStatusService(string codexHome)
    {
        _codexHome = codexHome;
    }

    public CodexStatusSnapshot Poll()
    {
        RefreshLatestRolloutIfNeeded();
        if (string.IsNullOrWhiteSpace(_latestRolloutPath) || !File.Exists(_latestRolloutPath))
            return _lastSnapshot;

        var info = new FileInfo(_latestRolloutPath);
        if (info.LastWriteTimeUtc == _latestRolloutWriteUtc && _lastSnapshot.RolloutPath == _latestRolloutPath)
            return _lastSnapshot;

        _latestRolloutWriteUtc = info.LastWriteTimeUtc;
        _lastSnapshot = ParseRollout(_latestRolloutPath, info.LastWriteTime);
        return _lastSnapshot;
    }

    private void RefreshLatestRolloutIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastLatestFileRefreshUtc < LatestFileRefreshInterval && !string.IsNullOrWhiteSpace(_latestRolloutPath))
            return;

        _lastLatestFileRefreshUtc = now;
        var sessionsDir = Path.Combine(_codexHome, "sessions");
        if (!Directory.Exists(sessionsDir))
            return;

        try
        {
            var latest = Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest != null && latest.FullName != _latestRolloutPath)
            {
                _latestRolloutPath = latest.FullName;
                _latestRolloutWriteUtc = DateTime.MinValue;
            }
        }
        catch
        {
            // Keep the last usable rollout instead of flickering the UI on transient file errors.
        }
    }

    private CodexStatusSnapshot ParseRollout(string rolloutPath, DateTime fileUpdatedAt)
    {
        var summary = "Watching Codex";
        var isRunning = false;
        double? tokenUsagePercent = null;
        long? lastTurnTokens = null;
        DateTime updatedAt = fileUpdatedAt;

        foreach (var line in ReadLinesShared(rolloutPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                updatedAt = TryGetTimestamp(root) ?? updatedAt;
                if (!root.TryGetProperty("type", out var typeProp))
                    continue;

                var type = typeProp.GetString();
                if (!root.TryGetProperty("payload", out var payload))
                    continue;

                if (type == "event_msg")
                    ApplyEventMessage(payload, ref summary, ref isRunning, ref tokenUsagePercent, ref lastTurnTokens);
                else if (type == "response_item")
                    ApplyResponseItem(payload, ref summary, ref isRunning);
            }
            catch
            {
                // Ignore incomplete trailing JSONL writes and malformed historical lines.
            }
        }

        return new CodexStatusSnapshot(Shorten(summary), isRunning, tokenUsagePercent, lastTurnTokens, updatedAt, rolloutPath);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static void ApplyEventMessage(JsonElement payload, ref string summary, ref bool isRunning, ref double? tokenUsagePercent, ref long? lastTurnTokens)
    {
        if (!payload.TryGetProperty("type", out var payloadTypeProp))
            return;

        switch (payloadTypeProp.GetString())
        {
            case "task_started":
                isRunning = true;
                summary = "Codex is working...";
                break;
            case "task_complete":
                isRunning = false;
                summary = ExtractString(payload, "last_agent_message") ?? "Codex finished";
                break;
            case "agent_message":
                summary = ExtractString(payload, "message") ?? summary;
                break;
            case "token_count":
                tokenUsagePercent = ExtractPrimaryLimitPercent(payload) ?? tokenUsagePercent;
                lastTurnTokens = ExtractLastTurnTokens(payload) ?? lastTurnTokens;
                break;
        }
    }

    private static void ApplyResponseItem(JsonElement payload, ref string summary, ref bool isRunning)
    {
        if (!payload.TryGetProperty("type", out var payloadTypeProp))
            return;

        switch (payloadTypeProp.GetString())
        {
            case "function_call":
                isRunning = true;
                summary = payload.TryGetProperty("name", out var name) ? $"Running {name.GetString()}..." : "Running a tool...";
                break;
            case "function_call_output":
                summary = "Checking tool output...";
                break;
            case "message":
                if (payload.TryGetProperty("role", out var role) && role.GetString() == "assistant")
                {
                    var text = ExtractMessageText(payload);
                    if (!string.IsNullOrWhiteSpace(text))
                        summary = text;
                }
                break;
        }
    }

    private static string? ExtractMessageText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var text))
                return text.GetString();
        }
        return null;
    }

    private static double? ExtractPrimaryLimitPercent(JsonElement payload)
    {
        if (!payload.TryGetProperty("rate_limits", out var rateLimits)) return null;
        if (!rateLimits.TryGetProperty("primary", out var primary)) return null;
        if (!primary.TryGetProperty("used_percent", out var used)) return null;
        return used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 100)
            : null;
    }

    private static long? ExtractLastTurnTokens(JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info)) return null;
        if (!info.TryGetProperty("last_token_usage", out var last)) return null;
        if (!last.TryGetProperty("total_tokens", out var total)) return null;
        return total.ValueKind == JsonValueKind.Number && total.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static string? ExtractString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static DateTime? TryGetTimestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String)
            return null;
        return DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? value.LocalDateTime
            : null;
    }

    private static string Shorten(string value)
    {
        var text = WhitespaceRegex.Replace(value, " ").Trim();
        text = text.Replace("```", "").Replace("<proposed_plan>", "").Replace("</proposed_plan>", "");
        if (text.Length <= 58)
            return text;
        return text[..55] + "...";
    }
}
