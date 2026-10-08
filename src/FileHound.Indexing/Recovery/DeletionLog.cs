using FileHound.Core.Recovery;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Recovery;

/// <summary>One deletion as seen by <see cref="UsnUpdater"/>. <c>Kind == Replaced</c> retags an earlier entry with the same record and sequence.</summary>
public sealed record UsnDeletion(
    long RecordNo, ushort Sequence, long ParentRecordNo, string Name, string? ParentPath, long Size, bool IsDirectory,
    long ModifiedUtcTicks, long DeletedUtcTicks, long Usn, DeletionKind Kind);

/// <summary>
/// The "recently deleted" list for one volume: subscribes to the live USN updater, keeps a bounded history in a
/// <see cref="DeletionLogStore"/>, and persists it (every 30 s when dirty and on dispose) unless persistence is suspended
/// because a recovery session must not write to that drive.
/// </summary>
public sealed class DeletionLog : IDisposable
{
    private readonly UsnUpdater _updater;
    private readonly string _storePath;
    private readonly DeletionLogStore _store;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(30));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _saveLoop;
    private volatile bool _suspended;
    private int _backfilled;

    public DeletionLog(UsnUpdater updater, string storePath)
    {
        _updater = updater;
        _storePath = storePath;
        _store = DeletionLogStore.Load(storePath);
        _updater.Deleted += OnDeleted;
        _saveLoop = Task.Run(SaveLoopAsync);
    }

    /// <summary>Entries newest first.</summary>
    public IReadOnlyList<DeletionEntry> Entries => _store.Snapshot();
    public int Count => _store.Count;
    public event EventHandler? Changed;

    /// <summary>While on, nothing is written to disk (the store stays in memory); turning it off flushes.</summary>
    public void SuspendPersistence(bool on)
    {
        _suspended = on;
        if (!on) Flush();
    }

    /// <summary>
    /// Reads the journal from its oldest available record up to the newest one already in the store, so deletions that
    /// happened while FileHound was not running are listed. Needs the volume handle (elevated); otherwise a no-op.
    /// </summary>
    public Task BackfillAsync(CancellationToken ct)
    {
        // Once per process: after a full replay the store covers everything from the journal's start, and the live
        // updater covers everything after; replaying again would only re-read the journal for nothing.
        if (Interlocked.CompareExchange(ref _backfilled, 1, 0) != 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            // Stop at the oldest entry already captured (0 = replay to the journal's end when the store is empty).
            long from = _updater.ReplayHistory(_store.FirstUsn, ct);
            if (from < 0 || ct.IsCancellationRequested) Volatile.Write(ref _backfilled, 0);
            if (from >= 0) Changed?.Invoke(this, EventArgs.Empty);
        }, ct);
    }

    public void Flush()
    {
        if (_suspended || !_store.IsDirty) return;
        try { _store.Save(_storePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { IndexManager.Log?.Invoke($"Deletion log save failed: {ex.Message}"); }
    }

    private void OnDeleted(object? sender, UsnDeletion d)
    {
        if (d.Kind == DeletionKind.Replaced)
        {
            if (_store.TryMark(d.RecordNo, d.Sequence, DeletionKind.Replaced)) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_store.Add(new DeletionEntry(d.RecordNo, d.Sequence, d.ParentRecordNo, d.Name, d.ParentPath ?? "", d.Size, d.IsDirectory,
                d.ModifiedUtcTicks, d.DeletedUtcTicks, d.Kind, d.Usn)))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveLoopAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false)) Flush();
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _updater.Deleted -= OnDeleted;
        _cts.Cancel();
        try { _saveLoop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        Flush();
        _timer.Dispose();
        _cts.Dispose();
    }
}
