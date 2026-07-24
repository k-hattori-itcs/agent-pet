using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AgentCompanion.Services;

public sealed class CodexLauncherService
{
    private const int SW_RESTORE = 9;
    private const string CodexAppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private readonly string _codexExePath;

    public CodexLauncherService()
    {
        _codexExePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI", "Codex", "bin", "codex.exe");
    }

    public void OpenOrFocus()
    {
        if (TryFocusCodexWindow())
            return;

        if (!TryLaunchCodexDesktopApp() && File.Exists(_codexExePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _codexExePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(_codexExePath) ?? string.Empty
            });
        }

        Task.Run(async () =>
        {
            for (var i = 0; i < 32; i++)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (TryFocusCodexWindow())
                    break;
            }
        });
    }

    private static bool TryLaunchCodexDesktopApp()
    {
        try
        {
            var explorerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            if (!File.Exists(explorerPath))
                return false;
            var startInfo = new ProcessStartInfo
            {
                FileName = explorerPath,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add($"shell:AppsFolder\\{CodexAppUserModelId}");
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Codex desktop launch failed.", ex);
            return false;
        }
    }

    private static bool TryFocusCodexWindow()
    {
        var processIds = Process.GetProcesses()
            .Where(IsCodexDesktopProcess)
            .Select(process => process.Id)
            .ToHashSet();

        if (processIds.Count == 0)
            return false;

        var handle = FindBestWindow(processIds);
        if (handle == IntPtr.Zero)
            return false;

        ShowWindow(handle, SW_RESTORE);
        SetForegroundWindow(handle);
        return true;
    }

    private static bool IsCodexDesktopProcess(Process process)
    {
        try
        {
            var name = process.ProcessName;
            var path = process.MainModule?.FileName ?? string.Empty;
            if (name.Equals("codex", StringComparison.OrdinalIgnoreCase))
                return true;
            return name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Codex desktop launch failed.", ex);
            return false;
        }
    }

    private static IntPtr FindBestWindow(HashSet<int> processIds)
    {
        var fallback = IntPtr.Zero;
        var titled = IntPtr.Zero;

        EnumWindows((handle, _) =>
        {
            var windowThreadId = GetWindowThreadProcessId(handle, out var windowProcessId);
            if (windowThreadId == 0)
                return true;
            if (!processIds.Contains(windowProcessId) || !IsWindowVisible(handle))
                return true;

            if (fallback == IntPtr.Zero)
                fallback = handle;

            if (GetWindowTextLength(handle) > 0)
            {
                titled = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return titled != IntPtr.Zero ? titled : fallback;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
