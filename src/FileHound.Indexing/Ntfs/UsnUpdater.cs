using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Ntfs;

/// <summary>
/// Keeps a Turbo (MFT-built) index current by polling the NTFS USN change journal from
/// <see cref="VolumeIndex.NextUsn"/>. Raises <see cref="JournalInvalid"/> when the journal wrapped, was deleted
/// or recreated, in which case the owner must rescan.
/// </summary>
public sealed class UsnUpdater : IDisposable
{
    private const long RootRecord = 5;
    private const UsnReason Mask = UsnReason.FileCreate | UsnReason.FileDelete | UsnReason.RenameNewName | UsnReason.RenameOldName |
                                   UsnReason.DataChanges | UsnReason.BasicInfoChange | UsnReason.HardLinkChange | UsnReason.Close;

    private readonly VolumeIndex _index;
    private readonly char _letter;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UsnUpdater(VolumeIndex index, char letter, TimeSpan pollInterval)
    {
        _index = index;
        _letter = letter;
        _interval = pollInterval;
    }

    public event EventHandler? Applied;
    public event EventHandler? JournalInvalid;
    /// <summary>Raised (on a background thread) when the poll loop hit an unexpected error and stopped; the owner must rescan.</summary>
    public event EventHandler<Exception>? Faulted;

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                int result = Poll();
                if (result < 0) { JournalInvalid?.Invoke(this, EventArgs.Empty); return; }
                if (result > 0) Applied?.Invoke(this, EventArgs.Empty);
            }
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            // NextUsn only advances after a buffer applied cleanly, so retrying would hit the same record again.
            // Dying silently would leave the drive "Ready" while its index quietly went stale: tell the owner instead.
            Faulted?.Invoke(this, ex);
        }
    }

    /// <summary>Reads all pending journal records. Returns the number applied, or -1 if the journal is invalid.</summary>
    private unsafe int Poll()
    {
        using var h = Kernel32.OpenVolume(_letter);
        if (h.IsInvalid) return -1;
        const int BufferSize = 256 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        int applied = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = new ReadUsnJournalDataV1
                {
                    StartUsn = _index.NextUsn,
                    ReasonMask = (uint)Mask,
                    ReturnOnlyOnClose = 1,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = _index.UsnJournalId,
                    MinMajorVersion = 2,
                    MaxMajorVersion = 3,
                };
                if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_READ_USN_JOURNAL, &read, sizeof(ReadUsnJournalDataV1), buffer, BufferSize, out int returned, 0))
                {
                    int err = Marshal.GetLastPInvokeError();
                    return err is Kernel32.ERROR_JOURNAL_ENTRY_DELETED or Kernel32.ERROR_JOURNAL_NOT_ACTIVE or Kernel32.ERROR_JOURNAL_DELETE_IN_PROGRESS or 87 /* invalid parameter: journal id mismatch */
                        ? -1 : applied;
                }
                long next = *(long*)buffer;
                if (returned > 8) applied += ApplyBuffer(new ReadOnlySpan<byte>(buffer + 8, returned - 8));
                bool advanced = next != _index.NextUsn;
                _index.NextUsn = next;
                if (returned <= 8 || !advanced) break;
            }
        }
        finally { NativeMemory.Free(buffer); }
        return applied;
    }

    /// <summary>Applies a buffer of USN records (without the leading 8-byte USN). Returns the number of changes.</summary>
    internal int ApplyBuffer(ReadOnlySpan<byte> records)
    {
        int changes = 0;
        var refresh = new List<int>();
        var restoredDirs = new List<int>();
        _index.Lock.EnterWriteLock();
        try
        {
            while (UsnRecordParser.TryRead(records, out var rec))
            {
                records = records[rec.Length..];
                int parent = rec.ParentRecordNo == RootRecord ? VolumeIndex.RootEntry : _index.FindByRecord(rec.ParentRecordNo);
                bool parentIndexed = parent == VolumeIndex.RootEntry || _index.IsLive(parent);
                int e = _index.FindByRecord(rec.RecordNo);
                if (e < 0 && parentIndexed)
                {
                    // Unmapped but present by name (e.g. re-walked after a restore): adopt it.
                    int byName = _index.FindChild(parent, rec.Name);
                    if (byName > 0 && _index.RecordOf(byName) < 0)
                    {
                        _index.SetRecord(byName, rec.RecordNo);
                        e = byName;
                    }
                }
                if ((rec.Reason & UsnReason.FileDelete) != 0)
                {
                    if (e > 0) { _index.Delete(e); changes++; }
                    continue;
                }
                if ((rec.Reason & (UsnReason.FileCreate | UsnReason.RenameNewName)) != 0 || e <= 0)
                {
                    bool skippedName = rec.ParentRecordNo == RootRecord && MftScanner.IsSkippedRootName(rec.Name);
                    if (!parentIndexed || skippedName)
                    {
                        // Created or moved outside the indexed tree (Recycle Bin, metafile area, excluded folder):
                        // if we knew the item, it has left the tree.
                        if (_index.IsLive(e)) { _index.Delete(e); changes++; }
                        continue;
                    }
                    var flags = EntryFlagsExtensions.FromAttributes(rec.Attributes);
                    if (_index.IsLive(e))
                    {
                        if (_index.Rename(e, parent, rec.Name)) _index.SetAttributes(e, flags);
                        else _index.Delete(e); // out-of-order nested move would create a cycle; drop it
                    }
                    else
                    {
                        e = _index.Add(parent, rec.Name, flags, 0, 0, rec.RecordNo);
                        // A directory appearing from outside the tree (Recycle Bin restore, move-in) brings a subtree
                        // that has no journal records of its own: rebuild it from disk below.
                        if (rec.IsDirectory && (rec.Reason & UsnReason.FileCreate) == 0) restoredDirs.Add(e);
                    }
                    changes++;
                    refresh.Add(e);
                    continue;
                }
                if ((rec.Reason & (UsnReason.DataChanges | UsnReason.BasicInfoChange)) != 0 && _index.IsLive(e))
                {
                    _index.SetAttributes(e, EntryFlagsExtensions.FromAttributes(rec.Attributes));
                    refresh.Add(e);
                    changes++;
                }
            }
        }
        finally { _index.Lock.ExitWriteLock(); }

        foreach (int dir in restoredDirs) RebuildSubtree(dir);
        foreach (int e in refresh) RefreshMetadata(e);
        return changes;
    }

    /// <summary>Walks a directory that re-entered the tree and maps every new entry to its NTFS record number.</summary>
    private void RebuildSubtree(int dir)
    {
        string path;
        _index.Lock.EnterReadLock();
        try
        {
            if (!_index.IsLive(dir)) return;
            path = PathBuilder.GetFullPath(_index, dir);
        }
        finally { _index.Lock.ExitReadLock(); }
        try
        {
            new DirectoryWalker([], 2).WalkAsync(_index, path, dir, null, _cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { return; }

        var stack = new Stack<(int Entry, string Path)>();
        stack.Push((dir, path));
        while (stack.Count > 0)
        {
            var (d, dPath) = stack.Pop();
            var children = new List<(int, string)>();
            _index.Lock.EnterReadLock();
            try
            {
                for (int c = _index.FirstChild(d); c > 0; c = _index.NextSibling(c))
                    if (_index.IsLive(c)) children.Add((c, Path.Combine(dPath, _index.Name(c).ToString())));
            }
            finally { _index.Lock.ExitReadLock(); }
            foreach (var (c, cPath) in children)
            {
                if (Kernel32.TryGetRecordNumber(cPath, out long recordNo)) _index.SetRecord(c, recordNo);
                if (_index.IsDirectory(c)) stack.Push((c, cPath));
            }
        }
    }

    private void RefreshMetadata(int e)
    {
        string path;
        _index.Lock.EnterReadLock();
        try
        {
            if (!_index.IsLive(e)) return;
            path = PathBuilder.GetFullPath(_index, e);
        }
        finally { _index.Lock.ExitReadLock(); }

        try
        {
            var fi = new FileInfo(path);
            long size; DateTime mod;
            if (fi.Exists) { size = fi.Length; mod = fi.LastWriteTimeUtc; }
            else if (Directory.Exists(path)) { size = 0; mod = Directory.GetLastWriteTimeUtc(path); }
            else return;
            _index.Lock.EnterReadLock();
            try { if (_index.IsLive(e)) _index.SetMetadata(e, size, mod.Ticks); }
            finally { _index.Lock.ExitReadLock(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
