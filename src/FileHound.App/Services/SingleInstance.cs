using System.Diagnostics;

namespace FileHound.App.Services;

/// <summary>One FileHound per user session; a second launch asks the first one to show itself.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FileHound.SingleInstance";
    private const string EventName = @"Local\FileHound.Activate";
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    private SingleInstance(Mutex? mutex, EventWaitHandle? activate, bool owned)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = owned;
    }

    public bool IsFirst => _owned;

    /// <summary>
    /// How long an elevated relaunch waits for the previous instance to exit. It must outlast that instance's shutdown
    /// (up to 20 s of snapshot saving, then settings and log flushes), or the two would read and write the same files.
    /// </summary>
    public static readonly TimeSpan PreviousInstanceExitTimeout = TimeSpan.FromSeconds(30);

    /// <param name="waitForPid">When relaunching elevated, the previous instance's pid; we wait for it to exit first.</param>
    public static SingleInstance Acquire(int? waitForPid)
    {
        if (waitForPid is { } pid)
        {
            try { Process.GetProcessById(pid).WaitForExit(PreviousInstanceExitTimeout); }
            catch (ArgumentException) { }
        }
        Mutex mutex;
        try { mutex = new Mutex(false, MutexName); }
        catch (UnauthorizedAccessException)
        {
            // An elevated (Turbo) FileHound owns the objects; a normal-integrity process may not open them.
            return new SingleInstance(null, null, owned: false) { ElevatedInstanceRunning = true };
        }
        bool owned;
        try { owned = mutex.WaitOne(waitForPid is null ? 0 : 5_000); }
        catch (AbandonedMutexException) { owned = true; }
        EventWaitHandle? evt;
        try { evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName); }
        catch (UnauthorizedAccessException) { evt = null; }
        return new SingleInstance(mutex, evt, owned) { ElevatedInstanceRunning = !owned && evt is null };
    }

    /// <summary>True when the running instance is elevated and can't be signalled from this process.</summary>
    public bool ElevatedInstanceRunning { get; private init; }

    public void SignalFirstInstance() => _activate?.Set();

    public void ListenForActivation(Action onActivate)
    {
        if (_activate is null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Releases the instance lock. Only called at the very end of shutdown, after snapshots and settings are written,
    /// so an elevated relaunch can never run alongside this process.
    /// </summary>
    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owned && _mutex is not null)
        {
            _owned = false;
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex?.Dispose();
        _activate?.Dispose();
    }
}
