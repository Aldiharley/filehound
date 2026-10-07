namespace FileHound.Indexing.Ntfs;

/// <summary>One extent of a non-resident attribute. <see cref="Lcn"/> is -1 for a sparse run.</summary>
public readonly record struct DataRun(long Vcn, long Lcn, long Clusters);

/// <summary>Decodes an NTFS mapping-pairs array ("data runs").</summary>
public static class DataRuns
{
    public static List<DataRun> Decode(ReadOnlySpan<byte> runs)
    {
        var result = new List<DataRun>();
        long vcn = 0, lcn = 0;
        int p = 0;
        while (p < runs.Length && runs[p] != 0)
        {
            int header = runs[p++];
            int lengthSize = header & 0x0F, offsetSize = header >> 4;
            if (lengthSize is 0 or > 8 || offsetSize > 8 || p + lengthSize + offsetSize > runs.Length)
                throw new InvalidDataException("Malformed data run.");
            long length = (long)ReadUnsigned(runs.Slice(p, lengthSize));
            p += lengthSize;
            if (length <= 0) throw new InvalidDataException("Data run with non-positive length.");
            if (offsetSize == 0)
            {
                result.Add(new DataRun(vcn, -1, length)); // sparse
            }
            else
            {
                lcn += ReadSigned(runs.Slice(p, offsetSize));
                p += offsetSize;
                if (lcn < 0) throw new InvalidDataException("Data run points before the start of the volume.");
                result.Add(new DataRun(vcn, lcn, length));
            }
            vcn += length;
        }
        return result;
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> b)
    {
        ulong v = 0;
        for (int i = b.Length - 1; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    private static long ReadSigned(ReadOnlySpan<byte> b)
    {
        long v = (long)ReadUnsigned(b);
        int bits = b.Length * 8;
        if (bits < 64 && (v & (1L << (bits - 1))) != 0) v |= -1L << bits; // sign-extend
        return v;
    }
}
