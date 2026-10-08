# File Recovery — Stage B Implementation Plan (Undelete + Previous versions)

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the two sources that need raw disk reads — **Undelete** (deleted MFT records, graded against `$Bitmap`, recovered through the volume reader with LZNT1 support) and **Previous versions** (Volume Shadow Copies) — to the Recovery page, with DFXML byte runs, and prove the undelete pipeline end to end on a throwaway NTFS volume.

**Architecture:** `VolumeReader` (Indexing) is the only way recovery code touches raw bytes; it picks the first readable path (volume handle → physical disk at the partition offset → newest shadow-copy device) and exposes geometry plus `ReadBytes`/`ReadClusters`. Everything above it is testable on a **synthetic volume image** (`MemoryBlockSource`): `ClusterBitmap`, `MftUndeleteSource` (deleted-record enumeration, path reconstruction, grading), `UndeleteWriter` (run reads, sparse fill, LZNT1, hashing, timestamps). Core gains the pure pieces (`Lznt1`, `ByteRun`, extended exports). `ShadowCopySource` is file-level (`\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\…`). The App gets two tab view models and enables the two chips that Stage A shipped disabled.

**Tech Stack:** .NET 10 / C# 14, WPF + CommunityToolkit.Mvvm, xUnit, existing `Kernel32` interop, `System.IO.Hashing`, `System.Management` (Microsoft, MIT) for `Win32_ShadowCopy`.

**Specs:** `docs/superpowers/specs/2026-10-08-file-recovery-design.md` (FR-11…FR-20, §6.2), `docs/superpowers/specs/2026-10-08-file-recovery-uiux.md` (§5.3, §5.4, §6, §8). Stage A plan: `2026-10-08-file-recovery-stage-a.md`.

## Global Constraints
- Recovery code never writes to a source volume. Exceptions, each behind an explicit user action: Recycle Bin *Restore* (Stage A), shadow-copy *Restore in place* (FR-19, writes next to the current file, never overwrites), *Freeze this drive now* (FR-20, snapshot creation, with the consent dialog whose default button is Cancel).
- Every raw handle is `GENERIC_READ`, `FILE_SHARE_READ | FILE_SHARE_WRITE`; `VolumeReader` has no write API. Physical-disk reads use `FILE_FLAG_NO_BUFFERING` with 4 KB-aligned buffers.
- Undelete destinations must pass `RecoverySession.IsDifferentVolume` (volume serial of the opened destination).
- Grade copy and the SSD caveat use the exact wording in UI §8; grade chip keys are those Stage A's `GradeChip` style already knows (Excellent, Good, Partial, Overwritten, Zeroed, Encrypted, Unknown).
- Licensing: the LZNT1 decoder is written from the public format description (MS-XCA §2.3) and DiscUtils' MIT implementation; nothing from PhotoRec/TSK/libyal is ported.
- `TreatWarningsAsErrors` stays on in Core and Indexing; Release build 0 warnings. Commit after every task with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Elevated tests carry `[Trait("Category", "Elevated")]` and return early when not elevated; the VHD acceptance test cleans up its disk even on failure.

## File Structure
```
src/FileHound.Core/Recovery/
  Lznt1.cs                   LZNT1 decompressor (pure)
  RecoveryModels.cs          + ByteRun, RecoveredFile.Runs
  Exports.cs                 + DFXML <byte_runs>
src/FileHound.Indexing/Ntfs/
  MftRecordParser.cs         + sequence, parent sequence, $FILE_NAME timestamps/sizes, $DATA flags/initialized size/resident bytes
src/FileHound.Indexing/Recovery/
  BlockSources.cs            IBlockSource, VolumeBlockSource, PhysicalDiskBlockSource, ShadowBlockSource, MemoryBlockSource (internal)
  VolumeReader.cs            path selection, geometry, ReadBytes/ReadClusters, MFT extents, record reads
  ClusterBitmap.cs           FSCTL_GET_VOLUME_BITMAP → $Bitmap (record 6) fallback; IsAllocated, CountAllocated
  UndeleteRecord.cs          what one deleted record carries (Key object for Undelete candidates)
  MftUndeleteSource.cs       enumerate deleted records, reconstruct paths, grade (FR-11…FR-14, FR-16)
  ContentCheck.cs            "first cluster looks like a <ext>" signature table
  UndeleteWriter.cs          FR-15 recovery of one record to a destination path
  ShadowCopies.cs            enumerate / create snapshots (WMI, vssadmin fallback)
  ShadowCopySource.cs        versions of a path, directory listing, save-to, restore in place (FR-17…FR-20)
  RecoverySession.cs         + Reader, Bitmap, UndeleteAsync, RecoverAsync for Undelete/ShadowCopy
src/FileHound.App/ViewModels/Recovery/
  UndeleteTabViewModel.cs, PreviousVersionsTabViewModel.cs, RecoveryViewModel.cs (+ tabs), RecoveryItem.cs (+ ShadowCopy key)
src/FileHound.App/Views/RecoveryView.xaml(.cs)   two new tab panels, chips enabled
tests/FileHound.Core.Tests/Lznt1Tests.cs, RecoveryStoreAndExportTests.cs (+byte_runs)
tests/FileHound.Indexing.Tests/MftRecordBuilder.cs (+deleted/sequence/timestamps/flags), MftRecordParserTests.cs,
  SyntheticVolume.cs (test helper: builds an NTFS-shaped image in memory), VolumeReaderTests.cs, ClusterBitmapTests.cs,
  UndeleteTests.cs, ShadowCopyTests.cs, VhdAcceptanceTests.cs (Elevated)
tests/FileHound.App.Tests/RecoveryViewModelTests.cs (+undelete/previous-versions VM tests)
```

---

### Task 1: Parser extensions for deleted records (Indexing)

**Files:**
- Modify: `src/FileHound.Indexing/Ntfs/MftRecordParser.cs`
- Modify: `tests/FileHound.Indexing.Tests/MftRecordBuilder.cs`
- Test: `tests/FileHound.Indexing.Tests/MftRecordParserTests.cs`

**Interfaces:**
- Produces (new `MftRecord` fields; existing ones unchanged):
```csharp
public ushort Sequence;              // record header @0x10
public ushort ParentSequence;        // high 16 bits of $FILE_NAME parent reference
public long CreatedUtcTicks;         // $STANDARD_INFORMATION @0, else $FILE_NAME @8
public long NameModifiedUtcTicks;    // $FILE_NAME @16
public long MftChangedUtcTicks;      // $FILE_NAME @24
public long AccessedUtcTicks;        // $FILE_NAME @32
public long RealSize;                // $FILE_NAME @48 (what the file system last recorded for the name)
public ushort DataFlags;             // unnamed $DATA attribute header @12: 0x0001 compressed, 0x4000 encrypted, 0x8000 sparse
public byte CompressionUnit;         // unnamed $DATA @34 (log2 clusters per unit; 4 → 16 clusters)
public long InitializedSize;         // unnamed $DATA @56 (non-resident)
public long AllocatedSize;           // unnamed $DATA @40 (non-resident)
public bool DataIsResident;          // unnamed $DATA was resident
public ReadOnlySpan<byte> ResidentData;  // its bytes when resident
public bool IsBaseRecord => BaseRecord == 0;
public const ushort DataCompressed = 0x0001, DataEncrypted = 0x4000, DataSparse = 0x8000;
```
- Builder additions (test helper): `Sequence(ushort)`, `FileName(parent, name, nameSpace = 1, parentSequence = 3, createdUtc = null, modifiedUtc = null, realSize = 0)`, `NonResidentData(dataSize, runs, startVcn = 0, streamName = null, flags = 0, initializedSize = null, compressionUnit = 0)`. A builder without `InUse()` produces a deleted record (flags 0).

- [ ] **Step 1: Extend the builder**

In `MftRecordBuilder`: add `private ushort _sequence = 1;` and
```csharp
public MftRecordBuilder Sequence(ushort sequence) { _sequence = sequence; return this; }

public MftRecordBuilder FileName(long parentRecord, string name, byte nameSpace = 1, ushort parentSequence = 3,
    DateTime? createdUtc = null, DateTime? modifiedUtc = null, long realSize = 0)
{
    var v = new byte[66 + name.Length * 2];
    BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parentRecord | ((ulong)parentSequence << 48));
    if (createdUtc is { } c) BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), FileTime(c));
    if (modifiedUtc is { } m) { BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(16), FileTime(m)); BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(24), FileTime(m)); BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(32), FileTime(m)); }
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(40), (realSize + 4095) / 4096 * 4096);
    BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(48), realSize);
    v[64] = (byte)name.Length; v[65] = nameSpace;
    MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(v.AsSpan(66));
    return Resident(0x30, v);
}
```
In `NonResidentData` add parameters `ushort flags = 0, long? initializedSize = null, byte compressionUnit = 0` and write `flags` at `a[12..]`, `compressionUnit` at `a[34]`, `initializedSize ?? dataSize` at `a[56..]`. In `Build()` write `_sequence` at offset 16 (replacing the constant 1).

- [ ] **Step 2: Write the failing tests** (append to `MftRecordParserTests`)
```csharp
[Fact]
public void Deleted_record_exposes_sequence_timestamps_sizes_and_flags()
{
    var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var modified = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    var rec = new MftRecordBuilder().Sequence(7)
        .StandardInfo(modified, attributes: 0x20)
        .FileName(100, "gone.jpg", parentSequence: 9, createdUtc: created, modifiedUtc: modified, realSize: 5000)
        .NonResidentData(5000, [0x21, 0x02, 0x10, 0x00, 0x00], flags: MftRecord.DataSparse, initializedSize: 4096, compressionUnit: 0)
        .Build();
    Assert.True(MftRecordParser.ApplyFixups(rec));
    Assert.True(MftRecordParser.TryParse(rec, out var r));
    Assert.False(r.InUse);
    Assert.True(r.IsBaseRecord);
    Assert.Equal(7, r.Sequence);
    Assert.Equal(100, r.ParentRecord);
    Assert.Equal(9, r.ParentSequence);
    Assert.Equal(modified.Ticks, r.NameModifiedUtcTicks);
    Assert.Equal(modified.Ticks, r.ModifiedUtcTicks);
    Assert.Equal(5000, r.RealSize);
    Assert.Equal(5000, r.Size);
    Assert.Equal(4096, r.InitializedSize);
    Assert.Equal(8192, r.AllocatedSize);
    Assert.Equal(MftRecord.DataSparse, r.DataFlags);
    Assert.False(r.DataIsResident);
}

[Fact]
public void Resident_data_bytes_are_exposed()
{
    var rec = new MftRecordBuilder().InUse().FileName(5, "tiny.txt").ResidentData(12).Build();
    MftRecordParser.ApplyFixups(rec);
    Assert.True(MftRecordParser.TryParse(rec, out var r));
    Assert.True(r.DataIsResident);
    Assert.Equal(12, r.ResidentData.Length);
}

[Fact]
public void Created_falls_back_to_file_name_when_standard_info_is_missing()
{
    var created = new DateTime(2025, 5, 5, 5, 5, 5, DateTimeKind.Utc);
    var rec = new MftRecordBuilder().FileName(5, "x", createdUtc: created).Build();
    MftRecordParser.ApplyFixups(rec);
    MftRecordParser.TryParse(rec, out var r);
    Assert.Equal(created.Ticks, r.CreatedUtcTicks);
}
```
- [ ] **Step 3:** Run `dotnet test tests/FileHound.Indexing.Tests --filter MftRecordParserTests` → FAIL (missing members).
- [ ] **Step 4: Implement** in `MftRecordParser.TryParse`: read `Sequence` at `record[16..]`; in case 0x10 also set `CreatedUtcTicks` from `v[0..]`; in case 0x30 read parent sequence (`>> 48`), the four timestamps (`v[8..]`, `v[16..]`, `v[24..]`, `v[32..]`; convert with the existing FILETIME→ticks rule), `RealSize = v[48..]`, and `CreatedUtcTicks` from `v[8..]` only when still 0; in case 0x80: `DataFlags = ReadUInt16(attr[12..])`; resident → `DataIsResident = true; ResidentData = v`; non-resident → `CompressionUnit = attr[34]`, `AllocatedSize = attr[40..]`, `InitializedSize = attr[56..]`. Add a private `static long Ticks(long fileTime)` helper used by all timestamp reads.
- [ ] **Step 5:** Tests PASS; run the full Indexing suite (the Turbo accumulator must be unaffected).
- [ ] **Step 6:** Commit `feat(indexing): MFT parser exposes sequence, timestamps, sizes and $DATA flags for undelete`.

---

### Task 2: LZNT1 decompressor (Core)

**Files:**
- Create: `src/FileHound.Core/Recovery/Lznt1.cs`
- Test: `tests/FileHound.Core.Tests/Lznt1Tests.cs`

**Interfaces:**
```csharp
namespace FileHound.Core.Recovery;
public static class Lznt1
{
    public const int ChunkSize = 4096;
    /// Decompresses one compression unit; returns bytes written (≤ output.Length). Stops at a zero chunk header
    /// or the end of input. Throws InvalidDataException for a back-reference before the chunk start.
    public static int Decompress(ReadOnlySpan<byte> compressed, Span<byte> output);
}
```
Format (MS-XCA 2.3): stream = chunks. Chunk header u16: bits 0–11 = compressed size − 1, bits 12–14 = signature `011`, bit 15 = 1 when compressed. Uncompressed chunk: copy `size` bytes. Compressed chunk: repeat { flag byte; for bit i in 0..7: if clear → literal byte; if set → u16 token: with `i = producedInChunk − 1`, `lg = 0; while (i >= 0x10) { i >>= 1; lg++; }`, `length = (token & (0xFFF >> lg)) + 3`, `offset = (token >> (12 − lg)) + 1`; copy byte-by-byte from `pos − offset` (overlap allowed) } until the chunk's compressed bytes are consumed or 4096 output bytes produced. A chunk header of 0 ends the stream; the rest of `output` is left as is (caller zero-fills).

- [ ] **Step 1: Write the failing tests**
```csharp
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
        var body = new List<byte> { 0x00 }; body.AddRange(lit[..8]);
        body.Add(0x00); body.AddRange(lit[8..16]);
        body.Add(0b0000_0010); body.Add(lit[16]);
        ushort token = (ushort)((16 << 11) | 2); body.Add((byte)token); body.Add((byte)(token >> 8));
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
        var a = new byte[4096]; Array.Fill(a, (byte)1);
        var b = new byte[4096]; Array.Fill(b, (byte)2);
        byte[] input = [.. Chunk(false, a), .. Chunk(false, b)];
        var output = new byte[8192];
        Assert.Equal(8192, Lznt1.Decompress(input, output));
        Assert.Equal(2, output[8191]);
    }
}
```
- [ ] **Step 2:** FAIL. **Step 3: Implement** exactly per the format notes; chunk loop writes into `output[chunkStart..]` and clamps at `output.Length`. **Step 4:** PASS.
- [ ] **Step 5:** Commit `feat(core): LZNT1 decompressor for compressed NTFS files`.

---

### Task 3: Block sources and `VolumeReader` (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/BlockSources.cs`, `src/FileHound.Indexing/Recovery/VolumeReader.cs`
- Modify: `src/FileHound.Indexing/Interop/Kernel32.cs` (`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, `FILE_FLAG_NO_BUFFERING`, `GetVolumePathNamesForVolumeName` not needed yet)
- Test: `tests/FileHound.Indexing.Tests/VolumeReaderTests.cs`, helper `tests/FileHound.Indexing.Tests/SyntheticVolume.cs`

**Interfaces:**
```csharp
namespace FileHound.Indexing.Recovery;
public enum VolumeReadPath { Volume, PhysicalDisk, ShadowCopy, Memory }
internal interface IBlockSource : IDisposable
{
    VolumeReadPath Path { get; }
    string Description { get; }            // "\\.\C:", "\\.\PhysicalDrive0 @122683392", shadow device, "memory"
    void Read(long volumeOffset, Span<byte> dest);   // whole span or throws IOException; offset and length sector-aligned
}
internal sealed class MemoryBlockSource(byte[] image) : IBlockSource { … }          // tests
internal sealed class VolumeBlockSource : IBlockSource { static VolumeBlockSource? TryOpen(char letter); }
internal sealed class PhysicalDiskBlockSource : IBlockSource { static PhysicalDiskBlockSource? TryOpen(SafeFileHandle control); } // extents → \\.\PhysicalDriveN + offset; rejects >1 extent; boot sector must say NTFS at 3
internal sealed class ShadowBlockSource : IBlockSource { static ShadowBlockSource? TryOpen(char letter); }  // newest ShadowCopies.List(letter)

public sealed record VolumeGeometry(int BytesPerSector, int BytesPerCluster, int RecordSize, long TotalClusters, long MftStartLcn, long MftValidDataLength);

public sealed class VolumeReader : IDisposable
{
    public static VolumeReader Open(DriveDescriptor drive, bool allowShadow = true);   // throws NotSupportedException("No readable path …") listing each failure
    internal VolumeReader(IBlockSource source, VolumeGeometry geometry, SafeFileHandle? control);
    public VolumeReadPath Path { get; }  public string PathDescription { get; }
    public VolumeGeometry Geometry { get; }
    public SafeFileHandle? ControlHandle { get; }     // \\.\X: for FSCTLs; null for Memory
    public void ReadBytes(long volumeOffset, Span<byte> dest);          // any offset/length; aligns internally
    public void ReadClusters(long lcn, int count, Span<byte> dest);
    public byte[] ReadRecord(long recordNo);                            // via MFT extents; fixups applied; throws for out of range
    public IReadOnlyList<DataRun> MftExtents { get; }
    public static VolumeGeometry ParseBootSector(ReadOnlySpan<byte> sector);   // bytes/sector @11, sectors/cluster @13, total sectors @40, MFT LCN @48, clusters-per-record @64 (negative → 2^-n bytes)
}
```
Selection order in `Open`: `VolumeBlockSource.TryOpen` (test read of 4 KB at `MftStartLcn`) → `PhysicalDiskBlockSource.TryOpen` → `ShadowBlockSource.TryOpen` if allowed. Geometry comes from `FSCTL_GET_NTFS_VOLUME_DATA` on the control handle; when that fails (only the Memory source in practice), from `ParseBootSector(read 512 @0)`.

- [ ] **Step 1: Synthetic volume helper** (`tests/.../SyntheticVolume.cs`): builds a byte[] image with cluster 4096, record 1024, MFT at LCN 4 holding N records, `$Bitmap` (record 6) data at LCN 2 covering all clusters, and a boot sector. API:
```csharp
internal sealed class SyntheticVolume
{
    public const int Cluster = 4096, Record = 1024; public const long MftLcn = 4, BitmapLcn = 2;
    public SyntheticVolume(int clusters = 256, int records = 64);
    public byte[] Image { get; }
    public void SetRecord(long no, byte[] record);                 // as built by MftRecordBuilder (fixups applied on disk)
    public void SetAllocated(long lcn, bool allocated);           // flips the $Bitmap bit
    public void WriteCluster(long lcn, ReadOnlySpan<byte> data);  // data for recovered files
    public VolumeReader OpenReader();                              // new VolumeReader(new MemoryBlockSource(Image), geometry, control: null)
}
```
Its constructor writes: record 0 (`$MFT`, in use, non-resident data runs covering `records*1024/4096` clusters at `MftLcn`), record 5 (root dir, in use, name "." parent 5), record 6 (`$Bitmap`, in use, non-resident runs: 1 cluster at `BitmapLcn`), and marks clusters 0..(MftLcn + mftClusters) allocated. Boot sector bytes at 0: `"NTFS    "` at 3, 512 @11, 8 @13, total sectors @40, MFT LCN @48, 0xF6 (= 1024-byte records) @64.
- [ ] **Step 2: Write the failing tests**
```csharp
public class VolumeReaderTests
{
    [Fact]
    public void Reads_geometry_from_boot_sector_and_records_through_mft_extents()
    {
        var vol = new SyntheticVolume();
        vol.SetRecord(20, new MftRecordBuilder().InUse().FileName(5, "a.txt").ResidentData(3).Build());
        using var r = vol.OpenReader();
        Assert.Equal(VolumeReadPath.Memory, r.Path);
        Assert.Equal(4096, r.Geometry.BytesPerCluster);
        Assert.Equal(1024, r.Geometry.RecordSize);
        Assert.Equal(SyntheticVolume.MftLcn, r.Geometry.MftStartLcn);
        var rec = r.ReadRecord(20);
        Assert.True(MftRecordParser.TryParse(rec, out var p));
        Assert.Equal("a.txt", p.Name.ToString());
    }

    [Fact]
    public void Unaligned_byte_reads_are_served()
    {
        var vol = new SyntheticVolume();
        vol.WriteCluster(10, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        using var r = vol.OpenReader();
        var buf = new byte[10];
        r.ReadBytes(10 * 4096 + 1000, buf);
        Assert.Equal(Enumerable.Range(1000, 10).Select(i => (byte)i).ToArray(), buf);
    }

    [Fact]
    public void Boot_sector_parser_handles_negative_record_size()
    {
        var s = new byte[512];
        "NTFS    "u8.CopyTo(s.AsSpan(3));
        BitConverter.GetBytes((ushort)512).CopyTo(s, 11); s[13] = 8;
        BitConverter.GetBytes(1_000_000L).CopyTo(s, 40); BitConverter.GetBytes(786432L).CopyTo(s, 48); s[64] = 0xF6;
        var g = VolumeReader.ParseBootSector(s);
        Assert.Equal(4096, g.BytesPerCluster); Assert.Equal(1024, g.RecordSize); Assert.Equal(125_000, g.TotalClusters); Assert.Equal(786432, g.MftStartLcn);
    }

    [Fact]
    public void Record_out_of_range_throws()
    {
        var vol = new SyntheticVolume(records: 16);
        using var r = vol.OpenReader();
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadRecord(16));
    }
}
```
- [ ] **Step 3:** FAIL. **Step 4: Implement** `BlockSources.cs` and `VolumeReader.cs`. `ReadBytes` rounds the request out to sector boundaries into a pooled buffer and copies the slice. `ReadRecord` maps record → VCN → extent → LCN using `MftExtents` (from record 0 read at `MftStartLcn`; attribute list → `NotSupportedException("$MFT has an attribute list")`), applies fixups (`acceptAlreadyApplied: false`) and throws `InvalidDataException` on a torn record. `PhysicalDiskBlockSource.TryOpen` issues `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (0x560000) on the control handle, refuses `NumberOfDiskExtents != 1`, opens `\\.\PhysicalDrive{N}` with `FILE_FLAG_NO_BUFFERING`, reads the first 4 KB at the partition offset through a 4 KB-aligned `NativeMemory.AlignedAlloc` buffer and requires `NTFS` at offset 3 (a `-FVE-FS-` sector → return null with description "BitLocker"). `ShadowBlockSource.TryOpen` uses `ShadowCopies.List(letter)` from Task 8 — until then it returns null (stub documented in the file; Task 8 replaces it).
- [ ] **Step 5:** PASS. **Step 6:** Commit `feat(indexing): VolumeReader with volume, physical-disk and shadow-copy paths`.

---

### Task 4: `ClusterBitmap` (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/ClusterBitmap.cs`
- Test: `tests/FileHound.Indexing.Tests/ClusterBitmapTests.cs`

**Interfaces:**
```csharp
public sealed class ClusterBitmap
{
    public static ClusterBitmap Load(VolumeReader reader);        // FSCTL_GET_VOLUME_BITMAP on ControlHandle (loop on ERROR_MORE_DATA, 1 MB chunks) → else record 6's unnamed $DATA through the reader
    public long TotalClusters { get; }  public string Source { get; }   // "FSCTL" | "$Bitmap"
    public bool IsAllocated(long lcn);                               // out of range → true (conservative)
    public (long Allocated, long Total) Count(IReadOnlyList<DataRun> runs, long clustersNeeded);   // sparse runs skipped; counts only the first clustersNeeded clusters
    public bool AnyChanged(IReadOnlyList<DataRun> runs, long clustersNeeded, ClusterBitmap other);
}
```
- [ ] **Step 1: Tests**
```csharp
public class ClusterBitmapTests
{
    [Fact]
    public void Falls_back_to_bitmap_record_and_reads_bits()
    {
        var vol = new SyntheticVolume(clusters: 256);
        vol.SetAllocated(100, true); vol.SetAllocated(101, false); vol.SetAllocated(102, true);
        using var r = vol.OpenReader();
        var bm = ClusterBitmap.Load(r);
        Assert.Equal("$Bitmap", bm.Source);
        Assert.Equal(256, bm.TotalClusters);
        Assert.True(bm.IsAllocated(100)); Assert.False(bm.IsAllocated(101)); Assert.True(bm.IsAllocated(102));
        Assert.True(bm.IsAllocated(999_999));
        var runs = new List<DataRun> { new(0, 100, 3), new(3, -1, 2), new(5, 103, 1) };
        Assert.Equal((2L, 4L), bm.Count(runs, clustersNeeded: 6));
        Assert.Equal((1L, 2L), bm.Count(runs, clustersNeeded: 2));
    }
}
```
- [ ] **Step 2:** FAIL. **Step 3: Implement** (bit `lcn & 7` of byte `lcn >> 3`, LSB first). The FSCTL path: input `STARTING_LCN_INPUT_BUFFER{0}`, output `VOLUME_BITMAP_BUFFER{StartingLcn, BitmapSize, Buffer}`; keep calling with the next starting LCN while the error is 234 (`ERROR_MORE_DATA`); error 50 → fallback. **Step 4:** PASS. **Step 5:** Commit `feat(indexing): cluster bitmap from FSCTL or $Bitmap`.

---

### Task 5: `MftUndeleteSource` — enumerate, reconstruct paths, grade (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/UndeleteRecord.cs`, `ContentCheck.cs`, `MftUndeleteSource.cs`
- Test: `tests/FileHound.Indexing.Tests/UndeleteTests.cs`

**Interfaces:**
```csharp
public sealed record UndeleteRecord(long RecordNo, ushort Sequence, long ParentRecordNo, ushort ParentSequence, string Name, bool IsDirectory,
    long RealSize, long InitializedSize, long AllocatedSize, ushort DataFlags, byte CompressionUnit, bool DataIsResident,
    byte[]? ResidentData, byte[]? DataRunBytes, long CreatedUtcTicks, long ModifiedUtcTicks)
{
    public IReadOnlyList<DataRun> Runs => DataRunBytes is null ? [] : DataRuns.Decode(DataRunBytes);   // cached
    public bool IsCompressed/IsEncrypted/IsSparse;
}

public static class ContentCheck
{
    /// null = no opinion (unknown extension), true = matches, false = does not match its signature.
    public static bool? LooksLike(string extension, ReadOnlySpan<byte> firstBytes);   // jpg/jpeg FFD8FF, png 89504E47, gif "GIF8", pdf "%PDF", zip/docx/xlsx/pptx/jar/apk "PK\3\4", mp4/mov/m4a/heic "ftyp"@4, exe/dll "MZ", 7z 377ABCAF, rar "Rar!", gz 1F8B, bmp "BM", wav/avi "RIFF", mp3 "ID3" or FFFx, flac "fLaC", ogg "OggS", mkv/webm 1A45DFA3, sqlite "SQLite format 3", doc/xls/ppt/msg D0CF11E0, rtf "{\rtf"
    public static bool IsAllZero(ReadOnlySpan<byte> bytes);
}

public sealed record UndeleteProgress(long RecordsScanned, long RecordsTotal, int Found);

public sealed class MftUndeleteSource(VolumeReader reader, ClusterBitmap bitmap, VolumeIndex? liveIndex, IReadOnlyList<DeletionEntry>? deletionLog)
{
    public List<RecoveryCandidate> Scan(IProgress<UndeleteProgress>? progress, CancellationToken ct);
    internal string? ResolveFolder(UndeleteRecord r);           // FR-13
    internal (RecoveryGrade Grade, int Percent, string? Detail) Grade(UndeleteRecord r, ReadOnlySpan<byte> firstCluster);   // FR-14
}
```
Scan algorithm: read the MFT through `reader.MftExtents` in 4 MB chunks (same loop shape as `RawMftReader`), for every record ≥ 16 with `ApplyFixups` ok, `TryParse` ok, `!InUse`, `IsBaseRecord`, `HasName`: build an `UndeleteRecord`; keep *all* deleted records (files and dirs) in `Dictionary<long, UndeleteRecord> deleted` for path resolution; then produce candidates for files and directories. Path (FR-13): walk parents: if `liveIndex.FindByRecord(parent)` is live and `liveIndex.RecordSequence`… the live index does not store sequences, so accept the live parent when it is live (the deletion log's sequence check covers the common reuse case); else if `deleted` has the parent and its `Sequence` is `ParentSequence` or `ParentSequence − 1`, recurse; depth ≤ 64; record 5 → root. Unresolved → `null` (UI shows `<unknown folder>`), then try `deletionLog` for an entry with the same `(RecordNo, Sequence)` → use its `ParentPath` and `DeletedUtcTicks` (FR-16: and mark `Detail = "also in Recently deleted"`). Grade (FR-14): encrypted → Encrypted; resident → Excellent 100; runs empty and size 0 → Excellent; else `bitmap.Count(runs, clustersNeeded = ceil(InitializedSize / cluster))`: allocated == 0 → first cluster check: `IsAllZero && InitializedSize > 0` → Zeroed; `LooksLike(ext) == false` → Good ("header does not match the file type"); else Excellent; allocated == total → Overwritten 0; else Partial with `percent = 100 * (total − allocated) / total`. Compressed files are graded the same way and `Detail = "Compressed (LZNT1)"`.

- [ ] **Step 1: Tests** (synthetic volume; a live `VolumeIndex` with record 100 = `Docs` under root)
```csharp
public class UndeleteTests
{
    private static (SyntheticVolume Vol, VolumeIndex Index) Setup()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 128);
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo); index.SetRecord(0, 5);
        index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        vol.SetRecord(100, new MftRecordBuilder().Sequence(2).InUse(directory: true).FileName(5, "Docs").Build());
        return (vol, index);
    }

    [Fact]
    public void Deleted_file_with_free_clusters_is_excellent_and_gets_its_folder()
    {
        var (vol, index) = Setup();
        var jpg = new byte[4096]; jpg[0] = 0xFF; jpg[1] = 0xD8; jpg[2] = 0xFF;
        vol.WriteCluster(200, jpg); vol.WriteCluster(201, new byte[4096]);
        vol.SetAllocated(200, false); vol.SetAllocated(201, false);
        vol.SetRecord(40, new MftRecordBuilder().Sequence(5).FileName(100, "photo.jpg", parentSequence: 2, realSize: 6000)
            .NonResidentData(6000, [0x21, 0x02, 0xC8, 0x00, 0x00]).Build());
        using var r = vol.OpenReader();
        var c = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(null, CancellationToken.None).Single();
        Assert.Equal("photo.jpg", c.Name); Assert.Equal(@"Q:\Docs", c.OriginalFolder);
        Assert.Equal(RecoveryGrade.Excellent, c.Grade); Assert.Equal(6000, c.Size);
    }

    [Fact]
    public void Partially_reused_clusters_give_a_percentage()
    {
        var (vol, index) = Setup();
        for (long l = 300; l < 304; l++) vol.SetAllocated(l, l == 301);
        vol.SetRecord(41, new MftRecordBuilder().FileName(100, "doc.bin", parentSequence: 2, realSize: 16384)
            .NonResidentData(16384, [0x21, 0x04, 0x2C, 0x01, 0x00]).Build());
        using var r = vol.OpenReader();
        var c = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(null, CancellationToken.None).Single();
        Assert.Equal(RecoveryGrade.Partial, c.Grade); Assert.Equal(75, c.PercentIntact);
    }

    [Fact]
    public void Zeroed_first_cluster_means_trimmed()
    {
        var (vol, index) = Setup();
        vol.SetAllocated(310, false);
        vol.SetRecord(42, new MftRecordBuilder().FileName(100, "gone.txt", parentSequence: 2, realSize: 100)
            .NonResidentData(100, [0x21, 0x01, 0x36, 0x01, 0x00], initializedSize: 100).Build());
        using var r = vol.OpenReader();
        var c = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(null, CancellationToken.None).Single();
        Assert.Equal(RecoveryGrade.Zeroed, c.Grade);
    }

    [Fact]
    public void Deleted_parent_chain_is_followed_when_sequence_matches()
    {
        var (vol, index) = Setup();
        vol.SetRecord(50, new MftRecordBuilder().Sequence(3).FileName(100, "Old", parentSequence: 2).Build());  // deleted dir, seq 3
        vol.SetRecord(51, new MftRecordBuilder().FileName(50, "note.txt", parentSequence: 3).ResidentData(5).Build());
        vol.SetRecord(52, new MftRecordBuilder().FileName(50, "stale.txt", parentSequence: 9).ResidentData(5).Build());  // parent reused since
        using var r = vol.OpenReader();
        var cs = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(null, CancellationToken.None);
        Assert.Equal(@"Q:\Docs\Old", cs.Single(c => c.Name == "note.txt").OriginalFolder);
        Assert.Null(cs.Single(c => c.Name == "stale.txt").OriginalFolder);
        Assert.Equal(@"Q:\Docs", cs.Single(c => c.Name == "Old").OriginalFolder);
    }

    [Fact]
    public void Deletion_log_supplies_folder_and_time_for_the_same_record()
    {
        var (vol, index) = Setup();
        vol.SetRecord(60, new MftRecordBuilder().Sequence(4).FileName(777, "orphan.txt", parentSequence: 1).ResidentData(5).Build());
        var when = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var log = new List<DeletionEntry> { new(60, 4, 777, "orphan.txt", @"Q:\Gone", 5, false, 0, when, DeletionKind.Deleted, 1) };
        using var r = vol.OpenReader();
        var c = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, log).Scan(null, CancellationToken.None).Single();
        Assert.Equal(@"Q:\Gone", c.OriginalFolder); Assert.Equal(when, c.DeletedUtc!.Value.Ticks);
    }

    [Fact]
    public void Encrypted_and_in_use_records_are_handled()
    {
        var (vol, index) = Setup();
        vol.SetRecord(70, new MftRecordBuilder().FileName(100, "secret.txt", parentSequence: 2).NonResidentData(10, [0x21, 0x01, 0x40, 0x01, 0x00], flags: MftRecord.DataEncrypted).Build());
        vol.SetRecord(71, new MftRecordBuilder().InUse().FileName(100, "live.txt", parentSequence: 2).ResidentData(1).Build());
        using var r = vol.OpenReader();
        var cs = new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(null, CancellationToken.None);
        Assert.Equal(RecoveryGrade.Encrypted, cs.Single().Grade);
    }

    [Theory]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(".png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, true)]
    [InlineData(".pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }, true)]
    [InlineData(".docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData(".jpg", new byte[] { 0x00, 0x00, 0x00, 0x00 }, false)]
    [InlineData(".txt", new byte[] { 0x41, 0x42 }, null)]
    public void Content_check(string ext, byte[] head, bool? expected) => Assert.Equal(expected, ContentCheck.LooksLike(ext, head));
}
```
- [ ] **Step 2:** FAIL. **Step 3: Implement.** **Step 4:** PASS. **Step 5:** Commit `feat(indexing): undelete source — deleted MFT records with paths and grades`.

---

### Task 6: `UndeleteWriter` and session integration (Indexing + Core)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/UndeleteWriter.cs`
- Modify: `src/FileHound.Core/Recovery/RecoveryModels.cs` (`ByteRun`, `RecoveredFile.Runs`), `src/FileHound.Core/Recovery/Exports.cs` (DFXML `<byte_runs>`), `src/FileHound.Indexing/Recovery/RecoverySession.cs`
- Test: `tests/FileHound.Indexing.Tests/UndeleteTests.cs` (writer), `tests/FileHound.Core.Tests/RecoveryStoreAndExportTests.cs` (byte runs)

**Interfaces:**
```csharp
// Core
public sealed record ByteRun(long FileOffset, long Length, long ImageOffset);      // ImageOffset = -1 for sparse/resident
public sealed record RecoveredFile(…, string? Error, IReadOnlyList<ByteRun>? Runs = null);
// Indexing
public static class UndeleteWriter
{
    /// Writes the record's data to destPath (never overwrites); returns bytes written, SHA-256 hex, the final grade, and byte runs.
    public static (long Bytes, string Sha256, RecoveryGrade FinalGrade, IReadOnlyList<ByteRun> Runs)
        Recover(VolumeReader reader, ClusterBitmap bitmap, UndeleteRecord record, RecoveryGrade gradeAtScan, string destPath, CancellationToken ct);
}
// RecoverySession additions
public VolumeReader Reader { get; }     // opened on first use; NotSupportedException surfaces as the tab's error
public ClusterBitmap Bitmap { get; }    // loaded on first use; Refresh() re-reads
public string ReadPathDescription { get; }
public Task<IReadOnlyList<RecoveryCandidate>> UndeleteAsync(IProgress<UndeleteProgress>? progress, CancellationToken ct);
```
Writer rules (FR-15): resident → write `ResidentData[..RealSize]`. Non-resident: `clustersNeeded = ceil(InitializedSize / cluster)`; before copying re-count the bitmap (`bitmap.Count`) — if more clusters are allocated now than at scan time, downgrade (`Partial`/`Overwritten`) and continue. Iterate runs in VCN order up to `clustersNeeded`: sparse → zeros; allocated → `reader.ReadClusters` in ≤ 1024-cluster pieces. Compressed (`IsCompressed`, unit = `1 << CompressionUnit` clusters, default 16): process per unit — a unit whose runs contain a sparse tail (allocated clusters < unit) is LZNT1 → read the allocated clusters, `Lznt1.Decompress` into a unit-sized buffer (zero-filled); a fully allocated unit is stored raw; an entirely sparse unit is zeros. Bytes beyond `InitializedSize` up to `RealSize` are zeros. Truncate to `RealSize`, hash with `IncrementalHash` while writing, `File.SetCreationTimeUtc/SetLastWriteTimeUtc` from the record. Byte runs: one per non-sparse run (`FileOffset = vcn*cluster`, `ImageOffset = lcn*cluster`, `Length` clamped to the file).

- [ ] **Step 1: Tests**
```csharp
public class UndeleteWriterTests : IDisposable
{
    private readonly string _dest = Directory.CreateTempSubdirectory("fh-undel-").FullName;
    public void Dispose() => Directory.Delete(_dest, true);

    [Fact]
    public void Recovers_non_resident_file_with_sparse_run_truncated_to_real_size()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        var a = new byte[4096]; Array.Fill(a, (byte)7); vol.WriteCluster(100, a);
        var b = new byte[4096]; Array.Fill(b, (byte)9); vol.WriteCluster(101, b);
        vol.SetAllocated(100, false); vol.SetAllocated(101, false);
        // runs: 2 clusters @100, 1 sparse cluster; real size 10000 (cuts into the sparse cluster)
        var rec = new UndeleteRecord(30, 1, 5, 1, "f.bin", false, 10000, 10000, 12288, 0, 0, false, null, [0x21, 0x02, 0x64, 0x00, 0x01, 0x01, 0x00], 0, 0);
        using var r = vol.OpenReader();
        var (bytes, sha, grade, runs) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, Path.Combine(_dest, "f.bin"), CancellationToken.None);
        var data = File.ReadAllBytes(Path.Combine(_dest, "f.bin"));
        Assert.Equal(10000, bytes); Assert.Equal(10000, data.Length);
        Assert.Equal(7, data[0]); Assert.Equal(9, data[4096]); Assert.Equal(0, data[9999]);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), sha);
        Assert.Equal(RecoveryGrade.Excellent, grade);
        Assert.Equal(new ByteRun(0, 8192, 100 * 4096), runs[0]);
    }

    [Fact]
    public void Compressed_unit_is_decompressed()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        // One 16-cluster unit: 1 allocated cluster holding an LZNT1 chunk ("ABC" + back-reference → "ABCABCABC"), 15 sparse.
        var chunk = new byte[4096]; byte[] body = [0b0000_1000, (byte)'A', (byte)'B', (byte)'C', 0x03, 0x20];
        BitConverter.GetBytes((ushort)(0xB000 | (body.Length - 1))).CopyTo(chunk, 0); body.CopyTo(chunk, 2);
        vol.WriteCluster(200, chunk); vol.SetAllocated(200, false);
        var rec = new UndeleteRecord(31, 1, 5, 1, "c.txt", false, 9, 9, 65536, MftRecord.DataCompressed, 4, false, null, [0x21, 0x01, 0xC8, 0x00, 0x01, 0x0F, 0x00], 0, 0);
        using var r = vol.OpenReader();
        var (bytes, _, _, _) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, Path.Combine(_dest, "c.txt"), CancellationToken.None);
        Assert.Equal(9, bytes);
        Assert.Equal("ABCABCABC", File.ReadAllText(Path.Combine(_dest, "c.txt")));
    }

    [Fact]
    public void Clusters_reused_since_the_scan_downgrade_the_grade()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 64);
        vol.SetAllocated(300, false); vol.SetAllocated(301, true);
        var rec = new UndeleteRecord(32, 1, 5, 1, "d.bin", false, 8192, 8192, 8192, 0, 0, false, null, [0x21, 0x02, 0x2C, 0x01, 0x00], 0, 0);
        using var r = vol.OpenReader();
        var (_, _, grade, _) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, Path.Combine(_dest, "d.bin"), CancellationToken.None);
        Assert.Equal(RecoveryGrade.Partial, grade);
    }

    [Fact]
    public void Resident_data_and_timestamps()
    {
        var vol = new SyntheticVolume();
        var created = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var rec = new UndeleteRecord(33, 1, 5, 1, "r.txt", false, 5, 5, 0, 0, 0, true, "hello!!"u8.ToArray(), null, created.Ticks, created.Ticks);
        using var r = vol.OpenReader();
        var path = Path.Combine(_dest, "r.txt");
        var (bytes, _, _, runs) = UndeleteWriter.Recover(r, ClusterBitmap.Load(r), rec, RecoveryGrade.Excellent, path, CancellationToken.None);
        Assert.Equal(5, bytes); Assert.Equal("hello", File.ReadAllText(path)); Assert.Equal(created, File.GetLastWriteTimeUtc(path)); Assert.Empty(runs);
    }
}
```
DFXML test addition (Core): a `RecoveredFile` with `Runs = [new(0, 8192, 409600)]` produces `<byte_runs><byte_run file_offset="0" len="8192" img_offset="409600" /></byte_runs>`.
- [ ] **Step 2:** FAIL. **Step 3: Implement** writer + models + export; in `RecoverySession.RecoverAsync` add `RecoverySource.Undelete when c.Key is UndeleteRecord u` → `Task.Run(() => UndeleteWriter.Recover(Reader, Bitmap, u, c.Grade, UniquePath(Path.Combine(folder, c.Name)), ct))` and build the `RecoveredFile` (with `Runs`); `UndeleteAsync` = `Task.Run(() => new MftUndeleteSource(Reader, Bitmap, liveIndex, Log?.Entries).Scan(progress, ct))` where `liveIndex` is `manager.Volumes` for the drive. Directory candidates recover their contents by resolving children through the scan result (`UndeleteRecord.ParentRecordNo == dir.RecordNo && ParentSequence == dir.Sequence`) into a folder of the same name. **Step 4:** PASS. **Step 5:** Commit `feat(indexing): undelete recovery with sparse, compressed and re-checked runs; DFXML byte runs`.

---

### Task 7: Shadow copies — enumerate, read versions, save, restore in place, freeze (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/ShadowCopies.cs`, `src/FileHound.Indexing/Recovery/ShadowCopySource.cs`
- Modify: `src/FileHound.Indexing/FileHound.Indexing.csproj` (`<PackageReference Include="System.Management" Version="10.0.0" />`), `BlockSources.cs` (`ShadowBlockSource.TryOpen` uses `ShadowCopies.List`), `RecoverySession.cs` (`ShadowCopy` recovery and restore)
- Test: `tests/FileHound.Indexing.Tests/ShadowCopyTests.cs`

**Interfaces:**
```csharp
public sealed record ShadowCopy(string Id, string Device, DateTime CreatedUtc, string VolumeName, char? Letter);
public static class ShadowCopies
{
    public static IReadOnlyList<ShadowCopy> List(char letter);               // WMI Win32_ShadowCopy (ManagementObjectSearcher) → on failure vssadmin list shadows; newest first; not elevated → empty
    internal static IReadOnlyList<ShadowCopy> ParseVssadmin(string output);  // pure: "Shadow Copy ID", "Original Volume: (C:)…", "Shadow Copy Volume", "Creation Time" (en-US and invariant parse; unparsable → MinValue)
    public static string? Create(char letter);                                // Win32_ShadowCopy.Create(Volume = "C:\", Context = "ClientAccessible") → new ShadowID; null + logged reason on failure
}
public sealed record ShadowVersion(ShadowCopy Snapshot, string OriginalPath, string SnapshotPath, long Size, DateTime ModifiedUtc, bool IsDirectory);
public static class ShadowCopySource
{
    public static IReadOnlyList<ShadowVersion> Versions(char letter, string path);          // every snapshot where <device>\<relative> exists, newest first; distinct by (size, mtime) marked with Detail "same as newer version" kept but flagged
    public static IReadOnlyList<ShadowVersion> ListDirectory(ShadowVersion dir);
    public static string SaveTo(ShadowVersion v, string destinationFolder);                 // copy; UniquePath " (recovered)"
    public static string RestoreInPlace(ShadowVersion v);                                   // next to the original: "name (from 2026-10-08 1530).ext", never overwrites
    internal static string RestoreName(string originalPath, DateTime createdLocal);
}
```
- [ ] **Step 1: Tests**
```csharp
public class ShadowCopyTests
{
    private const string Sample = """
        Contents of shadow copy set ID: {1}
           Contained 1 shadow copies at creation time: 10/5/2026 9:12:33 PM
              Shadow Copy ID: {aaaa-1}
                 Original Volume: (C:)\\?\Volume{v1}\
                 Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6
        Contents of shadow copy set ID: {2}
           Contained 1 shadow copies at creation time: 10/7/2026 8:00:00 AM
              Shadow Copy ID: {bbbb-2}
                 Original Volume: (E:)\\?\Volume{v2}\
                 Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy9
        """;

    [Fact]
    public void Parses_vssadmin_output()
    {
        var all = ShadowCopies.ParseVssadmin(Sample);
        Assert.Equal(2, all.Count);
        var c = all.Single(s => s.Letter == 'C');
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6", c.Device);
        Assert.Equal("{aaaa-1}", c.Id);
        Assert.Equal(new DateTime(2026, 10, 5, 21, 12, 33), c.CreatedUtc.ToLocalTime());
    }

    [Fact]
    public void Restore_name_never_overwrites() =>
        Assert.Equal(@"C:\d\report (from 2026-10-08 1530).docx", ShadowCopySource.RestoreName(@"C:\d\report.docx", new DateTime(2026, 10, 8, 15, 30, 0)));

    [Fact, Trait("Category", "Elevated")]
    public void Reads_a_previous_version_of_hosts_when_a_snapshot_exists()
    {
        if (!Elevation.IsElevated) return;
        var versions = ShadowCopySource.Versions('C', @"C:\Windows\System32\drivers\etc\hosts");
        if (ShadowCopies.List('C').Count == 0) { Assert.Empty(versions); return; }
        Assert.NotEmpty(versions);
        Assert.True(versions[0].Size > 0);
    }
}
```
- [ ] **Step 2:** FAIL. **Step 3: Implement.** WMI: `new ManagementObjectSearcher("root\\cimv2", "SELECT ID, DeviceObject, InstallDate, VolumeName FROM Win32_ShadowCopy")`; map `VolumeName` (`\\?\Volume{GUID}\`) to the letter via `Kernel32.VolumeGuidPathOf($"{letter}:\\")`; `InstallDate` through `ManagementDateTimeConverter.ToDateTime`. Create: `new ManagementClass("Win32_ShadowCopy").InvokeMethod("Create", new object[] { $"{letter}:\\", "ClientAccessible" })` → return value 0 means success, `ShadowID` out-param. Versions: `relative = path[3..]`; for each snapshot `Path.Combine(device, relative)` with `File.Exists`/`Directory.Exists` (prefix `\\?\GLOBALROOT` is accepted by .NET). **Step 4:** PASS. **Step 5:** Commit `feat(indexing): shadow copy source — versions, save, restore in place, freeze`.

---

### Task 8: Undelete and Previous versions tabs (App)

**Files:**
- Create: `src/FileHound.App/ViewModels/Recovery/UndeleteTabViewModel.cs`, `PreviousVersionsTabViewModel.cs`
- Modify: `RecoveryViewModel.cs` (tabs `Undelete`, `PreviousVersions`; `IsRestoreTab` true for PreviousVersions; `Recover` dispatches; `IsScanning`/`ScanProgressText` for the header chip), `RecoveryItem.cs` (`Describe` for Undelete grades incl. `Detail`), `MainViewModel.cs` (status chip shows `Scanning… 42%` in butter while `Recovery.IsScanning`; clicking it navigates to Recovery), `Views/RecoveryView.xaml(.cs)` (enable the two chips; two panels), `tests/FileHound.App.Tests/RecoveryViewModelTests.cs`
- UI spec: §5.3 (Undelete: Scan/Rescan button with the sniffing hound at 48 px while scanning, filter, sort chips *Best grade / Newest / Name / Size*, collapsed "Why some files can't come back" card, selection footer "Estimated intact: N%"), §5.4 (Previous versions: path box + versions list, *Freeze this drive now* with the exact confirm copy, empty states), §6 (status column after recovery).

**Interfaces:**
```csharp
public enum RecoveryTab { RecycleBin, Deleted, Undelete, PreviousVersions }
public enum UndeleteSort { BestGrade, Newest, Name, Size }
public sealed partial class UndeleteTabViewModel : ObservableObject
{
    public UndeleteTabViewModel(Func<RecoverySession, IProgress<UndeleteProgress>, CancellationToken, Task<IReadOnlyList<RecoveryCandidate>>>? scanner = null);
    public ObservableCollection<RecoveryItem> Items { get; }
    [ObservableProperty] string _filter; [ObservableProperty] UndeleteSort _sort; [ObservableProperty] bool _isScanning; [ObservableProperty] double _progress; [ObservableProperty] string _progressText;
    [ObservableProperty] bool _hasScanned; [ObservableProperty] int _count; [ObservableProperty] string? _error; [ObservableProperty] bool _whyExpanded;
    public string EstimatedIntactText { get; }        // "Estimated intact: 92%" over the selection (size-weighted)
    public IAsyncRelayCommand ScanCommand { get; }     // Scan / Rescan
    public IReadOnlyList<RecoveryItem> Selected { get; }  public event EventHandler? SelectionChanged;
    public Task LoadAsync(RecoverySession? session);   // resets; does not scan
}
public sealed partial class PreviousVersionsTabViewModel : ObservableObject
{
    public PreviousVersionsTabViewModel(Func<char, string, IReadOnlyList<ShadowVersion>>? versions = null, Func<char, string?>? freeze = null);
    [ObservableProperty] string _path; [ObservableProperty] string _snapshotsText;  // "3 snapshots (newest 2026-10-07 08:00)" / "No snapshots exist for C:"
    public ObservableCollection<RecoveryItem> Items { get; }   // one row per version; Key = ShadowVersion; grade Excellent
    public IRelayCommand LookupCommand { get; }  public IAsyncRelayCommand FreezeCommand { get; }   // Freeze asks via Func<bool> confirm injected by the view
    public Func<bool>? ConfirmFreeze { get; set; }
}
```
- [ ] **Step 1: View-model tests**
```csharp
public class UndeleteTabViewModelTests
{
    private static RecoveryCandidate C(string name, RecoveryGrade g, int pct, long size) => new(RecoverySource.Undelete, name, @"C:\d", size, null, null, g, pct, false, null, new object());

    [Fact]
    public async Task Scan_fills_items_sorted_by_best_grade_and_reports_intact_estimate()
    {
        var vm = new UndeleteTabViewModel((_, _, _) => Task.FromResult<IReadOnlyList<RecoveryCandidate>>([C("b", RecoveryGrade.Partial, 40, 1000), C("a", RecoveryGrade.Excellent, 100, 3000)]));
        await vm.LoadAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasScanned); Assert.Equal(2, vm.Count);
        Assert.Equal("a", vm.Items[0].Name);
        vm.Items[0].IsSelected = true; vm.Items[1].IsSelected = true;
        Assert.Equal("Estimated intact: 85%", vm.EstimatedIntactText);   // (3000*100 + 1000*40) / 4000
    }

    [Fact]
    public async Task Scan_error_is_shown_not_thrown()
    {
        var vm = new UndeleteTabViewModel((_, _, _) => throw new NotSupportedException("No readable path to C:"));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Contains("No readable path", vm.Error);
        Assert.False(vm.IsScanning);
    }
}

public class PreviousVersionsTabViewModelTests
{
    [Fact]
    public void Lookup_lists_versions_newest_first_and_freeze_needs_confirmation()
    {
        var snap = new ShadowCopy("{1}", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), "", 'C');
        bool frozen = false;
        var vm = new PreviousVersionsTabViewModel(
            versions: (_, p) => [new ShadowVersion(snap, p, snap.Device + p[2..], 10, DateTime.UtcNow, false)],
            freeze: _ => { frozen = true; return "{new}"; }) { Path = @"C:\d\f.txt" };
        vm.LookupCommand.Execute(null);
        Assert.Single(vm.Items);
        Assert.Equal("Excellent", vm.Items[0].GradeKey);
        vm.ConfirmFreeze = () => false; vm.FreezeCommand.ExecuteAsync(null).Wait(); Assert.False(frozen);
        vm.ConfirmFreeze = () => true; vm.FreezeCommand.ExecuteAsync(null).Wait(); Assert.True(frozen);
    }
}
```
Plus in `RecoveryItemTests`: `Describe` for an Undelete candidate with `Detail = "Compressed (LZNT1)"` keeps the grade text and puts the detail in the tooltip.
- [ ] **Step 2:** FAIL. **Step 3: Implement** VMs, wire `RecoveryViewModel` (`Recover` for Undelete → `session.RecoverAsync`; for PreviousVersions → `ShadowCopySource.RestoreInPlace` when `IsRestoreTab`, `SaveTo` the destination otherwise — the footer offers both *Restore in place* and *Save to…* on that tab per UI §5.3/5.4: implement as the Restore button plus a secondary `SoftButton` "Save to another drive…"), `MainViewModel.UpdateStatus` scanning chip, XAML panels using the Stage A `CandidateRow`/`ColumnHeader` templates (Undelete adds a MODIFIED column by reusing `WhenText` = modified when no deleted time), the `Why some files can't come back` expander (four lines: SSD TRIM caveat verbatim, record reuse, overwritten clusters, encryption), the Freeze confirm `MessageBox` with `MessageBoxResult.Cancel` default, and the empty states. The *Scan* button shows `hound-sniff.png` 48 px while scanning. **Step 4:** PASS; `dotnet build -c Release` 0 warnings; `--snapshot` now also saves `recovery-undelete.png` and `recovery-versions.png` (extend `SnapshotMode`); review against UI §5.3/5.4. **Step 5:** Commit `feat(app): Undelete and Previous versions tabs`.

---

### Task 9: VHD acceptance test (Elevated) and real-world run

**Files:**
- Create: `tests/FileHound.Indexing.Tests/VhdAcceptanceTests.cs`, `tests/FileHound.Indexing.Tests/VirtualDisk.cs`

**Interfaces:**
```csharp
internal sealed class VirtualDisk : IDisposable   // diskpart: create vdisk file=… maximum=256 type=expandable; attach; create partition primary; format fs=ntfs quick label=FHTEST; assign letter=<free>
{
    public static VirtualDisk? Create(string vhdPath);   // null when diskpart is unavailable or fails (logged)
    public char Letter { get; }
    public void Dispose();                               // diskpart: select vdisk; detach vdisk; then delete the file
}
```
- [ ] **Step 1: Test**
```csharp
public class VhdAcceptanceTests
{
    [Fact, Trait("Category", "Elevated")]
    public void Undelete_round_trip_on_a_fresh_ntfs_volume()
    {
        if (!Elevation.IsElevated) return;
        using var vhd = VirtualDisk.Create(Path.Combine(Path.GetTempPath(), $"fh-{Guid.NewGuid():N}.vhdx"));
        if (vhd is null) return;   // diskpart missing: nothing to assert
        var root = $@"{vhd.Letter}:\";
        var rng = new Random(42);
        byte[] big = new byte[3_000_000]; rng.NextBytes(big);     // multi-run candidate
        byte[] small = new byte[600]; rng.NextBytes(small);        // resident candidate
        byte[] jpg = new byte[200_000]; rng.NextBytes(jpg); jpg[0] = 0xFF; jpg[1] = 0xD8; jpg[2] = 0xFF;
        Directory.CreateDirectory(root + "Photos");
        File.WriteAllBytes(root + @"Photos\holiday.jpg", jpg); File.WriteAllBytes(root + "big.bin", big); File.WriteAllBytes(root + "small.txt", small);
        File.WriteAllBytes(root + "victim.bin", big);
        var expected = new Dictionary<string, string> { ["holiday.jpg"] = Sha(jpg), ["big.bin"] = Sha(big), ["small.txt"] = Sha(small) };
        foreach (var n in new[] { @"Photos\holiday.jpg", "big.bin", "small.txt", "victim.bin" }) File.Delete(root + n);
        File.WriteAllBytes(root + "overwriter.bin", new byte[6_000_000]);   // likely lands on victim's clusters
        // Flush metadata so $MFT on disk reflects the deletes.
        using (var h = Kernel32.OpenVolume(vhd.Letter)) Assert.False(h.IsInvalid);
        Thread.Sleep(2000);

        var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == vhd.Letter);
        using var reader = VolumeReader.Open(drive);
        var bitmap = ClusterBitmap.Load(reader);
        var index = new MftScanner().Scan(drive, [], null, CancellationToken.None);
        var candidates = new MftUndeleteSource(reader, bitmap, index, null).Scan(null, CancellationToken.None);
        var dest = Directory.CreateTempSubdirectory("fh-vhd-out-").FullName;
        try
        {
            foreach (var (name, sha) in expected)
            {
                var c = candidates.Single(x => x.Name == name);
                Assert.True(c.Grade is RecoveryGrade.Excellent or RecoveryGrade.Good, $"{name}: {c.Grade} {c.Detail}");
                var (_, got, _, _) = UndeleteWriter.Recover(reader, bitmap, (UndeleteRecord)c.Key, c.Grade, Path.Combine(dest, name), CancellationToken.None);
                Assert.Equal(sha, got);
            }
            Assert.Equal(@$"{vhd.Letter}:\Photos", candidates.Single(x => x.Name == "holiday.jpg").OriginalFolder);
            var victim = candidates.Single(x => x.Name == "victim.bin");
            Assert.True(victim.Grade is RecoveryGrade.Partial or RecoveryGrade.Overwritten, victim.Grade.ToString());
        }
        finally { Directory.Delete(dest, true); }
    }
    private static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));
}
```
- [ ] **Step 2:** Implement `VirtualDisk` (diskpart via a temp script file; pick the first free letter from `Z` down; wait for the volume to appear with `DriveInfo`). **Step 3:** Run elevated (`--filter Category=Elevated`) with UAC; fix what fails (expected pitfalls: NTFS keeps deleted small files' records cached — the 2 s sleep plus the volume open flush covers it; `overwriter.bin` may not reuse victim's clusters on a fresh volume, hence the `Partial or Overwritten` assertion may need `or Excellent` with a note if NTFS allocates elsewhere — verify and document). **Step 4:** Real-world run: `dotnet run --project src/FileHound.App -c Release`, Enable Turbo, Recovery → Undelete on C: → Scan, recover one *Excellent* candidate to another drive, open it; Previous versions → look up `C:\Windows\System32\drivers\etc\hosts`. **Step 5:** Commit `test(indexing): VHD undelete round trip (elevated)`.

---

### Task 10: Docs, review, merge

- [ ] README: Recovery table rows for *Previous versions* and *Undelete* become real (needs Turbo; grades; read path); `### 1.3.0` changelog; bump `Directory.Build.props` to 1.3.0; CSV export note mentions DFXML byte runs; architecture block adds `VolumeReader`, `ClusterBitmap`, `MftUndeleteSource`, `UndeleteWriter`, `ShadowCopySource`, `Lznt1`.
- [ ] `dotnet build -c Release` 0 warnings; all tests; elevated tests under UAC.
- [ ] Code review (Code Reviewer agent) of `feat/recovery-b` vs `main` with the same brief shape as Stage A (data safety: no writes through `VolumeReader`; `RestoreInPlace` never overwrites; `Freeze` only after consent; writer bounds on hostile data runs — LCN beyond the volume, lengths overflowing; LZNT1 bounds; thread safety of session lazies; VM cancellation).
- [ ] Fix findings with tests, merge `feat/recovery-b` → `main` (no-ff), push both.

## Self-review notes
- FR-11…FR-16 → Tasks 1, 3–6; FR-17…FR-20 → Task 7–8; §6.2 → Task 3; §6.5 progress → Tasks 5, 8; FR-28 byte runs → Task 6; §9 VHD → Task 9; UI §5.3/5.4/6 → Task 8.
- Names used across tasks: `VolumeReader.ReadClusters/ReadBytes/ReadRecord/MftExtents/Geometry/ControlHandle`, `ClusterBitmap.Load/IsAllocated/Count`, `UndeleteRecord` positional order `(RecordNo, Sequence, ParentRecordNo, ParentSequence, Name, IsDirectory, RealSize, InitializedSize, AllocatedSize, DataFlags, CompressionUnit, DataIsResident, ResidentData, DataRunBytes, CreatedUtcTicks, ModifiedUtcTicks)`, `UndeleteWriter.Recover(reader, bitmap, record, gradeAtScan, destPath, ct)`, `ShadowCopies.List/ParseVssadmin/Create`, `ShadowCopySource.Versions/ListDirectory/SaveTo/RestoreInPlace/RestoreName`.
- Deviation from spec FR-17 noted: WMI is primary as specified; `vssadmin` parsing is the fallback and the unit-tested path.
