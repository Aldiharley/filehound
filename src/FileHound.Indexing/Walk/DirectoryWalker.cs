using System.IO.Enumeration;
using System.Threading.Channels;

namespace FileHound.Indexing;

public sealed record ScanProgress(long Entries, long Directories, int Skipped, double Fraction);

/// <summary>
/// Standard-mode indexer: walks a directory tree in parallel with <see cref="FileSystemEnumerable{TResult}"/>
/// (NtQueryDirectoryFile with large buffers underneath) and adds every file and folder to a <see cref="VolumeIndex"/>.
/// Reparse-point directories are indexed but not descended into (avoids junction cycles and duplicates).
/// </summary>
public sealed class DirectoryWalker
{
    private static readonly HashSet<string> s_skipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$RECYCLE.BIN", "Config.Msi",
    };

    private readonly HashSet<string> _excluded;
    private readonly int _workers;

    public DirectoryWalker(IReadOnlyCollection<string> excludedPaths, int workers)
    {
        _excluded = new HashSet<string>(excludedPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        _workers = Math.Max(1, workers);
    }

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    private readonly record struct RawEntry(string Name, FileAttributes Attributes, long Length, long ModifiedTicks);

    private sealed class WalkState
    {
        public long Entries;
        public long DirsDone;
        public long DirsFound = 1;
        public int Skipped;
        public int Pending = 1;
    }

    /// <summary>
    /// Walks <paramref name="rootPath"/>, whose entry id in <paramref name="index"/> is <paramref name="rootEntry"/>,
    /// adding all descendants. Returns final counts (Fraction = 1).
    /// </summary>
    public async Task<ScanProgress> WalkAsync(VolumeIndex index, string rootPath, int rootEntry, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = new WalkState();
        var channel = Channel.CreateUnbounded<(string Path, int Entry)>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        channel.Writer.TryWrite((rootPath, rootEntry));

        var workers = Enumerable.Range(0, _workers).Select(_ => Task.Run(() => WorkerAsync(index, channel, state, ct), ct)).ToArray();
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reporter = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(timerCts.Token).ConfigureAwait(false))
                    progress.Report(Snapshot(state, final: false));
            }
            catch (OperationCanceledException) { }
        });

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            timerCts.Cancel();
            await reporter.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        var final = Snapshot(state, final: true);
        progress?.Report(final);
        return final;
    }

    private static ScanProgress Snapshot(WalkState s, bool final) =>
        new(Interlocked.Read(ref s.Entries), Interlocked.Read(ref s.DirsDone), Volatile.Read(ref s.Skipped),
            final ? 1.0 : Math.Min(0.99, (double)Interlocked.Read(ref s.DirsDone) / Math.Max(1, Interlocked.Read(ref s.DirsFound))));

    private async Task WorkerAsync(VolumeIndex index, Channel<(string Path, int Entry)> channel, WalkState state, CancellationToken ct)
    {
        var children = new List<RawEntry>(256);
        var subdirs = new List<(string, int)>(64);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        try
        {
            await foreach (var (dirPath, dirEntry) in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                children.Clear();
                subdirs.Clear();
                try
                {
                    var enumerable = new FileSystemEnumerable<RawEntry>(dirPath,
                        (ref FileSystemEntry e) => new RawEntry(e.FileName.ToString(), e.Attributes, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcTicks),
                        options);
                    foreach (var item in enumerable) children.Add(item);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    Interlocked.Increment(ref state.Skipped);
                }

                if (children.Count > 0)
                {
                    int added = 0;
                    index.Lock.EnterWriteLock();
                    try
                    {
                        foreach (var c in children)
                        {
                            bool isDir = (c.Attributes & FileAttributes.Directory) != 0;
                            string? childPath = null;
                            if (isDir)
                            {
                                if (s_skipNames.Contains(c.Name)) continue;
                                childPath = Join(dirPath, c.Name);
                                if (_excluded.Count > 0 && _excluded.Contains(childPath)) continue;
                            }
                            var flags = EntryFlagsExtensions.FromAttributes((uint)c.Attributes) | EntryFlags.MetadataKnown;
                            int id = index.Add(dirEntry, c.Name, flags, c.Length, c.ModifiedTicks);
                            added++;
                            if (isDir && (c.Attributes & FileAttributes.ReparsePoint) == 0) subdirs.Add((childPath!, id));
                        }
                    }
                    finally { index.Lock.ExitWriteLock(); }
                    Interlocked.Add(ref state.Entries, added);
                }

                if (subdirs.Count > 0)
                {
                    Interlocked.Add(ref state.Pending, subdirs.Count);
                    Interlocked.Add(ref state.DirsFound, subdirs.Count);
                    foreach (var s in subdirs) channel.Writer.TryWrite(s);
                }
                Interlocked.Increment(ref state.DirsDone);
                if (Interlocked.Decrement(ref state.Pending) == 0) channel.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            channel.Writer.TryComplete();
            throw;
        }
    }

    private static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');
}
