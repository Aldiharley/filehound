using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FileHound.Core.Carving;

namespace FileHound.Core.Tests.Carving;

public class DocumentValidatorTests
{
    // ---- builders -------------------------------------------------------------------------------------------------
    internal static byte[] Pdf() => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n2 0 obj << /Type /Pages /Count 3 >> endobj\nxref\ntrailer\n%%EOF\n3 0 obj << >> endobj\n%%EOF\r\n");

    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());   // stored, like a real EPUB mimetype
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] SevenZip(int payload)
    {
        var b = new byte[32 + payload + 20];
        b[0] = 0x37; b[1] = 0x7A; b[2] = 0xBC; b[3] = 0xAF; b[4] = 0x27; b[5] = 0x1C; b[6] = 0; b[7] = 4;
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(12), (ulong)payload);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(20), 20);
        return b;
    }

    internal static byte[] Rar4()
    {
        var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
        ms.Write([0x00, 0x00, 0x73, 0x00, 0x00, 13, 0, .. new byte[6]]);                 // main header, 13 bytes
        ms.Write([0x00, 0x00, 0x74, 0x00, 0x80, 32, 0, 10, 0, 0, 0, .. new byte[21], .. new byte[10]]);  // file header 32 + 10 data
        ms.Write([0x00, 0x00, 0x7B, 0x00, 0x40, 7, 0]);                                   // end block
        return ms.ToArray();
    }

    internal static byte[] Rar5()
    {
        var ms = new MemoryStream();
        ms.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);
        ms.Write([0, 0, 0, 0, 3, 1, 0, 0]);                 // CRC, size 3, type 1 (main), flags 0, 1 byte of payload
        ms.Write([0, 0, 0, 0, 5, 2, 2, 4, 0, 0, .. new byte[4]]);   // size 5, type 2 (file), flags 2 (data), data size 4; header payload 2 bytes; then 4 data bytes
        ms.Write([0, 0, 0, 0, 3, 5, 0, 0]);                 // end header
        return ms.ToArray();
    }

    internal static byte[] Gzip(byte[] content)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(content);
        return ms.ToArray();
    }

    internal static byte[] Sqlite(int pageSize, int pages)
    {
        var b = new byte[pageSize * pages];
        "SQLite format 3\0"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(16), (ushort)(pageSize == 65536 ? 1 : pageSize));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(28), (uint)pages);
        return b;
    }

    internal static byte[] Pe(bool dll)
    {
        const int peOffset = 0x80, sections = 2, optSize = 240;
        int table = peOffset + 24 + optSize;
        int rawStart = 0x400;
        var b = new byte[rawStart + 0x200 * sections];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(60), peOffset);
        "PE\0\0"u8.CopyTo(b.AsSpan(peOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 4), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 6), sections);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 20), optSize);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 22), (ushort)(dll ? 0x2002 : 0x0002));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(peOffset + 24), 0x20B);
        for (int i = 0; i < sections; i++)
        {
            int s = table + i * 40;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(s + 16), 0x200);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(s + 20), (uint)(rawStart + i * 0x200));
        }
        return b;
    }

    internal static byte[] Ole2()
    {
        // 512-byte sectors; FAT in sector 0; sector 1 = directory (holds the "WordDocument" name); sector 2 free.
        var b = new byte[512 * 4];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(44), 1);               // one FAT sector
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68), 0xFFFFFFFE);      // no DIFAT sectors
        for (int i = 76; i < 512; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(76), 0);               // DIFAT[0] = FAT at sector 0
        var fat = b.AsSpan(512, 512);
        for (int i = 0; i < 128; i++) BinaryPrimitives.WriteUInt32LittleEndian(fat[(i * 4)..], 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFFD);              // sector 0 = FATSECT
        BinaryPrimitives.WriteUInt32LittleEndian(fat[4..], 0xFFFFFFFE);         // sector 1 = end of chain
        Encoding.Unicode.GetBytes("WordDocument").CopyTo(b, 1024 + 128);
        return b[..(512 + 2 * 512)];
    }

    internal static byte[] Rtf() => Encoding.ASCII.GetBytes(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0 Hello \{world\} \\ done}");

    internal static byte[] Pst(bool unicode)
    {
        var b = new byte[4096];
        "!BDN"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(10), (ushort)(unicode ? 23 : 14));
        if (unicode) BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(0xB8), 4096);
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0xA8), 4096);
        return b;
    }

    internal static byte[] Lnk()
    {
        var ms = new MemoryStream();
        var header = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x4C);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(header, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0x01 | 0x04 | 0x80);   // IDList, NameString, Unicode
        ms.Write(header);
        ms.Write([6, 0, 1, 2, 3, 4, 0, 0]);                 // IDList: size 6
        ms.Write([2, 0, (byte)'h', 0, (byte)'i', 0]);       // name: 2 chars Unicode
        ms.Write([12, 0, 0, 0, .. new byte[8]]);            // one extra data block of 12 bytes
        ms.Write([0, 0, 0, 0]);                             // terminal block
        return ms.ToArray();
    }

    // ---- tests ----------------------------------------------------------------------------------------------------
    public static IEnumerable<object[]> Samples() =>
    [
        ["pdf", Pdf(), "3 pages"],
        ["zip", Zip(("a.txt", "hello"), ("b/c.txt", "world")), null!],
        ["7z", SevenZip(100), null!],
        ["rar", Rar4(), null!],
        ["rar", Rar5(), null!],
        ["gz", Gzip(Encoding.ASCII.GetBytes(new string('x', 5000))), null!],
        ["sqlite", Sqlite(512, 3), "3 pages of 512"],
        ["exe", Pe(dll: false), "x64 EXE"],
        ["exe", Pe(dll: true), "x64 DLL"],
        ["ole", Ole2(), "doc"],
        ["rtf", Rtf(), null!],
        ["pst", Pst(unicode: true), "Unicode PST"],
        ["pst", Pst(unicode: false), "ANSI PST"],
        ["lnk", Lnk(), null!],
    ];

    [Theory, MemberData(nameof(Samples))]
    public void Valid_file_reports_exact_size_and_info(string id, byte[] file, string? info)
    {
        var r = Signatures.ById(id)!.Validate(file);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(file.Length, r.Size);
        Assert.Equal(info, r.Info);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Garbage_after_the_file_does_not_change_its_size(string id, byte[] file, string? _)
    {
        var r = Signatures.ById(id)!.Validate([.. file, .. SyntheticFiles.Garbage(1500)]);
        Assert.Equal(CarveStatus.Ok, r.Status);
        Assert.Equal(file.Length, r.Size);
    }

    [Theory, MemberData(nameof(Samples))]
    public void Truncated_file_asks_for_more(string id, byte[] file, string? _)
    {
        if (id == "pdf") return;   // a PDF with incremental updates has several %%EOF marks; cutting the last one yields the previous version, by design
        var r = Signatures.ById(id)!.Validate(file.AsSpan(0, file.Length - 3));
        Assert.Equal(CarveStatus.NeedMore, r.Status);
    }

    [Fact]
    public void Pdf_without_any_eof_asks_for_more() =>
        Assert.Equal(CarveStatus.NeedMore, DocumentValidators.Pdf(Encoding.ASCII.GetBytes("%PDF-1.4 stuff")).Status);

    [Fact]
    public void Ooxml_subtype_and_zip_listing()
    {
        var docx = Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", "<w/>"));
        var r = DocumentValidators.Zip(docx);
        Assert.Equal("docx", r.Info);
        Assert.Equal(".docx", Signatures.ExtensionFor(Signatures.ById("zip")!, r.Info));
        Assert.Equal(["[Content_Types].xml", "word/document.xml"], ZipListing.Entries(docx));
        var epub = Zip(("mimetype", "application/epub+zip"), ("x", "y"));
        Assert.Equal("epub", DocumentValidators.Zip(epub).Info);
    }

    [Fact]
    public void Pe_dll_gets_the_dll_extension() =>
        Assert.Equal(".dll", Signatures.ExtensionFor(Signatures.ById("exe")!, "x64 DLL"));

    [Fact]
    public void Rtf_with_unbalanced_braces_asks_for_more_and_nul_rejects()
    {
        Assert.Equal(CarveStatus.NeedMore, DocumentValidators.Rtf(Encoding.ASCII.GetBytes(@"{\rtf1 {\b bold}")).Status);
        var bad = new byte[200];
        Encoding.ASCII.GetBytes(@"{\rtf1 ").CopyTo(bad, 0);
        Assert.Equal(CarveStatus.Reject, DocumentValidators.Rtf(bad).Status);
    }

    [Fact]
    public void Corrupt_structures_are_rejected()
    {
        var sqlite = Sqlite(512, 2); sqlite[17] = 0x03;                       // page size 768: not a power of two
        Assert.Equal(CarveStatus.Reject, DocumentValidators.Sqlite(sqlite).Status);
        var pe = Pe(false); pe[0x80] = (byte)'X';
        Assert.Equal(CarveStatus.Reject, DocumentValidators.Pe(pe).Status);
        var gz = Gzip([1, 2, 3]); gz[12] ^= 0xFF; gz[13] ^= 0xFF;
        Assert.NotEqual(CarveStatus.Ok, DocumentValidators.Gzip(gz).Status);
        Assert.Equal(CarveStatus.Reject, DocumentValidators.Zip([.. "PK\x03\x04"u8.ToArray(), .. new byte[26], (byte)'P', (byte)'K', 9, 9]).Status);
    }

    [Fact]
    public void Random_mutations_never_throw()
    {
        var rng = new Random(5);
        foreach (var s in Samples())
        {
            var type = Signatures.ById((string)s[0])!;
            var sample = (byte[])s[1];
            for (int i = 0; i < 300; i++)
            {
                var m = (byte[])sample.Clone();
                for (int k = rng.Next(1, 6); k > 0; k--) m[rng.Next(m.Length)] = (byte)rng.Next(256);
                var r = type.Validate(m);
                if (r.Status == CarveStatus.Ok) Assert.True(r.Size > 0 && r.Size <= m.Length, $"{type.Id}: {r.Size}/{m.Length}");
            }
        }
    }
}
