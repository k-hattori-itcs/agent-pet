using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentCompanion.Services;

public sealed class ClaudeStatusService
{
    private static readonly TimeSpan LatestFileRefreshInterval = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan UsageRefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ApiUsageRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly string _defaultClaudeHome;
    private readonly ClaudeUsageApiClient _usageApiClient = new();
    private readonly SingleFlightGate _apiUsageRefreshGate = new();
    private string? _latestTranscriptPath;
    private DateTime _latestTranscriptWriteUtc;
    private DateTime _lastLatestFileRefreshUtc = DateTime.MinValue;
    private DateTime _lastUsageRefreshUtc = DateTime.MinValue;
    private ClaudeUsageSnapshot _lastUsage = new(0, 0, null, null, null);
    private ClaudeUsageApiCache? _apiUsage;
    private DateTime _nextApiUsageRefreshUtc = DateTime.MinValue;
    private CodexStatusSnapshot _lastSnapshot = new("Watching Claude", false, null, null, DateTime.MinValue, null);

    public ClaudeStatusService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"))
    {
    }

    public ClaudeStatusService(string claudeHome)
    {
        _defaultClaudeHome = claudeHome;
    }

    public CodexStatusSnapshot Poll(
        string? configuredClaudeHome = null,
        long shortWindowTokenLimit = 2_000_000,
        long weeklyTokenLimit = 10_000_000,
        double shortWindowHours = 5)
    {
        var claudeHome = string.IsNullOrWhiteSpace(configuredClaudeHome) ? _defaultClaudeHome : configuredClaudeHome;
        RefreshApiUsageIfNeeded(claudeHome);
        RefreshLatestTranscriptIfNeeded(claudeHome);
        RefreshUsageIfNeeded(claudeHome, shortWindowHours);

        if (string.IsNullOrWhiteSpace(_latestTranscriptPath) || !File.Exists(_latestTranscriptPath))
            return WithUsage(_lastSnapshot, claudeHome, shortWindowTokenLimit, weeklyTokenLimit, shortWindowHours);

        var info = new FileInfo(_latestTranscriptPath);
        if (info.LastWriteTimeUtc != _latestTranscriptWriteUtc || _lastSnapshot.RolloutPath != _latestTranscriptPath)
        {
            _latestTranscriptWriteUtc = info.LastWriteTimeUtc;
            _lastSnapshot = ParseTranscript(_latestTranscriptPath, info.LastWriteTime);
        }

        return WithUsage(_lastSnapshot, claudeHome, shortWindowTokenLimit, weeklyTokenLimit, shortWindowHours);
    }

    private CodexStatusSnapshot WithUsage(CodexStatusSnapshot status, string claudeHome, long shortWindowTokenLimit, long weeklyTokenLimit, double shortWindowHours)
    {
        var apiUsage = Volatile.Read(ref _apiUsage);
        var now = DateTimeOffset.UtcNow;
        var shortUsage = ClaudeUsagePolicy.Resolve(
            apiUsage,
            claudeHome,
            ClaudeUsageWindow.FiveHour,
            _lastUsage.ShortWindowPercent,
            Percent(_lastUsage.ShortWindowTokens, shortWindowTokenLimit),
            now);
        var weeklyUsage = ClaudeUsagePolicy.Resolve(
            apiUsage,
            claudeHome,
            ClaudeUsageWindow.SevenDay,
            _lastUsage.WeeklyPercent,
            Percent(_lastUsage.WeeklyTokens, weeklyTokenLimit),
            now);
        return status with
        {
            TokenUsagePercent = shortUsage.Percent,
            LastTurnTokens = _lastUsage.LatestTurnTokens ?? status.LastTurnTokens,
            SecondaryTokenUsagePercent = weeklyUsage.Percent,
            TokenUsageLabel = $"{FormatHours(shortWindowHours)}{(shortUsage.IsExact ? "" : "~")} {FormatPercent(shortUsage.Percent)}",
            SecondaryTokenUsageLabel = $"W{(weeklyUsage.IsExact ? "" : "~")} {FormatPercent(weeklyUsage.Percent)}"
        };
    }

    private void RefreshApiUsageIfNeeded(string claudeHome)
    {
        var now = DateTime.UtcNow;
        if (now < _nextApiUsageRefreshUtc)
            return;

        _nextApiUsageRefreshUtc = now + ApiUsageRefreshInterval;
        if (!_apiUsageRefreshGate.TryEnter())
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var response = await _usageApiClient.FetchAsync(claudeHome, CancellationToken.None).ConfigureAwait(false);
                if (response != null)
                    Volatile.Write(ref _apiUsage, new ClaudeUsageApiCache(claudeHome, response, DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                AppLogger.Warning($"Claude usage API refresh failed: {ex.GetType().Name}");
            }
            finally
            {
                _apiUsageRefreshGate.Exit();
            }
        });
    }

    private void RefreshLatestTranscriptIfNeeded(string claudeHome)
    {
        var now = DateTime.UtcNow;
        if (now - _lastLatestFileRefreshUtc < LatestFileRefreshInterval && !string.IsNullOrWhiteSpace(_latestTranscriptPath))
            return;

        _lastLatestFileRefreshUtc = now;
        var projectsDir = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(projectsDir))
            return;

        try
        {
            var latest = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0 && !file.FullName.Contains("subagents", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            if (latest != null && latest.FullName != _latestTranscriptPath)
            {
                _latestTranscriptPath = latest.FullName;
                _latestTranscriptWriteUtc = DateTime.MinValue;
            }
        }
        catch
        {
        }
    }

    private void RefreshUsageIfNeeded(string claudeHome, double shortWindowHours)
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastUsageRefreshUtc < UsageRefreshInterval)
            return;

        _lastUsageRefreshUtc = nowUtc;
        _lastUsage = BuildUsageSnapshot(claudeHome, Math.Max(0.25, shortWindowHours));
    }

    private static ClaudeUsageSnapshot BuildUsageSnapshot(string claudeHome, double shortWindowHours)
    {
        var projectsDir = Path.Combine(claudeHome, "projects");
        if (!Directory.Exists(projectsDir))
            return new ClaudeUsageSnapshot(0, 0, null, null, null);

        var now = DateTime.Now;
        var weeklyStart = now - TimeSpan.FromDays(7);
        var shortEvents = new List<ClaudeUsageEvent>();
        long weeklyTokens = 0;
        long? latestTurnTokens = null;
        var latestUsageAt = DateTime.MinValue;

        try
        {
            foreach (var file in Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists && file.Length > 0 && file.LastWriteTime >= weeklyStart && !file.FullName.Contains("subagents", StringComparison.OrdinalIgnoreCase)))
            {
                var weeklyUsage = SumTranscriptUsage(file.FullName, weeklyStart);
                weeklyTokens += weeklyUsage.FullTokens;
                shortEvents.AddRange(weeklyUsage.Events);
                if (weeklyUsage.Latest.Timestamp >= latestUsageAt && weeklyUsage.Latest.FullTokens > 0)
                {
                    latestUsageAt = weeklyUsage.Latest.Timestamp;
                    latestTurnTokens = weeklyUsage.Latest.FullTokens;
                }
            }
        }
        catch
        {
        }

        var shortWindowTokens = CalculateActiveShortWindowTokens(shortEvents, TimeSpan.FromHours(shortWindowHours), now);
        var rateLimits = ReadStatuslineRateLimits(claudeHome);
        return new ClaudeUsageSnapshot(shortWindowTokens, weeklyTokens, latestTurnTokens, rateLimits?.ShortWindowPercent, rateLimits?.WeeklyPercent);
    }

    private static ClaudeTranscriptUsage SumTranscriptUsage(string transcriptPath, DateTime since)
    {
        long fullTokens = 0;
        long noCacheReadTokens = 0;
        var events = new List<ClaudeUsageEvent>();
        var latestUsage = new ClaudeUsageEvent(DateTime.MinValue, 0, 0);

        foreach (var line in ReadLinesShared(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "assistant")
                    continue;
                if (!root.TryGetProperty("message", out var message))
                    continue;

                var usage = ExtractUsage(message);
                if (usage.FullTokens <= 0)
                    continue;

                var timestamp = TryGetTimestamp(root) ?? DateTime.MinValue;
                if (timestamp < since)
                    continue;

                fullTokens += usage.FullTokens;
                noCacheReadTokens += usage.NoCacheReadTokens;
                if (timestamp >= latestUsage.Timestamp)
                    latestUsage = new ClaudeUsageEvent(timestamp, usage.FullTokens, usage.NoCacheReadTokens);
                events.Add(new ClaudeUsageEvent(timestamp, usage.FullTokens, usage.NoCacheReadTokens));
            }
            catch
            {
            }
        }

        return new ClaudeTranscriptUsage(fullTokens, noCacheReadTokens, latestUsage, events);
    }

    private static CodexStatusSnapshot ParseTranscript(string transcriptPath, DateTime fileUpdatedAt)
    {
        var summary = "Watching Claude";
        var isRunning = false;
        long? lastTurnTokens = null;
        DateTime updatedAt = fileUpdatedAt;

        foreach (var line in ReadLinesShared(transcriptPath))
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

                switch (typeProp.GetString())
                {
                    case "assistant":
                        ApplyAssistant(root, ref summary, ref isRunning, ref lastTurnTokens);
                        break;
                    case "user":
                        ApplyUser(root, ref summary, ref isRunning);
                        break;
                    case "queue-operation":
                        ApplyQueueOperation(root, ref summary, ref isRunning);
                        break;
                    case "system":
                        ApplySystem(root, ref isRunning);
                        break;
                    case "attachment":
                        ApplyAttachment(root, ref summary, ref isRunning);
                        break;
                    case "ai-title":
                        if (root.TryGetProperty("aiTitle", out var title) && title.ValueKind == JsonValueKind.String)
                            summary = title.GetString() ?? summary;
                        break;
                }
            }
            catch
            {
            }
        }

        return new CodexStatusSnapshot(Shorten(summary), isRunning, null, lastTurnTokens, updatedAt, transcriptPath);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static void ApplyAssistant(JsonElement root, ref string summary, ref bool isRunning, ref long? lastTurnTokens)
    {
        if (!root.TryGetProperty("message", out var message))
            return;

        var turnTokens = ExtractUsageTokens(message);
        if (turnTokens.HasValue)
            lastTurnTokens = turnTokens.Value;
        var toolSummary = ExtractToolSummary(message);
        var text = ExtractMessageText(message);

        if (!string.IsNullOrWhiteSpace(text))
            summary = text;

        if (!string.IsNullOrWhiteSpace(toolSummary))
        {
            summary = toolSummary;
            isRunning = !toolSummary.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase);
            return;
        }

        if (message.TryGetProperty("stop_reason", out var stopReason) && stopReason.GetString() == "tool_use")
            isRunning = true;
        else if (!string.IsNullOrWhiteSpace(text))
            isRunning = false;
    }

    private static void ApplyUser(JsonElement root, ref string summary, ref bool isRunning)
    {
        if (!root.TryGetProperty("message", out var message))
            return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "tool_result")
            {
                summary = "Checking tool output...";
                isRunning = true;
                return;
            }
        }
    }

    private static void ApplyQueueOperation(JsonElement root, ref string summary, ref bool isRunning)
    {
        if (!root.TryGetProperty("operation", out var operation) || operation.ValueKind != JsonValueKind.String)
            return;

        var value = operation.GetString();
        if (value == "enqueue" || value == "dequeue")
        {
            summary = "Claude is working...";
            isRunning = true;
        }
    }

    private static void ApplySystem(JsonElement root, ref bool isRunning)
    {
        if (root.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "stop_hook_summary")
            isRunning = false;
    }

    private static void ApplyAttachment(JsonElement root, ref string summary, ref bool isRunning)
    {
        if (!root.TryGetProperty("attachment", out var attachment))
            return;

        if (attachment.TryGetProperty("hookEvent", out var hookEvent) && hookEvent.GetString() == "Stop")
        {
            isRunning = false;
            var lastAssistant = ExtractLastAssistantFromHookStdout(attachment);
            if (!string.IsNullOrWhiteSpace(lastAssistant))
                summary = lastAssistant;
        }
    }

    private static string? ExtractToolSummary(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "tool_use")
                continue;
            if (!item.TryGetProperty("name", out var nameProp))
                return "Running a tool...";

            var name = nameProp.GetString() ?? "tool";
            return name switch
            {
                "AskUserQuestion" => "Waiting for input...",
                "Bash" => "Running Bash...",
                "Read" => "Reading file...",
                "Write" => "Writing file...",
                "Edit" => "Editing file...",
                "MultiEdit" => "Editing files...",
                "Grep" => "Searching files...",
                "Glob" => "Finding files...",
                _ => $"Running {name}..."
            };
        }

        return null;
    }

    private static string? ExtractMessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
            return null;

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString();

        if (content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) && type.GetString() == "text" && item.TryGetProperty("text", out var text))
                return text.GetString();
        }

        return null;
    }

    private static string? ExtractLastAssistantFromHookStdout(JsonElement attachment)
    {
        if (!attachment.TryGetProperty("stdout", out var stdout) || stdout.ValueKind != JsonValueKind.String)
            return null;

        var text = stdout.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("last_assistant_message", out var last) && last.ValueKind == JsonValueKind.String
                ? last.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static ClaudeStatuslineRateLimits? ReadStatuslineRateLimits(string claudeHome)
    {
        var path = Path.Combine(claudeHome, "agentcompanion-rate-limits.json");
        if (!File.Exists(path))
            path = Path.Combine(claudeHome, "agentpet-rate-limits.json");
        if (!File.Exists(path))
            return null;

        try
        {
            var fileUpdatedAt = File.GetLastWriteTime(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = doc.RootElement;
            if (root.TryGetProperty("rate_limits", out var nested))
                root = nested;

            var now = DateTime.Now;
            var fiveHour = ExtractStatuslineLimitPercent(root, "five_hour", now, fileUpdatedAt);
            var sevenDay = ExtractStatuslineLimitPercent(root, "seven_day", now, fileUpdatedAt);
            return fiveHour.HasValue || sevenDay.HasValue
                ? new ClaudeStatuslineRateLimits(fiveHour, sevenDay)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static double? ExtractStatuslineLimitPercent(JsonElement rateLimits, string key, DateTime now, DateTime fileUpdatedAt)
    {
        if (!rateLimits.TryGetProperty(key, out var info) || info.ValueKind != JsonValueKind.Object)
            return null;

        var percent = ExtractDouble(info, "used_percentage") ?? ExtractDouble(info, "used_percent");
        if (!percent.HasValue)
            return null;

        if (TryGetDateTime(info, "resets_at", out var resetsAt))
        {
            if (resetsAt < now.AddMinutes(-5))
                return null;
        }
        else if (now - fileUpdatedAt > TimeSpan.FromMinutes(30))
        {
            return null;
        }

        return Math.Clamp(percent.Value, 0, 100);
    }

    private static double? ExtractDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static bool TryGetDateTime(JsonElement element, string propertyName, out DateTime value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
            return false;
        if (!DateTimeOffset.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var offset))
            return false;
        value = offset.LocalDateTime;
        return true;
    }
    private static long CalculateActiveShortWindowTokens(IEnumerable<ClaudeUsageEvent> events, TimeSpan windowLength, DateTime now)
    {
        DateTime? windowStart = null;
        long windowTokens = 0;

        foreach (var usage in events.OrderBy(item => item.Timestamp))
        {
            if (windowStart == null || usage.Timestamp >= windowStart.Value + windowLength)
            {
                windowStart = usage.Timestamp;
                windowTokens = 0;
            }

            windowTokens += usage.NoCacheReadTokens;
        }

        return windowStart.HasValue && now < windowStart.Value + windowLength ? windowTokens : 0;
    }

    private static long? ExtractUsageTokens(JsonElement message)
    {
        var usage = ExtractUsage(message);
        return usage.FullTokens > 0 ? usage.FullTokens : null;
    }

    private static ClaudeTurnUsage ExtractUsage(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out var usage))
            return new ClaudeTurnUsage(0, 0);

        var input = ExtractInt64(usage, "input_tokens") ?? 0;
        var output = ExtractInt64(usage, "output_tokens") ?? 0;
        var cacheCreation = ExtractInt64(usage, "cache_creation_input_tokens") ?? 0;
        var cacheRead = ExtractInt64(usage, "cache_read_input_tokens") ?? 0;
        var full = input + output + cacheCreation + cacheRead;
        var noCacheRead = input + output + cacheCreation;
        return new ClaudeTurnUsage(full, noCacheRead);
    }

    private static long? ExtractInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value)
            ? value
            : null;
    }

    private static double Percent(long tokens, long limit)
    {
        return limit <= 0 ? 0 : Math.Clamp(tokens / (double)limit * 100.0, 0, 100);
    }

    private static string FormatPercent(double value)
    {
        return value >= 10 ? $"{value:0}%" : $"{value:0.#}%";
    }

    private static string FormatHours(double hours)
    {
        return Math.Abs(hours - Math.Round(hours)) < 0.01 ? $"{hours:0}h" : $"{hours:0.#}h";
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

    private sealed record ClaudeUsageSnapshot(long ShortWindowTokens, long WeeklyTokens, long? LatestTurnTokens, double? ShortWindowPercent, double? WeeklyPercent);
    private sealed record ClaudeStatuslineRateLimits(double? ShortWindowPercent, double? WeeklyPercent);
    private sealed record ClaudeUsageEvent(DateTime Timestamp, long FullTokens, long NoCacheReadTokens);
    private sealed record ClaudeTranscriptUsage(long FullTokens, long NoCacheReadTokens, ClaudeUsageEvent Latest, IReadOnlyList<ClaudeUsageEvent> Events);
    private sealed record ClaudeTurnUsage(long FullTokens, long NoCacheReadTokens);
}
