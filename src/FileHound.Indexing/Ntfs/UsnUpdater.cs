using System.Runtime.InteropServices;
using FileHound.Core.Recovery;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Recovery;

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
    /// <summary>
    /// Raised for every file that leaves the volume's tree — a delete, a move into the Recycle Bin, or a Windows 11 POSIX
    /// delete (rename into <c>\$Extend\$Deleted</c>) — while the index entry still exists, so the parent path is known.
    /// Raised with the write lock held: handlers must be quick and must not touch the index.
    /// </summary>
    public event EventHandler<UsnDeletion>? Deleted;

    /// <summary>Recent deletes by (parent record, lowercase name) for save-by-replace detection (FR-6).</summary>
    private readonly Dictionary<(long, string), (long RecordNo, ushort Sequence, long UsnTicks)> _recentDeletes = [];
    private static readonly TimeSpan ReplaceWindow = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// Replays the journal's history from its oldest record up to <paramref name="untilUsn"/> (exclusive), raising only
    /// <see cref="Deleted"/> — the index is not modified, because those changes are already reflected in it.
    /// Returns the USN the replay started from, or -1 when the journal could not be opened.
    /// </summary>
    internal unsafe long ReplayHistory(long untilUsn, CancellationToken ct)
    {
        using var h = Kernel32.OpenVolume(_letter);
        if (h.IsInvalid) return -1;
        if (!MftScanner.TryQueryJournal(_letter, out ulong journalId, out long firstUsn, out long nextUsn)) return -1;
        long stop = untilUsn > 0 ? Math.Min(untilUsn, nextUsn) : nextUsn;
        if (firstUsn >= stop) return firstUsn;
        const int BufferSize = 256 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(BufferSize);
        try
        {
            long cursor = firstUsn;
            while (cursor < stop && !ct.IsCancellationRequested)
            {
                var read = new ReadUsnJournalDataV1
                {
                    StartUsn = cursor, ReasonMask = (uint)(UsnReason.FileDelete | UsnReason.RenameNewName | UsnReason.FileCreate | UsnReason.Close),
                    ReturnOnlyOnClose = 1, Timeout = 0, BytesToWaitFor = 0, UsnJournalID = journalId, MinMajorVersion = 2, MaxMajorVersion = 3,
                };
                if (!Kernel32.DeviceIoControl(h, Kernel32.FSCTL_READ_USN_JOURNAL, &read, sizeof(ReadUsnJournalDataV1), buffer, BufferSize, out int returned, 0)) break;
                long next = *(long*)buffer;
                if (returned > 8) ReplayDeletions(new ReadOnlySpan<byte>(buffer + 8, returned - 8), stop);
                if (returned <= 8 || next == cursor) break;
                cursor = next;
            }
            return firstUsn;
        }
        finally { NativeMemory.Free(buffer); }
    }

    /// <summary>History replay: raise <see cref="Deleted"/> for deletes and leaves-tree renames without touching the index.</summary>
    private void ReplayDeletions(ReadOnlySpan<byte> records, long stopUsn)
    {
        // Write lock (not read): RaiseDeleted/NoteReplacement mutate _recentDeletes, and two replays may overlap.
        _index.Lock.EnterWriteLock();
        try
        {
            while (UsnRecordParser.TryRead(records, out var rec))
            {
                records = records[rec.Length..];
                if (rec.Usn >= stopUsn) return;
                int parent = rec.ParentRecordNo == RootRecord ? VolumeIndex.RootEntry : _index.FindByRecord(rec.ParentRecordNo);
                bool parentIndexed = parent == VolumeIndex.RootEntry || _index.IsLive(parent);
                if ((rec.Reason & UsnReason.FileDelete) != 0)
                {
                    // Without a resolvable parent the entry would be an "<unknown folder>" row, which in practice is the
                    // second half of a POSIX delete or an emptied Recycle Bin ($R...), both already covered.
                    if (parentIndexed && !IsPosixDeleteMarker(rec)) RaiseDeleted(rec, -1, parent, parentIndexed, DeletionKind.Deleted);
                }
                else if ((rec.Reason & UsnReason.RenameNewName) != 0)
                {
                    var leaving = ClassifyLeaving(rec, parent, parentIndexed);
                    if (leaving is { } kind) RaiseDeleted(rec, -1, -1, false, kind);
                }
                else if ((rec.Reason & UsnReason.FileCreate) != 0) NoteReplacement(rec);
            }
        }
        finally { _index.Lock.ExitWriteLock(); }
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
                    // An unindexed file in an unindexed folder is the tail of a POSIX delete ($Extend\$Deleted) or an
                    // emptied Recycle Bin: the deletion was already logged when the file left the tree.
                    if (e > 0 || (parentIndexed && !IsPosixDeleteMarker(rec))) RaiseDeleted(rec, e, parent, parentIndexed, DeletionKind.Deleted);
                    if (e > 0) { _index.Delete(e); changes++; }
                    continue;
                }
                if ((rec.Reason & (UsnReason.FileCreate | UsnReason.RenameNewName)) != 0 || e <= 0)
                {
                    bool skippedName = rec.ParentRecordNo == RootRecord && MftScanner.IsSkippedRootName(rec.Name);
                    // A rename into the Recycle Bin or a POSIX-delete marker leaves the tree even though those folders
                    // are themselves indexed; a destination that isn't indexed at all leaves it too.
                    var leaving = (rec.Reason & UsnReason.RenameNewName) != 0 && _index.IsLive(e) ? ClassifyLeaving(rec, parent, parentIndexed) : null;
                    if (!parentIndexed || skippedName || leaving is not null)
                    {
                        if (_index.IsLive(e))
                        {
                            RaiseDeleted(rec, e, -1, false, leaving ?? DeletionKind.Deleted);
                            _index.Delete(e);
                            changes++;
                        }
                        continue;
                    }
                    if ((rec.Reason & UsnReason.FileCreate) != 0) NoteReplacement(rec);
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

    /// <summary>
    /// Builds the deletion notification from the index entry (if still indexed) before it is removed.
    /// The parent path is the entry's own parent when known; otherwise the USN record's parent, when that is indexed.
    /// </summary>
    private void RaiseDeleted(in UsnRecord rec, int e, int usnParent, bool usnParentIndexed, DeletionKind kind)
    {
        var handler = Deleted;
        string name;
        string? parentPath = null;
        long size = -1, modified = 0;
        bool isDir = rec.IsDirectory;
        if (_index.IsLive(e))
        {
            name = _index.Name(e).ToString();
            parentPath = PathBuilder.GetParentPath(_index, e);
            size = (_index.Flags(e) & EntryFlags.MetadataKnown) != 0 ? _index.Size(e) : -1;
            modified = _index.ModifiedTicks(e);
            isDir = _index.IsDirectory(e);
        }
        else
        {
            name = rec.Name.ToString();
            if (usnParentIndexed) parentPath = usnParent == VolumeIndex.RootEntry ? _index.Root : PathBuilder.GetFullPath(_index, usnParent);
        }
        if (kind == DeletionKind.Deleted && !isDir)
        {
            var key = (rec.ParentRecordNo, name.ToLowerInvariant());
            _recentDeletes[key] = (rec.RecordNo, rec.Sequence, rec.TimestampUtc?.Ticks ?? DateTime.UtcNow.Ticks);
            if (_recentDeletes.Count > 4096) _recentDeletes.Clear();
        }
        if (handler is null) return;
        long deletedTicks = rec.TimestampUtc?.Ticks ?? DateTime.UtcNow.Ticks;
        handler(this, new UsnDeletion(rec.RecordNo, rec.Sequence, rec.ParentRecordNo, name, parentPath, size, isDir, modified, deletedTicks, rec.Usn, kind));
    }

    /// <summary>
    /// Decides whether a rename takes the file out of the indexed tree: a POSIX-delete marker or a move into
    /// $Recycle.Bin (Recycled), a move to an unindexed destination (Deleted), or null for an ordinary move.
    /// </summary>
    private DeletionKind? ClassifyLeaving(in UsnRecord rec, int newParent, bool newParentIndexed)
    {
        if (IsPosixDeleteMarker(rec)) return DeletionKind.Deleted;
        if (newParentIndexed && newParent > 0)
        {
            var path = PathBuilder.GetFullPath(_index, newParent);
            if (path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) || path.EndsWith(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                return DeletionKind.Recycled;
            return null;
        }
        // $Recycle.Bin is never indexed (MftScanner skips it), so a move into it shows up as a rename to an unindexed
        // parent; the new name tells it apart: the shell renames the file to $R + 6 random characters + extension.
        if (!newParentIndexed && IsRecycleBinDataName(rec.Name)) return DeletionKind.Recycled;
        return newParentIndexed ? null : DeletionKind.Deleted;
    }

    /// <summary><c>$R</c> followed by six letters or digits, then the original extension (or nothing).</summary>
    internal static bool IsRecycleBinDataName(ReadOnlySpan<char> name)
    {
        if (name.Length < 8 || name[0] != '$' || name[1] != 'R') return false;
        for (int i = 2; i < 8; i++) if (!char.IsAsciiLetterOrDigit(name[i])) return false;
        return name.Length == 8 || name[8] == '.';
    }

    /// <summary>
    /// Windows 11 deletes are POSIX unlinks: the file is first renamed into <c>\$Extend\$Deleted</c> as 24 hex characters
    /// whose first 16 are the file reference (or "16hex:name" on older builds). Requiring the embedded FRN to match keeps
    /// user files with all-hex names from being misclassified.
    /// </summary>
    private static bool IsPosixDeleteMarker(in UsnRecord rec)
    {
        var name = rec.Name;
        int colon = name.IndexOf(':');
        var hex = colon >= 0 ? name[..colon] : name;
        if (hex.Length != 16 && hex.Length != 24) return false;
        foreach (char c in hex) if (!char.IsAsciiHexDigit(c)) return false;
        if (!ulong.TryParse(hex[..16], System.Globalization.NumberStyles.HexNumber, null, out ulong frn)) return false;
        return (long)(frn & 0x0000_FFFF_FFFF_FFFF) == rec.RecordNo;
    }

    /// <summary>A create with the same name and parent as a delete seconds earlier is a save-by-replace: retag the deletion.</summary>
    private void NoteReplacement(in UsnRecord rec)
    {
        var key = (rec.ParentRecordNo, rec.Name.ToString().ToLowerInvariant());
        if (!_recentDeletes.Remove(key, out var prior)) return;
        long now = rec.TimestampUtc?.Ticks ?? DateTime.UtcNow.Ticks;
        if (now - prior.UsnTicks > ReplaceWindow.Ticks) return;
        Deleted?.Invoke(this, new UsnDeletion(prior.RecordNo, prior.Sequence, rec.ParentRecordNo, rec.Name.ToString(), null, -1, false, 0, now, rec.Usn, DeletionKind.Replaced));
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
