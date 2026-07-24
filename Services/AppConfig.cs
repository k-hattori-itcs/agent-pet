using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenPet.Services;

public class AppConfig
{
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private static readonly string JsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet_data", "pet_config.json");
    private static readonly string CfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet_data", "pet_config.cfg");

    public int TotalCalls { get; set; }
    public long TotalTokens { get; set; }
    public string ActiveModel { get; set; } = "N/A";
    public string Endpoint { get; set; } = "N/A";
    public double WindowX { get; set; } = -1;
    public double WindowY { get; set; } = -1;
    public double PetScale { get; set; } = 0.85;
    public string ActivePetId { get; set; } = "";
    public bool ProxyEnabled { get; set; }
    public int ProxyPort { get; set; } = 11435;
    public bool ProxyDebugLogEnabled { get; set; }
    public bool ShowTokenRing { get; set; } = true;
    public long DailyTokenLimit { get; set; } = 200_000;
    public string StatusProvider { get; set; } = "Codex";
    public string LauncherTarget { get; set; } = "Codex";
    public string ClaudeHomePath { get; set; } = "";
    public string VSCodeWorkspacePath { get; set; } = "";
    public long ClaudeShortWindowTokenLimit { get; set; } = 3_640_000;
    public long ClaudeWeeklyTokenLimit { get; set; } = 562_000_000;
    public double ClaudeShortWindowHours { get; set; } = 5;
    public double PetSittingDurationSeconds { get; set; } = PetIdleTiming.DefaultSittingDurationSeconds;
    public double PetSleepingDurationSeconds { get; set; } = PetIdleTiming.DefaultSleepingDurationSeconds;

    public void Load()
    {
        try
        {
            if (TryLoadJson(JsonPath) || TryLoadJson(JsonPath + ".bak"))
                return;
            if (File.Exists(CfgPath))
                LoadFromCfg(CfgPath);
            Normalize();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Configuration load failed.", ex);
            Normalize();
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            var json = JsonSerializer.Serialize(this, SaveOptions);
            AtomicFile.WriteAllText(JsonPath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Configuration save failed.", ex);
        }
    }

    private bool TryLoadJson(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path));
            if (config == null)
                return false;
            CopyFrom(config);
            Normalize();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLogger.Error("Configuration file could not be read.", ex);
            return false;
        }
    }

    private void LoadFromCfg(string path)
    {
        var content = File.ReadAllText(path);
        var section = "";
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1];
                continue;
            }

            var equals = trimmed.IndexOf('=');
            if (equals < 0)
                continue;
            var key = trimmed[..equals].Trim();
            var value = trimmed[(equals + 1)..].Trim().Trim('"');
            switch (section)
            {
                case "stats":
                    if (key == "total_calls" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var calls)) TotalCalls = calls;
                    if (key == "total_tokens" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tokens)) TotalTokens = tokens;
                    if (key == "active_model") ActiveModel = value;
                    if (key == "endpoint") Endpoint = value;
                    break;
                case "window" when key == "position":
                    var match = Regex.Match(value, @"\((-?\d+(?:\.\d+)?),\s*(-?\d+(?:\.\d+)?)\)", RegexOptions.CultureInvariant);
                    if (match.Success)
                    {
                        double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x);
                        double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y);
                        WindowX = x;
                        WindowY = y;
                    }
                    break;
                case "pet":
                    if (key == "scale" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale)) PetScale = scale;
                    if (key == "active_id") ActivePetId = value;
                    break;
                case "proxy":
                    if (key == "enabled" && bool.TryParse(value, out var enabled)) ProxyEnabled = enabled;
                    if (key == "port" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)) ProxyPort = port;
                    if (key == "debug_log_enabled" && bool.TryParse(value, out var debug)) ProxyDebugLogEnabled = debug;
                    break;
            }
        }
    }

    private void Normalize()
    {
        PetScale = double.IsFinite(PetScale) ? Math.Clamp(PetScale, 0.2, 2.0) : 0.85;
        ProxyPort = ProxyPort is >= 1 and <= 65535 ? ProxyPort : 11435;
        DailyTokenLimit = Math.Max(1, DailyTokenLimit);
        ClaudeShortWindowTokenLimit = Math.Max(1, ClaudeShortWindowTokenLimit);
        ClaudeWeeklyTokenLimit = Math.Max(1, ClaudeWeeklyTokenLimit);
        ClaudeShortWindowHours = double.IsFinite(ClaudeShortWindowHours) ? Math.Clamp(ClaudeShortWindowHours, 0.1, 168) : 5;
        PetSittingDurationSeconds = PetIdleTiming.NormalizeDuration(PetSittingDurationSeconds, PetIdleTiming.DefaultSittingDurationSeconds);
        PetSleepingDurationSeconds = PetIdleTiming.NormalizeDuration(PetSleepingDurationSeconds, PetIdleTiming.DefaultSleepingDurationSeconds);
        StatusProvider = string.IsNullOrWhiteSpace(StatusProvider) ? "Codex" : StatusProvider;
        LauncherTarget = string.IsNullOrWhiteSpace(LauncherTarget) ? "Codex" : LauncherTarget;
    }

    private void CopyFrom(AppConfig other)
    {
        TotalCalls = other.TotalCalls;
        TotalTokens = other.TotalTokens;
        ActiveModel = other.ActiveModel;
        Endpoint = other.Endpoint;
        WindowX = other.WindowX;
        WindowY = other.WindowY;
        PetScale = other.PetScale;
        ActivePetId = other.ActivePetId;
        ProxyEnabled = other.ProxyEnabled;
        ProxyPort = other.ProxyPort;
        ProxyDebugLogEnabled = other.ProxyDebugLogEnabled;
        ShowTokenRing = other.ShowTokenRing;
        DailyTokenLimit = other.DailyTokenLimit;
        StatusProvider = other.StatusProvider;
        LauncherTarget = other.LauncherTarget;
        ClaudeHomePath = other.ClaudeHomePath ?? "";
        VSCodeWorkspacePath = other.VSCodeWorkspacePath ?? "";
        ClaudeShortWindowTokenLimit = other.ClaudeShortWindowTokenLimit;
        ClaudeWeeklyTokenLimit = other.ClaudeWeeklyTokenLimit;
        ClaudeShortWindowHours = other.ClaudeShortWindowHours;
        PetSittingDurationSeconds = other.PetSittingDurationSeconds;
        PetSleepingDurationSeconds = other.PetSleepingDurationSeconds;
    }
}
