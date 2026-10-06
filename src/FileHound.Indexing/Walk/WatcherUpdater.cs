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

    public void Start()
    {
        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes,
        };
        _watcher.Created += (_, e) => _queue.Enqueue(new Change(Kind.Created, e.FullPath, null));
        _watcher.Deleted += (_, e) => _queue.Enqueue(new Change(Kind.Deleted, e.FullPath, null));
        _watcher.Changed += (_, e) => _queue.Enqueue(new Change(Kind.Changed, e.FullPath, null));
        _watcher.Renamed += (_, e) => _queue.Enqueue(new Change(Kind.Renamed, e.FullPath, e.OldFullPath));
        _watcher.Error += (_, _) => Overflowed?.Invoke(this, EventArgs.Empty);
        _watcher.EnableRaisingEvents = true;
        _loop = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_queue.IsEmpty) continue;
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
                    _index.Rename(e, newParent, Path.GetFileName(c.Path));
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
