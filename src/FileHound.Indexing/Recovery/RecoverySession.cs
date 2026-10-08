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
    private string? _recoveryFolder;
    private bool _disposed;

    public RecoverySession(IndexManager manager, DriveDescriptor drive)
    {
        _manager = manager;
        Drive = drive;
        IsElevated = Elevation.IsElevated;
        StartedUtc = DateTime.UtcNow;
        Log = manager.TryGetDeletionLog(drive.Letter);
        manager.SuspendWrites(drive.Letter);
    }

    public DriveDescriptor Drive { get; }
    public bool IsElevated { get; }
    public DateTime StartedUtc { get; }
    /// <summary>The drive's deletion log (null when the drive is not in Turbo mode, i.e. not elevated).</summary>
    public DeletionLog? Log { get; }
    public IReadOnlyList<RecoveredFile> Recovered => _recovered;
    public string? RecoveryFolder => _recoveryFolder;

    /// <summary>Undelete and carving must never write to the drive they read from; compares volume GUIDs, not letters.</summary>
    public static bool IsDifferentVolume(string destinationFolder, DriveDescriptor source, out string reason)
    {
        reason = "";
        string? dest = Kernel32.VolumeGuidPathOf(destinationFolder);
        string? src = Kernel32.VolumeGuidPathOf(source.Root);
        bool same = dest is not null && src is not null
            ? string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetPathRoot(Path.GetFullPath(destinationFolder)), source.Root, StringComparison.OrdinalIgnoreCase);
        if (same) reason = $"This is the drive you're recovering from ({source.Letter}:) — pick another drive";
        return !same;
    }

    public IReadOnlyList<RecoveryCandidate> RecycleBinCandidates(bool allUsers) =>
        RecycleBinSource.ToCandidates(RecycleBinSource.Enumerate([Drive], allUsers && IsElevated)).ToList();

    /// <summary>Puts a Recycle Bin item back where it was. Returns the final path.</summary>
    public Task<string> RestoreAsync(RecoveryCandidate c, bool keepBoth) => Task.Run(() =>
        c.Key is RecycleBinItem item ? RecycleBinSource.Restore(item, keepBoth)
        : throw new NotSupportedException($"{c.Source} items are recovered to another drive, not restored in place."));

    /// <summary>Copies a candidate into the session's recovery folder on <paramref name="destinationFolder"/>, hashing it on the way.</summary>
    public async Task<RecoveredFile> RecoverAsync(RecoveryCandidate c, string destinationFolder, CancellationToken ct)
    {
        if (!IsDifferentVolume(destinationFolder, Drive, out string why) && c.Source is not RecoverySource.RecycleBin)
            return Fail(c, why);
        var folder = _recoveryFolder ??= Path.Combine(destinationFolder, $"FileHound Recovery {StartedUtc.ToLocalTime():yyyy-MM-dd HHmm}");
        RecoveredFile result;
        try
        {
            result = c.Source switch
            {
                RecoverySource.RecycleBin when c.Key is RecycleBinItem item => await Task.Run(() => RecoverRecycled(c, item, folder, ct), ct),
                RecoverySource.DeletionLog => Fail(c, "Needs undelete, which is coming in the next build"),
                _ => Fail(c, $"{c.Source} recovery is not available yet"),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            result = Fail(c, ex.Message);
        }
        lock (_recovered) _recovered.Add(result);
        AppendManifest(folder, result);
        return result;
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
        new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.ResumeWrites(Drive.Letter);
    }
}
