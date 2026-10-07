using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;

namespace FileHound.Indexing.Ntfs;

/// <summary>
/// Turbo indexer for systems where raw volume reads are refused (e.g. by security software, Win32 error 50):
/// fetches every in-use FILE record through <c>FSCTL_GET_NTFS_FILE_RECORD</c>, a file-system control code, and
/// parses it with the same <see cref="RawMftReader.Accumulator"/> — names, sizes and dates in one pass.
/// </summary>
/// <remarks>
/// The control code returns the highest in-use record at or below the requested number, so walking each range
/// downward costs one call per in-use record and skips free ranges for free. Ranges run in parallel, each worker
/// with its own volume handle (requests on one synchronous handle are serialized by the I/O manager).
/// </remarks>
public sealed class FileRecordMftReader
{
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const uint FSCTL_GET_NTFS_FILE_RECORD = 0x00090068;
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private const int BatchRecords = 64;

    public int Threads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    public TimeSpan ParseTime { get; private set; }
    public TimeSpan BuildTime { get; private set; }
    public long RecordsFetched { get; private set; }

    /// <summary>Splits [0, total) into <paramref name="parts"/> contiguous ranges.</summary>
    internal static IEnumerable<(long Lo, long Hi)> Partition(long total, int parts)
    {
        long size = (total + parts - 1) / parts;
        for (long lo = 0; lo < total; lo += size) yield return (lo, Math.Min(total, lo + size));
    }

    /// <summary>
    /// Visits every in-use record in [lo, hi) from the top down. <paramref name="fetch"/> returns the highest in-use
    /// record number &lt;= n (or -1); records below <paramref name="lo"/> belong to another range and stop the walk.
    /// </summary>
    internal static void EnumerateDownward(long lo, long hi, Func<long, long> fetch, Action<long> onRecord)
    {
        long n = hi - 1;
        while (n >= lo)
        {
            long m = fetch(n);
            if (m < lo || m > n) return;
            onRecord(m);
            n = m - 1;
        }
    }

    public unsafe VolumeIndex Read(DriveDescriptor drive, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        int recordSize;
        long totalRecords;
        using (var h = Kernel32.OpenVolume(drive.Letter))
        {
            if (h.IsInvalid) throw new IOException($"Cannot open volume {drive.Letter}: ({Marshal.GetLastPInvokeError()})");
            byte* vd = stackalloc byte[128];
            if (!Kernel32.DeviceIoControl(h, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, vd, 128, out _, 0))
                throw new NotSupportedException($"FSCTL_GET_NTFS_VOLUME_DATA failed ({Marshal.GetLastPInvokeError()})");
            recordSize = (int)*(uint*)(vd + 48);
            totalRecords = *(long*)(vd + 56) / Math.Max(1, recordSize);
            if (recordSize < 512 || recordSize > 65536) throw new NotSupportedException($"Unsupported record size {recordSize}");
            // Probe once so an unsupported control code falls back cleanly before any work starts.
            long probe = FetchOne(h, totalRecords - 1, recordSize, null);
            if (probe < 0) throw new NotSupportedException($"FSCTL_GET_NTFS_FILE_RECORD failed ({Marshal.GetLastPInvokeError()})");
        }

        var acc = new RawMftReader.Accumulator(drive.Root, recordSize, (int)Math.Clamp(totalRecords, 1 << 16, 1 << 26));
        using var batches = new BlockingCollection<(byte[] Data, long[] RecordNos, int Count)>(Threads * 8);
        var ranges = new ConcurrentQueue<(long, long)>(Partition(totalRecords, Threads * 16));
        long fetched = 0, highestDone = 0;
        Exception? failure = null;

        var workers = Enumerable.Range(0, Threads).Select(_ => Task.Factory.StartNew(() =>
        {
            try
            {
                using var h = Kernel32.OpenVolume(drive.Letter);
                if (h.IsInvalid) throw new IOException($"Cannot open volume {drive.Letter}: ({Marshal.GetLastPInvokeError()})");
                var data = ArrayPool<byte>.Shared.Rent(BatchRecords * recordSize);
                var nos = ArrayPool<long>.Shared.Rent(BatchRecords);
                int count = 0;
                while (ranges.TryDequeue(out var range))
                {
                    var (lo, hi) = range;
                    EnumerateDownward(lo, hi, n =>
                    {
                        ct.ThrowIfCancellationRequested();
                        long m = FetchOne(h, n, recordSize, data.AsSpan(count * recordSize, recordSize));
                        // Record 0 ($MFT) is always in use, so "nothing at or below n" never happens: -1 is a failure.
                        if (m < 0) throw new IOException($"FSCTL_GET_NTFS_FILE_RECORD({n}) failed: Win32 error {Marshal.GetLastPInvokeError()}");
                        return m;
                    }, m =>
                    {
                        nos[count++] = m;
                        if (count == BatchRecords)
                        {
                            batches.Add((data, nos, count), ct);
                            data = ArrayPool<byte>.Shared.Rent(BatchRecords * recordSize);
                            nos = ArrayPool<long>.Shared.Rent(BatchRecords);
                            count = 0;
                        }
                    });
                    Interlocked.Add(ref highestDone, hi - lo);
                }
                if (count > 0) batches.Add((data, nos, count), ct);
                else { ArrayPool<byte>.Shared.Return(data); ArrayPool<long>.Shared.Return(nos); }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        _ = Task.WhenAll(workers).ContinueWith(_ => batches.CompleteAdding(), TaskScheduler.Default);

        long parseTicks = 0, lastReport = Environment.TickCount64;
        foreach (var (data, nos, count) in batches.GetConsumingEnumerable(ct))
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < count; i++) acc.AddRecord(data.AsSpan(i * recordSize, recordSize), nos[i], fixupsMayBeApplied: true);
            parseTicks += Stopwatch.GetTimestamp() - t0;
            fetched += count;
            ArrayPool<byte>.Shared.Return(data);
            ArrayPool<long>.Shared.Return(nos);
            if (progress is not null && Environment.TickCount64 - lastReport > 200)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new ScanProgress(acc.Count, 0, 0, Math.Min(0.99, (double)Interlocked.Read(ref highestDone) / Math.Max(1, totalRecords))));
            }
        }
        Task.WaitAll(workers);
        if (failure is not null) throw new IOException($"FSCTL_GET_NTFS_FILE_RECORD scan failed: {failure.Message}", failure);

        ParseTime = Stopwatch.GetElapsedTime(0, parseTicks);
        RecordsFetched = fetched;
        long tb = Stopwatch.GetTimestamp();
        var v = acc.Build();
        BuildTime = Stopwatch.GetElapsedTime(tb);
        return v;
    }

    /// <summary>Fetches the highest in-use record &lt;= n into <paramref name="dest"/>; returns its number or -1.</summary>
    private static unsafe long FetchOne(Microsoft.Win32.SafeHandles.SafeFileHandle h, long n, int recordSize, Span<byte> dest)
    {
        int outLen = 12 + recordSize; // NTFS_FILE_RECORD_OUTPUT_BUFFER: FRN (8), length (4), record
        byte* outBuf = stackalloc byte[outLen];
        long input = n;
        if (!Kernel32.DeviceIoControl(h, FSCTL_GET_NTFS_FILE_RECORD, &input, sizeof(long), outBuf, outLen, out int returned, 0) || returned < 12)
            return -1;
        long m = (long)(*(ulong*)outBuf & RecordMask);
        int len = (int)Math.Min(*(uint*)(outBuf + 8), (uint)recordSize);
        if (!dest.IsEmpty)
        {
            new ReadOnlySpan<byte>(outBuf + 12, len).CopyTo(dest);
            if (len < recordSize) dest[len..].Clear();
        }
        return m;
    }
}
