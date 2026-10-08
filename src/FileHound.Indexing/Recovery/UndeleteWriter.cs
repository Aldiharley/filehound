using System.Security.Cryptography;
using FileHound.Core.Recovery;
using FileHound.Indexing.Ntfs;

namespace FileHound.Indexing.Recovery;

/// <summary>
/// FR-15: copies one deleted record's data to a destination file — resident bytes, or the data runs read through the
/// <see cref="VolumeReader"/> with sparse runs zero-filled and LZNT1 units decompressed — truncated to the real size,
/// hashed on the way, with the original timestamps. The bitmap is re-checked first; clusters reused since the scan
/// downgrade the grade but the copy still proceeds (partial data beats none).
/// </summary>
public static class UndeleteWriter
{
    private const int MaxClustersPerRead = 1024;

    public static (long Bytes, string Sha256, RecoveryGrade FinalGrade, IReadOnlyList<ByteRun> Runs) Recover(
        VolumeReader reader, ClusterBitmap bitmap, UndeleteRecord record, RecoveryGrade gradeAtScan, string destPath, CancellationToken ct)
    {
        if (record.IsEncrypted) throw new NotSupportedException("EFS-encrypted files cannot be recovered.");
        if (record.IsDirectory) throw new NotSupportedException("Directories are recovered through their children.");
        // Validate before anything touches the destination, so a refused record leaves no empty file behind.
        IReadOnlyList<DataRun> runList = [];
        if (!record.DataIsResident)
        {
            if (!record.TryGetRuns(reader.Geometry.TotalClusters, out runList)) throw new InvalidDataException("The record's data runs are damaged.");
            if (!record.HasSupportedCompressionUnit) throw new InvalidDataException("Unsupported compression unit.");
        }
        return Write(reader, bitmap, record, runList, gradeAtScan, destPath, ct);
    }

    private static (long Bytes, string Sha256, RecoveryGrade FinalGrade, IReadOnlyList<ByteRun> Runs) Write(
        VolumeReader reader, ClusterBitmap bitmap, UndeleteRecord record, IReadOnlyList<DataRun> runList, RecoveryGrade gradeAtScan, string destPath, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // CreateNew: an existing file is never touched. Only a file this call created is removed on failure.
        var output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        try { return Fill(reader, bitmap, record, runList, gradeAtScan, destPath, output, hash, ct); }
        catch
        {
            // A half-written file would be mistaken for a recovery; the error in the results list says what happened instead.
            output.Dispose();
            try { File.Delete(destPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static (long Bytes, string Sha256, RecoveryGrade FinalGrade, IReadOnlyList<ByteRun> Runs) Fill(
        VolumeReader reader, ClusterBitmap bitmap, UndeleteRecord record, IReadOnlyList<DataRun> runList, RecoveryGrade gradeAtScan, string destPath,
        FileStream output, IncrementalHash hash, CancellationToken ct)
    {
        var runs = new List<ByteRun>();
        long written = 0;
        var grade = gradeAtScan;

        if (record.DataIsResident)
        {
            var data = (record.ResidentData ?? []).AsSpan();
            data = data[..(int)Math.Min(data.Length, record.RealSize)];
            output.Write(data);
            hash.AppendData(data);
            written = data.Length;
        }
        else
        {
            long cluster = reader.Geometry.BytesPerCluster;
            // Sizes come from disk: never trust them past what the runs can hold.
            long capacity = UndeleteRecord.Capacity(runList, cluster);
            long realSize = Math.Min(record.RealSize, capacity);
            long initialized = record.InitializedSize > 0 ? Math.Min(record.InitializedSize, realSize) : realSize;
            long needed = record.IsCompressed ? runList.Sum(r => r.Clusters) : (initialized + cluster - 1) / cluster;
            var (allocatedNow, total) = bitmap.Count(runList, needed);
            if (total > 0 && allocatedNow > 0)
            {
                var worse = allocatedNow == total ? RecoveryGrade.Overwritten : RecoveryGrade.Partial;
                if (Rank(worse) > Rank(grade)) grade = worse;
            }
            if (record.IsCompressed) written = WriteCompressed(reader, runList, record.ClustersPerUnit, initialized, output, hash, runs, ct);
            else written = WritePlain(reader, runList, initialized, output, hash, runs, ct);
            // Bytes past the initialized size read as zeros on a live volume; reproduce that up to the real size.
            if (written < realSize) WriteZeros(output, hash, realSize - written);
            written = realSize;
        }
        output.Flush();
        output.Dispose();
        try
        {
            if (record.CreatedUtcTicks > 0) File.SetCreationTimeUtc(destPath, new DateTime(record.CreatedUtcTicks, DateTimeKind.Utc));
            if (record.ModifiedUtcTicks > 0) File.SetLastWriteTimeUtc(destPath, new DateTime(record.ModifiedUtcTicks, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
        return (written, Convert.ToHexStringLower(hash.GetHashAndReset()), grade, runs);
    }

    private static int Rank(RecoveryGrade g) => g switch
    {
        RecoveryGrade.Excellent => 0, RecoveryGrade.Good => 1, RecoveryGrade.Partial => 2, RecoveryGrade.Zeroed => 3, RecoveryGrade.Overwritten => 4, _ => 5,
    };

    private static long WritePlain(VolumeReader reader, IReadOnlyList<DataRun> dataRuns, long limit, Stream output, IncrementalHash hash, List<ByteRun> runs, CancellationToken ct)
    {
        long cluster = reader.Geometry.BytesPerCluster;
        long written = 0;
        var buffer = new byte[Math.Min(MaxClustersPerRead, 1024) * cluster];
        foreach (var run in dataRuns)
        {
            if (written >= limit) break;
            long runBytes = Math.Min(run.Clusters * cluster, limit - written);
            if (run.Lcn < 0)
            {
                WriteZeros(output, hash, runBytes);
                written += runBytes;
                continue;
            }
            runs.Add(new ByteRun(written, runBytes, run.Lcn * cluster));
            long done = 0;
            while (done < runBytes)
            {
                ct.ThrowIfCancellationRequested();
                int clusters = (int)Math.Min(MaxClustersPerRead, (runBytes - done + cluster - 1) / cluster);
                reader.ReadClusters(run.Lcn + done / cluster, clusters, buffer);
                int take = (int)Math.Min(clusters * cluster, runBytes - done);
                output.Write(buffer, 0, take);
                hash.AppendData(buffer, 0, take);
                done += take;
            }
            written += runBytes;
        }
        return written;
    }

    /// <summary>
    /// Compressed files are stored in units of <see cref="UndeleteRecord.ClustersPerUnit"/> clusters: a unit whose
    /// runs end in a sparse tail holds an LZNT1 stream in its allocated clusters, a fully allocated unit is stored raw,
    /// and an entirely sparse unit is zeros.
    /// </summary>
    private static long WriteCompressed(VolumeReader reader, IReadOnlyList<DataRun> dataRuns, int unitClusters, long limit, Stream output, IncrementalHash hash, List<ByteRun> runs, CancellationToken ct)
    {
        long cluster = reader.Geometry.BytesPerCluster;
        long unitBytes = unitClusters * cluster;
        long totalVcns = dataRuns.Sum(r => r.Clusters);
        var packed = new byte[unitBytes];
        var unpacked = new byte[unitBytes];
        long written = 0;
        for (long unitVcn = 0; unitVcn < totalVcns && written < limit; unitVcn += unitClusters)
        {
            ct.ThrowIfCancellationRequested();
            // Gather this unit's clusters in VCN order.
            int stored = 0;
            bool anySparse = false;
            for (long v = unitVcn; v < unitVcn + unitClusters && v < totalVcns; v++)
            {
                long lcn = LcnOf(dataRuns, v);
                if (lcn < 0) { anySparse = true; continue; }
                if (anySparse) throw new InvalidDataException("Compression unit has data after its sparse tail.");
                reader.ReadClusters(lcn, 1, packed.AsSpan(stored * (int)cluster));
                stored++;
            }
            long take = Math.Min(unitBytes, limit - written);
            if (stored == 0)
            {
                WriteZeros(output, hash, take);
            }
            else if (!anySparse && stored == unitClusters)
            {
                output.Write(packed, 0, (int)take);
                hash.AppendData(packed, 0, (int)take);
                runs.Add(new ByteRun(written, take, LcnOf(dataRuns, unitVcn) * cluster));
            }
            else
            {
                Array.Clear(unpacked);
                Lznt1.Decompress(packed.AsSpan(0, stored * (int)cluster), unpacked);
                output.Write(unpacked, 0, (int)take);
                hash.AppendData(unpacked, 0, (int)take);
                runs.Add(new ByteRun(written, take, LcnOf(dataRuns, unitVcn) * cluster));
            }
            written += take;
        }
        return written;
    }

    private static long LcnOf(IReadOnlyList<DataRun> runs, long vcn)
    {
        foreach (var r in runs)
            if (vcn >= r.Vcn && vcn < r.Vcn + r.Clusters) return r.Lcn < 0 ? -1 : r.Lcn + (vcn - r.Vcn);
        return -1;
    }

    private static void WriteZeros(Stream output, IncrementalHash hash, long count)
    {
        var zeros = new byte[(int)Math.Min(count, 1 << 16)];
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            output.Write(zeros, 0, n);
            hash.AppendData(zeros, 0, n);
            count -= n;
        }
    }
}
