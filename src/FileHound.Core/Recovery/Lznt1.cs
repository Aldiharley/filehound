using System.Buffers.Binary;

namespace FileHound.Core.Recovery;

/// <summary>
/// Decompressor for LZNT1, the format NTFS uses for compressed files (MS-XCA §2.3). A compression unit is a sequence
/// of chunks, each up to 4 KB of output; every chunk starts with a 16-bit header (bits 0–11: compressed size − 1,
/// bits 12–14: signature 3, bit 15: compressed). A zero header ends the unit. Written from the public format
/// description and DiscUtils' MIT implementation; no GPL code.
/// </summary>
public static class Lznt1
{
    public const int ChunkSize = 4096;

    /// <summary>
    /// Decompresses one compression unit into <paramref name="output"/> and returns the number of bytes written
    /// (at most <c>output.Length</c>). Stops at a zero chunk header or at the end of the input; bytes the unit does
    /// not cover are left untouched, so callers zero-fill first.
    /// </summary>
    /// <exception cref="InvalidDataException">A back-reference points before the chunk start, or a token is truncated.</exception>
    public static int Decompress(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        int inPos = 0, outPos = 0;
        while (inPos + 2 <= compressed.Length && outPos < output.Length)
        {
            ushort header = BinaryPrimitives.ReadUInt16LittleEndian(compressed[inPos..]);
            if (header == 0) break;
            inPos += 2;
            int chunkLength = (header & 0x0FFF) + 1;
            if (chunkLength > compressed.Length - inPos) chunkLength = compressed.Length - inPos;
            var chunk = compressed.Slice(inPos, chunkLength);
            inPos += chunkLength;

            int chunkStart = outPos;
            int limit = Math.Min(output.Length, chunkStart + ChunkSize);
            if ((header & 0x8000) == 0)
            {
                int n = Math.Min(chunk.Length, limit - outPos);
                chunk[..n].CopyTo(output[outPos..]);
                outPos += n;
                continue;
            }

            int p = 0;
            while (p < chunk.Length && outPos < limit)
            {
                byte flags = chunk[p++];
                for (int bit = 0; bit < 8 && p < chunk.Length && outPos < limit; bit++)
                {
                    if ((flags & (1 << bit)) == 0)
                    {
                        output[outPos++] = chunk[p++];
                        continue;
                    }
                    if (p + 2 > chunk.Length) throw new InvalidDataException("LZNT1: truncated back-reference token.");
                    ushort token = BinaryPrimitives.ReadUInt16LittleEndian(chunk[p..]);
                    p += 2;
                    // The split between displacement and length bits depends on how far into the chunk we are.
                    int lg = 0;
                    for (int i = outPos - chunkStart - 1; i >= 0x10; i >>= 1) lg++;
                    int length = (token & (0xFFF >> lg)) + 3;
                    int offset = (token >> (12 - lg)) + 1;
                    if (offset > outPos - chunkStart) throw new InvalidDataException("LZNT1: back-reference before the chunk start.");
                    for (int k = 0; k < length && outPos < limit; k++, outPos++) output[outPos] = output[outPos - offset];
                }
            }
        }
        return outPos;
    }
}
