using System.Security.Cryptography;
using FileHound.Core.Recovery;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// One recovery session for one drive. While it is open, FileHound's own writes to that drive (index snapshots, the
/// deletion log) are suspended so nothing overwrites clusters the user may still want back. Recovered files go into a
/// dated folder on the destination with a <c>manifest.csv</c> of SHA-256 hashes.
/// </summary>
public sealed class RecoverySession : IDisposable
{
    private readonly IndexManager _manager;
    private readonly List<RecoveredFile> _recovered = [];
    private readonly object _lazyGate = new();
    private string? _recoveryFolder;
    private bool _disposed;
    private VolumeReader? _reader;
    private ClusterBitmap? _bitmap;
    private IReadOnlyList<RecoveryCandidate> _lastUndelete = [];
    private Carver? _carver;

    public RecoverySession(IndexManager manager, DriveDescriptor drive)
    {
        _manager = manager;
        Drive = drive;
        IsElevated = Elevation.IsElevated;
        StartedUtc = DateTime.UtcNow;
        manager.SuspendWrites(drive.Letter);
    }

    public DriveDescriptor Drive { get; }
    public bool IsElevated { get; }
    public DateTime StartedUtc { get; }
    /// <summary>The drive's deletion log (null when the drive is not in Turbo mode, i.e. not elevated).</summary>
    /// <summary>Looked up live: a drive still being indexed gets its log only when its Turbo scan finishes.</summary>
    public DeletionLog? Log => _manager.TryGetDeletionLog(Drive.Letter);
    /// <summary>The drive's current index state (mode and progress), for explaining why a source is not available yet.</summary>
    public DriveState? State => _manager.Drives.FirstOrDefault(s => s.Drive.Letter == Drive.Letter);
    public IReadOnlyList<RecoveredFile> Recovered => _recovered;
    public string? RecoveryFolder => _recoveryFolder;

    /// <summary>Raw access to the drive, opened on first use (throws <see cref="NotSupportedException"/> when no path reads).</summary>
    public VolumeReader Reader
    {
        get
        {
            lock (_lazyGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _reader ??= VolumeReader.Open(Drive);
            }
        }
    }

    /// <summary>The cluster bitmap as of the last scan (or first use).</summary>
    public ClusterBitmap Bitmap
    {
        get { lock (_lazyGate) return _bitmap ??= ClusterBitmap.Load(Reader); }
    }

    public void RefreshBitmap()
    {
        lock (_lazyGate) _bitmap = ClusterBitmap.Load(Reader);
    }

    /// <summary>"Volume", "Physical disk" or "Shadow copy" once the reader is open; null before.</summary>
    public string? ReadPath => _reader?.Path switch
    {
        VolumeReadPath.Volume => "Volume",
        VolumeReadPath.PhysicalDisk => "Physical disk",
        VolumeReadPath.ShadowCopy => "Shadow copy",
        VolumeReadPath.Memory => "Memory",
        _ => null,
    };

    /// <summary>FR-21…FR-25: carves free space. Results are de-duplicated against the last undelete scan (FR-23).</summary>
    public Task<IReadOnlyList<RecoveryCandidate>> CarveAsync(IReadOnlyCollection<string>? typeFilter, IProgress<CarveProgress>? progress, Action<IReadOnlyList<RecoveryCandidate>>? batch, CancellationToken ct) => Task.Run(() =>
    {
        RefreshBitmap();
        var carver = new Carver(Reader, Bitmap) { TypeFilter = typeFilter };
        _carver = carver;
        if (batch is not null) carver.Batch += files => batch(CarveWriter.Deduplicate(files, _lastUndelete).Select(ToCandidate).ToList());
        try
        {
            var found = carver.Run(progress, ct);
            return (IReadOnlyList<RecoveryCandidate>)CarveWriter.Deduplicate(found, _lastUndelete).Select(ToCandidate).ToList();
        }
        finally { Interlocked.CompareExchange(ref _carver, null, carver); }
    }, ct);

    public bool IsCarvePaused => _carver?.IsPaused ?? false;
    public void PauseCarve() => _carver?.Pause();
    public void ResumeCarve() => _carver?.Resume();

    private static RecoveryCandidate ToCandidate(CarvedFile f) =>
        new(RecoverySource.Carving, f.SuggestedName, null, f.Size, null, null, RecoveryGrade.Excellent, 100, false, f.Info is null ? f.Type.Label : $"{f.Type.Label} · {f.Info}", f);

    /// <summary>FR-11…FR-14: scans the MFT for deleted records. Re-reads the bitmap first so grades are current.</summary>
    public Task<IReadOnlyList<RecoveryCandidate>> UndeleteAsync(IProgress<UndeleteProgress>? progress, CancellationToken ct) => Task.Run(() =>
    {
        RefreshBitmap();
        var live = _manager.Volumes.FirstOrDefault(v => v.Root.Equals(Drive.Root, StringComparison.OrdinalIgnoreCase));
        var found = new MftUndeleteSource(Reader, Bitmap, live, Log?.Entries).Scan(progress, ct);
        _lastUndelete = found;
        return (IReadOnlyList<RecoveryCandidate>)found;
    }, ct);

    /// <summary>
    /// Undelete and carving must never write to the drive they read from. Opens the destination and compares the volume
    /// serial of what the handle lands on (junctions, symlinks and folder mount points resolved) with the source's;
    /// falls back to volume GUIDs, then to drive letters. Network and unresolvable paths are refused (fail closed).
    /// </summary>
    public static bool IsDifferentVolume(string destinationFolder, DriveDescriptor source, out string reason)
    {
        reason = "";
        string full;
        try { full = Path.GetFullPath(destinationFolder); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            reason = "That folder path isn't valid — pick a folder on another local drive";
            return false;
        }
        if (!Path.IsPathFullyQualified(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "Pick a folder on a local drive, not a network path";
            return false;
        }
        bool same;
        if (Kernel32.TryGetVolumeSerial(full, out uint destSerial) && Kernel32.TryGetVolumeSerial(source.Root, out uint srcSerial))
            same = destSerial == srcSerial;
        else if (Kernel32.VolumeGuidPathOf(full) is { } dest && Kernel32.VolumeGuidPathOf(source.Root) is { } src)
            same = string.Equals(dest, src, StringComparison.OrdinalIgnoreCase);
        else
            same = string.Equals(Path.GetPathRoot(full), source.Root, StringComparison.OrdinalIgnoreCase);
        if (same) reason = $"This is the drive you're recovering from ({source.Letter}:) — pick another drive";
        return !same;
    }

    public IReadOnlyList<RecoveryCandidate> RecycleBinCandidates(bool allUsers) =>
        RecycleBinSource.ToCandidates(RecycleBinSource.Enumerate([Drive], allUsers && IsElevated)).ToList();

    /// <summary>Puts a Recycle Bin item back where it was. Returns the final path.</summary>
    public Task<string> RestoreAsync(RecoveryCandidate c, bool keepBoth) => Task.Run(() => c.Key switch
    {
        RecycleBinItem item => RecycleBinSource.Restore(item, keepBoth),
        ShadowVersion v => ShadowCopySource.RestoreInPlace(v),
        _ => throw new NotSupportedException($"{c.Source} items are recovered to another drive, not restored in place."),
    });

    /// <summary>Copies a candidate into the session's recovery folder on <paramref name="destinationFolder"/>, hashing it on the way.</summary>
    public async Task<RecoveredFile> RecoverAsync(RecoveryCandidate c, string destinationFolder, CancellationToken ct)
    {
        // Recycle Bin and snapshot data cannot be overwritten by the copy, so those may land on the same volume.
        if (!IsDifferentVolume(destinationFolder, Drive, out string why) && c.Source is not (RecoverySource.RecycleBin or RecoverySource.ShadowCopy))
            return Fail(c, why);
        // Refuse before creating the recovery folder: nothing to put in it.
        if (c.Source is RecoverySource.DeletionLog) return Fail(c, "Use the Undelete tab to bring this file back");
        if (c.Source is not (RecoverySource.RecycleBin or RecoverySource.Undelete or RecoverySource.ShadowCopy or RecoverySource.Carving)) return Fail(c, $"{c.Source} recovery is not available yet");
        var folder = _recoveryFolder ??= Path.Combine(destinationFolder, $"FileHound Recovery {StartedUtc.ToLocalTime():yyyy-MM-dd HHmm}");
        RecoveredFile result;
        try
        {
            result = c.Source switch
            {
                RecoverySource.RecycleBin when c.Key is RecycleBinItem item => await Task.Run(() => RecoverRecycled(c, item, folder, ct), ct),
                RecoverySource.Undelete when c.Key is UndeleteRecord u && u.IsDirectory => await Task.Run(() => RecoverUndeletedTree(c, u, folder, ct), ct),
                RecoverySource.Undelete when c.Key is UndeleteRecord u => await Task.Run(() => RecoverUndeleted(c, u, folder, ct), ct),
                RecoverySource.ShadowCopy when c.Key is ShadowVersion v => await Task.Run(() => RecoverShadow(c, v, folder, ct), ct),
                RecoverySource.Carving when c.Key is CarvedFile f => await Task.Run(() => RecoverCarved(c, f, folder, ct), ct),
                _ => Fail(c, $"{c.Source} recovery is not available yet"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // One bad candidate (corrupt record, closed handle, full disk) must not end the batch.
            IndexManager.Log?.Invoke($"Recovery of {c.OriginalPath} failed: {ex.GetType().Name}: {ex.Message}");
            result = Fail(c, ex.Message);
        }
        lock (_recovered) _recovered.Add(result);
        AppendManifest(folder, result);
        return result;
    }

    private static RecoveredFile RecoverShadow(RecoveryCandidate c, ShadowVersion v, string folder, CancellationToken ct)
    {
        string path = ShadowCopySource.SaveTo(v, folder);
        if (v.IsDirectory) return new RecoveredFile(c, path, DirectorySize(path), "", RecoveryGrade.Excellent, null);
        ct.ThrowIfCancellationRequested();
        return new RecoveredFile(c, path, new FileInfo(path).Length, Sha256Of(path), RecoveryGrade.Excellent, null);
    }

    private RecoveredFile RecoverCarved(RecoveryCandidate c, CarvedFile f, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string path = RecycleBinSource.UniquePath(Path.Combine(folder, f.SuggestedName), " (recovered)");
        var (bytes, sha, runs, finalPath) = CarveWriter.Recover(Reader, f, path, ct);
        return new RecoveredFile(c, finalPath, bytes, sha, RecoveryGrade.Excellent, null, runs);
    }

    private RecoveredFile RecoverUndeleted(RecoveryCandidate c, UndeleteRecord u, string folder, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string path = RecycleBinSource.UniquePath(Path.Combine(folder, SafeName(c.Name)), " (recovered)");
        var (bytes, sha, grade, runs) = UndeleteWriter.Recover(Reader, Bitmap, u, c.Grade, path, ct);
        return new RecoveredFile(c, path, bytes, sha, grade, null, runs);
    }

    /// <summary>A deleted folder: recovers every scanned child whose parent reference points at this record (same sequence), recursively.</summary>
    private RecoveredFile RecoverUndeletedTree(RecoveryCandidate c, UndeleteRecord dir, string folder, CancellationToken ct, HashSet<long>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(dir.RecordNo) || visited.Count > 64) return Fail(c, "folder structure loops back on itself");
        string target = RecycleBinSource.UniquePath(Path.Combine(folder, SafeName(c.Name)), " (recovered)");
        Directory.CreateDirectory(target);
        long bytes = 0;
        var worst = RecoveryGrade.Excellent;
        int files = 0, failed = 0;
        foreach (var child in MftUndeleteSource.ChildrenOf(_lastUndelete, dir))
        {
            ct.ThrowIfCancellationRequested();
            var r = (UndeleteRecord)child.Key;
            RecoveredFile childResult;
            try { childResult = r.IsDirectory ? RecoverUndeletedTree(child, r, target, ct, visited) : RecoverUndeleted(child, r, target, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { childResult = Fail(child, ex.Message); }
            lock (_recovered) _recovered.Add(childResult);
            AppendManifest(folder, childResult);
            if (childResult.Succeeded) { bytes += childResult.Bytes; files++; if (Rank(childResult.FinalGrade) > Rank(worst)) worst = childResult.FinalGrade; }
            else failed++;
        }
        return new RecoveredFile(c, target, bytes, "", worst, failed == 0 ? null : $"{failed} of {files + failed} items failed");
    }

    private static int Rank(RecoveryGrade g) => g switch
    {
        RecoveryGrade.Excellent => 0, RecoveryGrade.Good => 1, RecoveryGrade.Partial => 2, RecoveryGrade.Zeroed => 3, RecoveryGrade.Overwritten => 4, _ => 5,
    };

    /// <summary>Names come from disk structures, so strip anything the file system would reject.</summary>
    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }

    private static RecoveredFile RecoverRecycled(RecoveryCandidate c, RecycleBinItem item, string folder, CancellationToken ct)
    {
        string path = RecycleBinSource.CopyTo(item, folder);
        if (item.IsDirectory) return new RecoveredFile(c, path, DirectorySize(path), "", RecoveryGrade.Excellent, null);
        ct.ThrowIfCancellationRequested();
        return new RecoveredFile(c, path, new FileInfo(path).Length, Sha256Of(path), RecoveryGrade.Excellent, null);
    }

    private static RecoveredFile Fail(RecoveryCandidate c, string error) => new(c, "", 0, "", c.Grade, error);

    private static void AppendManifest(string folder, RecoveredFile r)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var manifest = Path.Combine(folder, "manifest.csv");
            bool fresh = !File.Exists(manifest);
            using var w = new StreamWriter(manifest, append: true);
            if (fresh) CsvExport.WriteManifest(w, [r]);
            else { var sw = new StringWriter(); CsvExport.WriteManifest(sw, [r]); w.Write(sw.ToString().Split('\n', 2)[1]); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { IndexManager.Log?.Invoke($"manifest write failed: {ex.Message}"); }
    }

    public void ExportDfxml(string path)
    {
        using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        lock (_recovered) DfxmlExport.Write(w, $@"\\.\{Drive.Letter}:", _recovered.ToList(), StartedUtc, DateTime.UtcNow);
    }

    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static long DirectorySize(string path) =>
        new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => f.Length);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lazyGate) { _reader?.Dispose(); _reader = null; _bitmap = null; }
        _manager.ResumeWrites(Drive.Letter);
    }
}
