using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenPet.Services;

public record DailyRecord(string Date, string Platform, int Calls, long InputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}

public class TokenHistory
{
    private const int SaveDelayMilliseconds = 500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _dataPath;
    private Dictionary<string, Dictionary<string, PlatformStats>> _records = new();
    private Timer? _saveTimer;
    private bool _dirty;

    public TokenHistory()
        : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet_data", "token_history.json"))
    {
    }

    internal TokenHistory(string dataPath)
    {
        _dataPath = Path.GetFullPath(dataPath);
    }

    public void Load()
    {
        lock (_sync)
        {
            if (TryLoad(_dataPath) || TryLoad(_dataPath + ".bak"))
                return;
            _records = new();
        }
    }

    public void Record(string platform, long inputTokens, long outputTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        lock (_sync)
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!_records.TryGetValue(date, out var platforms))
                _records[date] = platforms = new();
            if (!platforms.TryGetValue(platform, out var stats))
                platforms[platform] = stats = new();
            stats.Calls++;
            stats.In = checked(stats.In + Math.Max(0, inputTokens));
            stats.Out = checked(stats.Out + Math.Max(0, outputTokens));
            _dirty = true;
            ScheduleSave();
        }
    }

    public long GetTodayTotal() => GetTodayStats(stats => stats.Total);
    public int GetTodayCalls() => checked((int)GetTodayStats(stats => stats.Calls));

    public long GetCumulativeTokens()
    {
        lock (_sync)
            return _records.Values.SelectMany(day => day.Values).Sum(stats => stats.Total);
    }

    public int GetTotalCalls()
    {
        lock (_sync)
            return _records.Values.SelectMany(day => day.Values).Sum(stats => stats.Calls);
    }

    public List<DailyRecord> GetDailyRecords()
    {
        lock (_sync)
        {
            return _records.SelectMany(day => day.Value.Select(platform =>
                    new DailyRecord(day.Key, platform.Key, platform.Value.Calls, platform.Value.In, platform.Value.Out)))
                .OrderByDescending(record => record.Date, StringComparer.Ordinal)
                .ThenBy(record => record.Platform, StringComparer.Ordinal)
                .ToList();
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records = new();
            _dirty = true;
            FlushUnderLock();
        }
    }

    public void Flush()
    {
        lock (_sync)
            FlushUnderLock();
    }
    public Dictionary<string, PlatformStats> GetTotals()
    {
        lock (_sync)
        {
            var totals = new Dictionary<string, PlatformStats>(StringComparer.Ordinal);
            foreach (var platforms in _records.Values)
            {
                foreach (var (name, stats) in platforms)
                {
                    if (!totals.TryGetValue(name, out var total))
                        totals[name] = total = new();
                    total.Calls += stats.Calls;
                    total.In += stats.In;
                    total.Out += stats.Out;
                }
            }
            return totals;
        }
    }

    private long GetTodayStats(Func<PlatformStats, long> selector)
    {
        lock (_sync)
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return _records.TryGetValue(date, out var platforms) ? platforms.Values.Sum(selector) : 0;
        }
    }

    private bool TryLoad(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            _records = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, PlatformStats>>>(File.ReadAllText(path), JsonOptions) ?? new();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error("Token history load failed.", ex);
            return false;
        }
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new Timer(SavePending);
        _saveTimer.Change(SaveDelayMilliseconds, Timeout.Infinite);
    }

    private void SavePending(object? state)
    {
        lock (_sync)
            FlushUnderLock();
    }

    private void FlushUnderLock()
    {
        _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_dirty)
            Save();
    }

    private void Save()
    {
        try
        {
            AtomicFile.WriteAllText(_dataPath, JsonSerializer.Serialize(_records, JsonOptions));
            _dirty = false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Token history save failed.", ex);
        }
    }
}

public class PlatformStats
{
    [JsonPropertyName("calls")]
    public int Calls { get; set; }
    [JsonPropertyName("in")]
    public long In { get; set; }
    [JsonPropertyName("out")]
    public long Out { get; set; }
    [JsonIgnore]
    public long Total => In + Out;
}
