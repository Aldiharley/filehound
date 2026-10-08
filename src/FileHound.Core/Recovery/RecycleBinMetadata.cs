using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace FileHound.Core.Recovery;

/// <summary>
/// The metadata Windows keeps for a recycled item in <c>$Recycle.Bin\&lt;SID&gt;\$I&lt;id&gt;</c>.
/// Version 2 (Windows 10+): u64 version, u64 size, FILETIME deleted, u32 path length in UTF-16 units (incl. NUL), path.
/// Version 1 (Vista–8.1): u64 version, u64 size, FILETIME deleted, fixed 520-byte NUL-padded UTF-16 path (544 bytes total).
/// </summary>
public sealed record RecycleBinMetadata(int Version, long Size, DateTime DeletedUtc, string OriginalPath)
{
    private const int V1Length = 544;
    private const int V2HeaderLength = 28;
    private const int MaxPathChars = 32768;

    public static bool TryParse(ReadOnlySpan<byte> bytes, out RecycleBinMetadata? meta)
    {
        meta = null;
        if (bytes.Length < 24) return false;
        long version = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        long size = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
        if (size < 0 || fileTime <= 0 || fileTime > DateTime.MaxValue.ToFileTimeUtc()) return false;
        var deleted = DateTime.FromFileTimeUtc(fileTime);

        string path;
        switch (version)
        {
            case 1:
                if (bytes.Length < V1Length) return false;
                path = ReadUtf16(bytes.Slice(24, 520));
                break;
            case 2:
            {
                if (bytes.Length < V2HeaderLength) return false;
                uint chars = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
                if (chars <= 1 || chars > MaxPathChars || V2HeaderLength + chars * 2 > (uint)bytes.Length) return false;
                path = ReadUtf16(bytes.Slice(V2HeaderLength, (int)chars * 2));
                break;
            }
            default:
                return false;
        }
        // A relative path here would make Restore move the file relative to the working directory.
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return false;
        meta = new RecycleBinMetadata((int)version, size, deleted, path);
        return true;
    }

    private static string ReadUtf16(ReadOnlySpan<byte> utf16)
    {
        var chars = MemoryMarshal.Cast<byte, char>(utf16);
        int nul = chars.IndexOf('\0');
        return new string(nul < 0 ? chars : chars[..nul]);
    }
}
