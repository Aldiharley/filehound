using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileHound.Indexing.Interop;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Ntfs;

/// <summary>
/// Turbo indexer that reads the raw <c>$MFT</c> of an NTFS volume in large sequential blocks and parses every FILE
/// record itself, producing names, parents, attributes, sizes and modified times in a single pass (no separate
/// metadata fill). Requires an elevated volume handle. Throws <see cref="NotSupportedException"/> for layouts it
/// does not handle so the caller can fall back to <c>FSCTL_ENUM_USN_DATA</c>.
/// </summary>
public sealed class RawMftReader
{
    private const long RootRecord = 5;
    private const int FirstUserRecord = 16;
    private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    private const int ChunkBytes = 4 << 20;
    private const int BufferCount = 4;

    public TimeSpan IoTime { get; private set; }
    public TimeSpan ParseTime { get; private set; }
    public TimeSpan BuildTime { get; private set; }
    public long BytesRead { get; private set; }

    private readonly record struct VolumeData(int BytesPerSector, int BytesPerCluster, int RecordSize, long MftValidDataLength, long MftStartLcn);

    public unsafe VolumeIndex Read(SafeFileHandle volume, DriveDescriptor drive, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var vd = QueryVolumeData(volume);
        var extents = ReadMftExtents(volume, vd);
        long total = vd.MftValidDataLength;
        var acc = new Accumulator(drive.Root, vd.RecordSize, (int)Math.Clamp(total / vd.RecordSize, 1 << 16, 1 << 26));

        int chunk = ChunkBytes / vd.BytesPerCluster * vd.BytesPerCluster;
        var buffers = new nint[BufferCount];
        for (int i = 0; i < BufferCount; i++) buffers[i] = (nint)NativeMemory.AlignedAlloc((nuint)chunk, 4096);
        using var free = new BlockingCollection<int>(BufferCount);
        using var filled = new BlockingCollection<(int Buffer, int Length, long FirstRecord)>(BufferCount);
        for (int i = 0; i < BufferCount; i++) free.Add(i);
        long ioTicks = 0, parseTicks = 0, bytes = 0;

        // Reader: walks the extents in VCN order and fills buffers; the caller's thread parses them meanwhile.
        var reader = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var ext in extents)
                {
                    long extStart = ext.Vcn * vd.BytesPerCluster;
                    if (extStart >= total) break;
                    if (ext.Lcn < 0) continue; // sparse: no records stored
                    long extBytes = Math.Min(ext.Clusters * vd.BytesPerCluster, total - extStart);
                    for (long done = 0; done < extBytes; done += chunk)
                    {
                        ct.ThrowIfCancellationRequested();
                        int len = (int)Math.Min(chunk, extBytes - done);
                        len -= len % vd.RecordSize;
                        if (len <= 0) break;
                        int readLen = (len + vd.BytesPerSector - 1) / vd.BytesPerSector * vd.BytesPerSector;
                        int b = free.Take(ct);
                        long t0 = Stopwatch.GetTimestamp();
                        Kernel32.ReadExactly(volume, ext.Lcn * vd.BytesPerCluster + done, (byte*)buffers[b], readLen);
                        ioTicks += Stopwatch.GetTimestamp() - t0;
                        filled.Add((b, len, (extStart + done) / vd.RecordSize), ct);
                    }
                }
            }
            finally { filled.CompleteAdding(); }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            long lastReport = Environment.TickCount64;
            foreach (var (b, len, first) in filled.GetConsumingEnumerable(ct))
            {
                long t0 = Stopwatch.GetTimestamp();
                acc.AddChunk(new Span<byte>((byte*)buffers[b], len), first);
                parseTicks += Stopwatch.GetTimestamp() - t0;
                bytes += len;
                free.Add(b, ct);
                if (progress is not null && Environment.TickCount64 - lastReport > 200)
                {
                    lastReport = Environment.TickCount64;
                    progress.Report(new ScanProgress(acc.Count, 0, 0, Math.Min(0.99, (double)bytes / total)));
                }
            }
            reader.GetAwaiter().GetResult(); // surface read errors
        }
        finally
        {
            if (!reader.IsCompleted)
            {
                filled.CompleteAdding();
                try { reader.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            }
            if (reader.IsCompleted) foreach (var p in buffers) NativeMemory.AlignedFree((void*)p);
        }

        IoTime = Stopwatch.GetElapsedTime(0, ioTicks);
        ParseTime = Stopwatch.GetElapsedTime(0, parseTicks);
        BytesRead = bytes;
        long tb = Stopwatch.GetTimestamp();
        var v = acc.Build();
        BuildTime = Stopwatch.GetElapsedTime(tb);
        return v;
    }

    private static unsafe VolumeData QueryVolumeData(SafeFileHandle volume)
    {
        byte* data = stackalloc byte[128];
        if (!Kernel32.DeviceIoControl(volume, FSCTL_GET_NTFS_VOLUME_DATA, null, 0, data, 128, out _, 0))
            throw new NotSupportedException($"FSCTL_GET_NTFS_VOLUME_DATA failed ({Marshal.GetLastPInvokeError()})");
        var vd = new VolumeData(
            BytesPerSector: (int)*(uint*)(data + 40),
            BytesPerCluster: (int)*(uint*)(data + 44),
            RecordSize: (int)*(uint*)(data + 48),
            MftValidDataLength: *(long*)(data + 56),
            MftStartLcn: *(long*)(data + 64));
        if (vd.RecordSize < 512 || (vd.RecordSize & (vd.RecordSize - 1)) != 0 || vd.RecordSize > vd.BytesPerCluster
            || vd.BytesPerSector < 512 || vd.BytesPerCluster % vd.BytesPerSector != 0 || vd.MftValidDataLength <= 0)
            throw new NotSupportedException($"Unsupported NTFS geometry (record {vd.RecordSize}, cluster {vd.BytesPerCluster}, sector {vd.BytesPerSector})");
        return vd;
    }

    /// <summary>Reads MFT record 0 ($MFT itself) and decodes the extents of its unnamed $DATA.</summary>
    private static unsafe List<DataRun> ReadMftExtents(SafeFileHandle volume, VolumeData vd)
    {
        int readLen = (vd.RecordSize + vd.BytesPerSector - 1) / vd.BytesPerSector * vd.BytesPerSector;
        byte* p = (byte*)NativeMemory.AlignedAlloc((nuint)readLen, 4096);
        byte[] record;
        try
        {
            Kernel32.ReadExactly(volume, vd.MftStartLcn * vd.BytesPerCluster, p, readLen);
            record = new ReadOnlySpan<byte>(p, vd.RecordSize).ToArray();
        }
        finally { NativeMemory.AlignedFree(p); }

        if (!MftRecordParser.ApplyFixups(record) || !MftRecordParser.TryParse(record, out var r) || !r.InUse)
            throw new NotSupportedException("MFT record 0 is unreadable");
        if (r.HasAttributeList)
            throw new NotSupportedException("$MFT has an attribute list (heavily fragmented MFT)");
        if (r.UnnamedDataRuns.IsEmpty)
            throw new NotSupportedException("$MFT data runs not found in record 0");
        var extents = DataRuns.Decode(r.UnnamedDataRuns);
        long covered = extents.Sum(e => e.Clusters) * vd.BytesPerCluster;
        if (covered < vd.MftValidDataLength)
            throw new NotSupportedException("$MFT data runs in record 0 do not cover the whole MFT");
        return extents;
    }

    /// <summary>Turns record-aligned chunks of the $MFT stream into a <see cref="VolumeIndex"/>.</summary>
    internal sealed class Accumulator
    {
        private sealed class Extension
        {
            public string? Name;
            public long Parent;
            public long? Size;
        }

        private readonly record struct Deferred(string? Name, long Parent, EntryFlags Flags, long ModifiedTicks, long? Size);

        private readonly VolumeIndexBuilder _builder;
        private readonly int _recordSize;
        private readonly Dictionary<long, Extension> _extensions = [];
        private readonly Dictionary<long, Deferred> _deferred = [];

        public Accumulator(string root, int recordSize, int capacityHint)
        {
            _builder = new VolumeIndexBuilder(root, IndexMode.Turbo, capacityHint);
            _recordSize = recordSize;
        }

        public int Count => _builder.Count;

        public void AddChunk(Span<byte> chunk, long firstRecordNo)
        {
            if (chunk.Length % _recordSize != 0) throw new ArgumentException("Chunk is not record-aligned.", nameof(chunk));
            int n = chunk.Length / _recordSize;
            for (int i = 0; i < n; i++)
            {
                var rec = chunk.Slice(i * _recordSize, _recordSize);
                if (!MftRecordParser.ApplyFixups(rec) || !MftRecordParser.TryParse(rec, out var r) || !r.InUse) continue;
                long recNo = firstRecordNo + i;

                if (r.BaseRecord != 0)
                {
                    if (!r.HasName && !r.HasSize) continue;
                    if (!_extensions.TryGetValue(r.BaseRecord, out var ext)) _extensions[r.BaseRecord] = ext = new Extension();
                    if (r.HasName && ext.Name is null) { ext.Name = r.Name.ToString(); ext.Parent = r.ParentRecord; }
                    if (r.HasSize && ext.Size is null) ext.Size = r.Size;
                    continue;
                }
                if (recNo < FirstUserRecord) continue; // root and NTFS metafiles

                var flags = EntryFlagsExtensions.FromAttributes(r.Attributes) & ~EntryFlags.Directory;
                if (r.IsDirectory) flags |= EntryFlags.Directory;
                bool complete = r.HasName && (r.IsDirectory || r.HasSize || !r.HasAttributeList);
                if (complete)
                {
                    if (r.ParentRecord == RootRecord && MftScanner.IsSkippedRootName(r.Name)) continue;
                    _builder.AddRecord(recNo, r.ParentRecord, r.Name, flags | EntryFlags.MetadataKnown, r.IsDirectory ? 0 : r.Size, r.ModifiedUtcTicks);
                }
                else
                {
                    _deferred[recNo] = new Deferred(r.HasName ? r.Name.ToString() : null, r.ParentRecord, flags, r.ModifiedUtcTicks, r.HasSize ? r.Size : null);
                }
            }
        }

        public VolumeIndex Build()
        {
            foreach (var (recNo, d) in _deferred)
            {
                _extensions.TryGetValue(recNo, out var ext);
                string? name = d.Name ?? ext?.Name;
                if (name is null) continue;
                long parent = d.Name is not null ? d.Parent : ext!.Parent;
                if (parent == RootRecord && MftScanner.IsSkippedRootName(name)) continue;
                bool isDir = (d.Flags & EntryFlags.Directory) != 0;
                long size = isDir ? 0 : d.Size ?? ext?.Size ?? 0;
                _builder.AddRecord(recNo, parent, name, d.Flags | EntryFlags.MetadataKnown, size, d.ModifiedTicks);
            }
            return _builder.Build(RootRecord);
        }
    }
}
