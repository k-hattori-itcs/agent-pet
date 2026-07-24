using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TokenPet.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _activationWait;
    private bool _ownsMutex;

    private SingleInstanceService(Mutex mutex, EventWaitHandle activationEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceService Acquire()
    {
        var installId = GetInstallId();
        var mutex = new Mutex(true, $"Local\\AgentPet-{installId}", out var createdNew);
        var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\AgentPet-{installId}-Activate");
        return new SingleInstanceService(mutex, activationEvent, createdNew);
    }

    public void NotifyPrimary()
    {
        if (!IsPrimary)
            _activationEvent.Set();
    }

    public void Listen(Action activationRequested)
    {
        if (!IsPrimary || _activationWait != null)
            return;
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                    activationRequested();
            },
            null,
            Timeout.Infinite,
            false);
    }

    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _activationEvent.Dispose();
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
}
