using FileHound.Core.Recovery;

namespace FileHound.Core.Tests;

public class Lznt1Tests
{
    private static byte[] Chunk(bool compressed, byte[] body) =>
        [.. BitConverter.GetBytes((ushort)((compressed ? 0xB000 : 0x3000) | (body.Length - 1))), .. body];

    [Fact]
    public void Uncompressed_chunk_is_copied()
    {
        var body = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        var output = new byte[4096];
        Assert.Equal(4096, Lznt1.Decompress(Chunk(false, body), output));
        Assert.Equal(body, output);
    }

    [Fact]
    public void Back_reference_at_offset_three()
    {
        // "ABC" then copy 6 bytes from 3 back → "ABCABCABC". At produced=3, lg=0: token = (offset-1)<<12 | (len-3).
        byte[] body = [0b0000_1000, (byte)'A', (byte)'B', (byte)'C', 0x03, 0x20];
        var output = new byte[16];
        Assert.Equal(9, Lznt1.Decompress(Chunk(true, body), output));
        Assert.Equal("ABCABCABC", System.Text.Encoding.ASCII.GetString(output, 0, 9));
    }

    [Fact]
    public void Displacement_width_shrinks_after_sixteen_bytes()
    {
        // 17 literals then copy 5 bytes from 17 back. At produced=17, i=16 → lg=1: len = (t & 0x7FF)+3, off = (t>>11)+1.
        var lit = System.Text.Encoding.ASCII.GetBytes("0123456789ABCDEFG");
        var body = new List<byte> { 0x00 };
        body.AddRange(lit[..8]);
        body.Add(0x00);
        body.AddRange(lit[8..16]);
        body.Add(0b0000_0010);
        body.Add(lit[16]);
        ushort token = (ushort)((16 << 11) | 2);
        body.Add((byte)token);
        body.Add((byte)(token >> 8));
        var output = new byte[32];
        Assert.Equal(22, Lznt1.Decompress(Chunk(true, body.ToArray()), output));
        Assert.Equal("0123456789ABCDEFG01234", System.Text.Encoding.ASCII.GetString(output, 0, 22));
    }

    [Fact]
    public void Zero_header_ends_the_stream()
    {
        byte[] input = [.. Chunk(true, [0x00, (byte)'x']), 0x00, 0x00, 0xFF];
        var output = new byte[8192];
        Assert.Equal(1, Lznt1.Decompress(input, output));
    }

    [Fact]
    public void Reference_before_chunk_start_is_rejected()
    {
        byte[] body = [0b0000_0010, (byte)'A', 0x00, 0x50]; // offset 6 at produced=1
        Assert.Throws<InvalidDataException>(() => Lznt1.Decompress(Chunk(true, body), new byte[16]));
    }

    [Fact]
    public void Two_chunks_fill_eight_kilobytes()
    {
        var a = new byte[4096];
        Array.Fill(a, (byte)1);
        var b = new byte[4096];
        Array.Fill(b, (byte)2);
        byte[] input = [.. Chunk(false, a), .. Chunk(false, b)];
        var output = new byte[8192];
        Assert.Equal(8192, Lznt1.Decompress(input, output));
        Assert.Equal(2, output[8191]);
    }

    [Fact]
    public void Output_is_clamped_to_the_buffer()
    {
        var body = new byte[4096];
        Array.Fill(body, (byte)5);
        var output = new byte[100];
        Assert.Equal(100, Lznt1.Decompress(Chunk(false, body), output));
    }

    [Fact]
    public void Truncated_token_is_rejected()
    {
        byte[] body = [0b0000_0010, (byte)'A', 0x03]; // the pair's second byte is missing
        Assert.Throws<InvalidDataException>(() => Lznt1.Decompress(Chunk(true, body), new byte[16]));
    }
}
