using System.Diagnostics;

namespace FileHound.App.Services;

/// <summary>One FileHound per user session; a second launch asks the first one to show itself.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FileHound.SingleInstance";
    private const string EventName = @"Local\FileHound.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;
    private bool _owned;

    private SingleInstance(Mutex mutex, EventWaitHandle activate, bool owned)
    {
        _mutex = mutex;
        _activate = activate;
        _owned = owned;
    }

    public bool IsFirst => _owned;

    /// <param name="waitForPid">When relaunching elevated, the previous instance's pid; we wait for it to exit first.</param>
    public static SingleInstance Acquire(int? waitForPid)
    {
        if (waitForPid is { } pid)
        {
            try { Process.GetProcessById(pid).WaitForExit(10_000); }
            catch (ArgumentException) { }
        }
        var mutex = new Mutex(false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(waitForPid is null ? 0 : 5_000); }
        catch (AbandonedMutexException) { owned = true; }
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        return new SingleInstance(mutex, evt, owned);
    }

    public void SignalFirstInstance() => _activate.Set();

    public void ListenForActivation(Action onActivate) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);

    /// <summary>Lets an elevated relaunch take over before this process has fully exited.</summary>
    public void Release()
    {
        if (!_owned) return;
        _owned = false;
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        Release();
        _mutex.Dispose();
        _activate.Dispose();
    }
}
