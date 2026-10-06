using System.Collections.Concurrent;

namespace FileHound.Indexing;

/// <summary>
/// Keeps a Standard-mode <see cref="VolumeIndex"/> current using a recursive <see cref="FileSystemWatcher"/>.
/// Events are queued and applied in batches; directories that appear (e.g. moved in) are walked.
/// A watcher buffer overflow raises <see cref="Overflowed"/> so the owner can rescan.
/// </summary>
public sealed class WatcherUpdater : IDisposable
{
    private enum Kind : byte { Created, Deleted, Changed, Renamed }
    private readonly record struct Change(Kind Kind, string Path, string? OldPath);

    private readonly VolumeIndex _index;
    private readonly string _rootPath;
    private readonly DirectoryWalker _walker;
    private readonly TimeSpan _interval;
    private readonly ConcurrentQueue<Change> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Task? _loop;

    public WatcherUpdater(VolumeIndex index, string rootPath, DirectoryWalker walker, TimeSpan drainInterval)
    {
        _index = index;
        _rootPath = rootPath;
        _walker = walker;
        _interval = drainInterval;
    }

    /// <summary>Raised (on a background thread) after a batch changed the index.</summary>
    public event EventHandler? Applied;
    /// <summary>Raised when the OS dropped events; the index may be stale until rescanned.</summary>
    public event EventHandler? Overflowed;

    private volatile bool _paused;

    /// <summary>
    /// Starts watching. When <paramref name="paused"/> is true, changes are only queued until <see cref="Resume"/>
    /// (used to capture changes made while the initial walk is still running).
    /// </summary>
    public void Start(bool paused = false)
    {
        _paused = paused;
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 256 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
        };
        _watcher.Created += (_, e) => Enqueue(new Change(Kind.Created, e.FullPath, null));
        _watcher.Deleted += (_, e) => Enqueue(new Change(Kind.Deleted, e.FullPath, null));
        _watcher.Changed += (_, e) => Enqueue(new Change(Kind.Changed, e.FullPath, null));
        _watcher.Renamed += (_, e) => Enqueue(new Change(Kind.Renamed, e.FullPath, e.OldFullPath));
        // Any error (buffer overflow or a broken watch) means events were or will be lost: the index needs a rescan.
        _watcher.Error += (_, _) => SignalLost();
        _watcher.EnableRaisingEvents = true;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Upper bound on queued changes; beyond it we stop queueing and ask for a rescan instead.</summary>
    public const int MaxQueuedChanges = 200_000;
    private int _lostWhilePaused;

    private void Enqueue(Change c)
    {
        if (_queue.Count >= MaxQueuedChanges) { SignalLost(); return; }
        _queue.Enqueue(c);
    }

    private void SignalLost()
    {
        if (_paused) Volatile.Write(ref _lostWhilePaused, 1); // reported once on Resume
        else Overflowed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts applying changes, beginning with everything queued while paused.</summary>
    public void Resume()
    {
        _paused = false;
        if (Interlocked.Exchange(ref _lostWhilePaused, 0) == 1)
        {
            _queue.Clear();
            Overflowed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_paused || _queue.IsEmpty) continue;
                bool changed = false;
                var walks = new List<(string Path, int Entry)>(1);
                while (_queue.TryDequeue(out var c))
                {
                    try { changed |= Apply(c, walks); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                    // Walk new directories before applying later events so their children are not added twice.
                    foreach (var (path, entry) in walks)
                    {
                        try { await _walker.WalkAsync(_index, path, entry, null, _cts.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    walks.Clear();
                }
                if (changed) Applied?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool Apply(Change c, List<(string, int)> walks)
    {
        switch (c.Kind)
        {
            case Kind.Created:
                return Upsert(c.Path, walks);
            case Kind.Deleted:
            {
                int e = _index.FindByPath(c.Path);
                if (e <= 0) return false;
                _index.Delete(e);
                return true;
            }
            case Kind.Changed:
                return RefreshMetadata(_index.FindByPath(c.Path), c.Path);
            case Kind.Renamed:
            {
                int e = _index.FindByPath(c.OldPath!);
                int newParent = _index.FindByPath(Path.GetDirectoryName(c.Path) ?? string.Empty);
                if (e > 0 && newParent >= 0)
                {
                    if (!_index.Rename(e, newParent, Path.GetFileName(c.Path))) _index.Delete(e); // impossible move: drop it
                    return true;
                }
                if (e > 0)
                {
                    // Moved somewhere we don't index: it's gone from the indexed tree.
                    _index.Delete(e);
                    return true;
                }
                return Upsert(c.Path, walks);
            }
        }
        return false;
    }

    private bool Upsert(string path, List<(string, int)> walks)
    {
        int existing = _index.FindByPath(path);
        if (existing > 0) return RefreshMetadata(existing, path);

        int parent = _index.FindByPath(Path.GetDirectoryName(path) ?? string.Empty);
        if (parent < 0) return false;
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists) return false;
        var flags = EntryFlagsExtensions.FromAttributes((uint)info.Attributes) | EntryFlags.MetadataKnown;
        long size = info is FileInfo fi ? fi.Length : 0;
        int id = _index.Add(parent, info.Name, flags, size, info.LastWriteTimeUtc.Ticks);
        if (info is DirectoryInfo && (info.Attributes & FileAttributes.ReparsePoint) == 0) walks.Add((path, id));
        return true;
    }

    private bool RefreshMetadata(int e, string path)
    {
        if (e <= 0) return false;
        var info = new FileInfo(path);
        if (!info.Exists && !Directory.Exists(path)) return false;
        _index.Lock.EnterReadLock();
        try
        {
            _index.SetMetadata(e, info.Exists ? info.Length : 0, (info.Exists ? info.LastWriteTimeUtc : Directory.GetLastWriteTimeUtc(path)).Ticks);
            _index.SetAttributes(e, EntryFlagsExtensions.FromAttributes((uint)File.GetAttributes(path)));
        }
        finally { _index.Lock.ExitReadLock(); }
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
