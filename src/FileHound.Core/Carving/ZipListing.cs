using System.Buffers.Binary;
using System.Text;

namespace FileHound.Core.Carving;

/// <summary>Entry names from a ZIP's central directory (for previews); tolerant of truncation.</summary>
public static class ZipListing
{
    public static IReadOnlyList<string> Entries(ReadOnlySpan<byte> zip, int max = 200)
    {
        var names = new List<string>();
        int p = zip.LastIndexOf([(byte)'P', (byte)'K', (byte)5, (byte)6]);
        if (p < 0 || p + 22 > zip.Length) return names;
        uint cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(zip[(p + 16)..]);
        if (cdOffset >= (uint)zip.Length) return names;
        int q = (int)cdOffset;
        while (q + 46 <= zip.Length && names.Count < max && BinaryPrimitives.ReadUInt32LittleEndian(zip[q..]) == 0x02014B50)
        {
            int n = BinaryPrimitives.ReadUInt16LittleEndian(zip[(q + 28)..]), e = BinaryPrimitives.ReadUInt16LittleEndian(zip[(q + 30)..]), c = BinaryPrimitives.ReadUInt16LittleEndian(zip[(q + 32)..]);
            if (q + 46 + n > zip.Length) break;
            names.Add(Encoding.UTF8.GetString(zip.Slice(q + 46, n)));
            q += 46 + n + e + c;
        }
        return names;
    }
}
