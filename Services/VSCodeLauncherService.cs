using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TokenPet.Services;

public sealed class VSCodeLauncherService
{
    private const int SW_RESTORE = 9;
    private readonly string _defaultWorkspacePath;

    public VSCodeLauncherService()
    {
        _defaultWorkspacePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude");
    }

    public void OpenOrFocus(string? configuredWorkspacePath = null)
    {
        var workspacePath = ResolveWorkspacePath(configuredWorkspacePath);
        if (TryFocusVSCodeWindow(workspacePath))
            return;
        if (!TryLaunchVSCode(workspacePath))
            return;

        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 32; i++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (TryFocusVSCodeWindow(workspacePath))
                    break;
            }
        });
    }

    private string ResolveWorkspacePath(string? configuredWorkspacePath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredWorkspacePath)
            ? _defaultWorkspacePath
            : Environment.ExpandEnvironmentVariables(configuredWorkspacePath.Trim());
        if (Directory.Exists(candidate) || File.Exists(candidate))
            return Path.GetFullPath(candidate);
        return Directory.Exists(_defaultWorkspacePath)
            ? Path.GetFullPath(_defaultWorkspacePath)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static bool TryLaunchVSCode(string workspacePath)
    {
        var codePath = FindCodeExecutable();
        if (codePath == null)
        {
            AppLogger.Warning("VS Code executable was not found.");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = codePath,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(codePath) ?? string.Empty
            };
            startInfo.ArgumentList.Add(workspacePath);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("VS Code launch failed.", ex);
            return false;
        }
    }

    internal static string? FindCodeExecutable()
    {
        var candidates = new List<string?>
        {
            ReadAppPath(RegistryHive.CurrentUser, RegistryView.Default),
            ReadAppPath(RegistryHive.LocalMachine, RegistryView.Registry64),
            ReadAppPath(RegistryHive.LocalMachine, RegistryView.Registry32),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", "Code.exe")
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path!)))
            .FirstOrDefault(File.Exists);
    }

    private static string? ReadAppPath(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Code.exe");
            return key?.GetValue(null) as string;
        }
        catch (Exception ex)
        {
            AppLogger.Error("VS Code App Paths lookup failed.", ex);
            return null;
        }
    }

    private static bool TryFocusVSCodeWindow(string workspacePath)
    {
        var processIds = Process.GetProcessesByName("Code").Select(process => process.Id).ToHashSet();
        if (processIds.Count == 0)
            return false;

        var workspaceName = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var handle = FindBestWindow(processIds, workspaceName);
        if (handle == IntPtr.Zero)
            return false;

        ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        return true;
    }

    private static IntPtr FindBestWindow(HashSet<int> processIds, string workspaceName)
    {
        var fallback = IntPtr.Zero;
        var titled = IntPtr.Zero;
        var workspaceMatch = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var windowThreadId = GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowThreadId == 0)
                return true;
            if (!processIds.Contains(windowProcessId) || !IsWindowVisible(handle))
                return true;
            fallback = fallback == IntPtr.Zero ? handle : fallback;
            var title = GetWindowTitle(handle);
            titled = title.Length > 0 && titled == IntPtr.Zero ? handle : titled;
            if (!string.IsNullOrWhiteSpace(workspaceName) && title.Contains(workspaceName, StringComparison.OrdinalIgnoreCase))
            {
                workspaceMatch = handle;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return workspaceMatch != IntPtr.Zero ? workspaceMatch : titled != IntPtr.Zero ? titled : fallback;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return string.Empty;
        var buffer = new char[length + 1];
        _ = GetWindowText(handle, buffer, buffer.Length);
        return new string(buffer).TrimEnd('\0');
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
