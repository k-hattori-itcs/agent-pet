using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace AgentCompanion.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryPrefix = "AgentCompanion-";
    private const string LegacyEntryPrefix = "AgentPet-";

    public string EntryName => EntryPrefix + SingleInstanceService.GetInstallId();

    public string ExecutablePath => Path.GetFullPath(
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AgentCompanion.exe"));

    public bool IsEnabled()
    {
        MigrateLegacyRegistration();
        RemoveOrphanedEntries();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return FindCurrentExecutableEntryNames(key).Any();
    }

    public void Enable()
    {
        var executablePath = ExecutablePath;
        if (!File.Exists(executablePath))
            throw new InvalidOperationException("The running AgentCompanion executable could not be located.");

        RemoveOrphanedEntries();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("The Windows startup registry key could not be opened.");
        DeleteCurrentExecutableEntries(key);
        key.SetValue(EntryName, Quote(executablePath), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        DeleteCurrentExecutableEntries(key);
    }

    private static void RemoveOrphanedEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
        if (key == null)
            return;

        foreach (var valueName in key.GetValueNames().Where(IsManagedEntryName))
        {
            var command = key.GetValue(valueName) as string;
            var executable = ExtractExecutablePath(command);
            if (executable == null || File.Exists(executable))
                continue;
            key.DeleteValue(valueName, false);
        }
    }

    private void DeleteCurrentExecutableEntries(RegistryKey? key)
    {
        if (key == null)
            return;

        foreach (var valueName in FindCurrentInstallEntryNames(key).ToArray())
            key.DeleteValue(valueName, false);
    }

    private IEnumerable<string> FindCurrentExecutableEntryNames(RegistryKey? key)
    {
        if (key == null)
            yield break;

        foreach (var valueName in key.GetValueNames().Where(IsManagedEntryName))
        {
            if (IsCurrentExecutableCommand(key.GetValue(valueName) as string))
                yield return valueName;
        }
    }

    private IEnumerable<string> FindCurrentInstallEntryNames(RegistryKey? key)
    {
        if (key == null)
            yield break;

        foreach (var valueName in key.GetValueNames().Where(IsManagedEntryName))
        {
            var executable = ExtractExecutablePath(key.GetValue(valueName) as string);
            if (executable != null && IsCurrentInstallDirectory(executable))
                yield return valueName;
        }
    }

    public void MigrateLegacyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null)
                return;

            var legacyEntries = FindCurrentInstallEntryNames(key)
                .Where(name => name.StartsWith(LegacyEntryPrefix, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (legacyEntries.Length == 0 || !File.Exists(ExecutablePath))
                return;

            foreach (var valueName in legacyEntries)
                key.DeleteValue(valueName, false);
            key.SetValue(EntryName, Quote(ExecutablePath), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Legacy startup registration migration failed.", ex);
        }
    }

    private bool IsCurrentInstallDirectory(string executable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
        var currentDirectory = Path.GetDirectoryName(ExecutablePath);
        return directory != null
            && currentDirectory != null
            && directory.Equals(currentDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedEntryName(string name)
    {
        return name.StartsWith(EntryPrefix, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(LegacyEntryPrefix, StringComparison.OrdinalIgnoreCase);
    }
    private bool IsCurrentExecutableCommand(string? command)
    {
        var executable = ExtractExecutablePath(command);
        return executable != null && string.Equals(
            Path.GetFullPath(executable),
            ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var trimmed = command.Trim();
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }
        var separator = trimmed.IndexOf(' ');
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private static string Quote(string value) => $"\"{value}\"";
}
