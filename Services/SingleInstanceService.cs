using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AgentCompanion.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _settingsEvent;
    private RegisteredWaitHandle? _activationWait;
    private RegisteredWaitHandle? _settingsWait;
    private bool _ownsMutex;

    private SingleInstanceService(
        Mutex mutex,
        EventWaitHandle activationEvent,
        EventWaitHandle settingsEvent,
        bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _settingsEvent = settingsEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceService Acquire()
    {
        var installId = GetInstallId();
        var mutex = new Mutex(true, $@"Local\AgentCompanion-{installId}", out var createdNew);
        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\AgentCompanion-{installId}-Activate");
        var settingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\AgentCompanion-{installId}-Settings");
        return new SingleInstanceService(mutex, activationEvent, settingsEvent, createdNew);
    }

    public void NotifyPrimary(bool showSettings = false)
    {
        if (IsPrimary)
            return;

        if (showSettings)
            _settingsEvent.Set();
        else
            _activationEvent.Set();
    }

    public void Listen(Action activationRequested, Action settingsRequested)
    {
        if (!IsPrimary || _activationWait != null || _settingsWait != null)
            return;

        _activationWait = Register(_activationEvent, activationRequested);
        _settingsWait = Register(_settingsEvent, settingsRequested);
    }

    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _settingsWait?.Unregister(null);
        _activationEvent.Dispose();
        _settingsEvent.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }

    internal static string GetInstallId()
    {
        var path = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    private static RegisteredWaitHandle Register(EventWaitHandle waitHandle, Action callback)
    {
        return ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, timedOut) =>
            {
                if (!timedOut)
                    callback();
            },
            null,
            Timeout.Infinite,
            false);
    }
}
