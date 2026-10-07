# Raw $MFT Reader Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task.

**Goal:** Make Turbo first scans faster by reading names, sizes and dates from the raw `$MFT` in one sequential pass, with an automatic fallback to `FSCTL_ENUM_USN_DATA`.
**Spec:** `docs/superpowers/specs/2026-10-07-raw-mft-reader-design.md`
**Tech:** C# 14 / .NET 10, unsafe spans, `[LibraryImport]`, `System.Threading.Channels`, xUnit.

## Global Constraints
- No new packages. Indexing stays warning-free (`TreatWarningsAsErrors`).
- The current enumeration scanner stays intact and remains the fallback.
- All existing tests stay green (206).
- Commit after each task with the `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` trailer.

### Task 1: Data runs and FILE record parsing
**Files:**
- Create: `src/FileHound.Indexing/Ntfs/DataRuns.cs`, `src/FileHound.Indexing/Ntfs/MftRecordParser.cs`
- Create (tests): `tests/FileHound.Indexing.Tests/MftRecordBuilder.cs` (test helper), `tests/FileHound.Indexing.Tests/MftRecordParserTests.cs`

**Produces:**
```csharp
public readonly record struct DataRun(long Vcn, long Lcn, long Clusters);   // Lcn = -1 → sparse
public static class DataRuns { public static List<DataRun> Decode(ReadOnlySpan<byte> runs); }
public static class MftRecordParser {
    public static bool ApplyFixups(Span<byte> record);                       // false → bad signature or torn
    public static bool TryParse(ReadOnlySpan<byte> record, out MftRecord r);  // after fixups
}
public ref struct MftRecord { bool InUse, IsDirectory, HasAttributeList, HasName, HasSize, HasStandardInfo;
    long BaseRecord, ParentRecord, Size, ModifiedUtcTicks; uint Attributes; ReadOnlySpan<char> Name;
    ReadOnlySpan<byte> UnnamedDataRuns; }  // runs only when $DATA is non-resident with StartVcn 0
```
- [ ] **Step 1:** Write failing tests covering:
  - data-run decoding: `21 18 34 56 | 11 10 F0 | 01 08 | 00` gives
    - (0, 0x5634, 0x18)
    - (0x18, 0x5624, 0x10)
    - (0x28, -1, 8)
  - the fixups round-trip, and a torn record is rejected;
  - SI, FN and resident `$DATA` produce the expected name, parent, size, mtime and attributes;
  - a DOS name listed first, where the Win32 name still wins;
  - non-resident `$DATA` (size and runs);
  - a named stream is ignored;
  - the directory flag;
  - a record not in use;
  - an extension record (`BaseRecord`);
  - the attribute-list flag;
  - a truncated or oversized attribute length is rejected without throwing.
- [ ] **Step 2:** Run the tests and see them fail. **Step 3:** Implement. **Step 4:** Run the tests and see them pass. **Step 5:** Commit.

### Task 2: RawMftReader core (record stream → VolumeIndex)
**Files:**
- Create: `src/FileHound.Indexing/Ntfs/RawMftReader.cs`
- Test: `tests/FileHound.Indexing.Tests/RawMftReaderTests.cs`

**Produces:**
```csharp
public sealed class RawMftReader {
    // Disk entry point (elevated): volume data → record 0 → extents → pipelined reads → index.
    public VolumeIndex Read(SafeFileHandle volume, DriveDescriptor drive, IProgress<ScanProgress>? progress, CancellationToken ct);
    // Testable core: feed record-aligned chunks, then Build.
    internal sealed class Accumulator { Accumulator(string root, int recordSize, int capacityHint);
        void AddChunk(Span<byte> chunk, long firstRecordNo); VolumeIndex Build(); }
    public TimeSpan IoTime { get; } public TimeSpan ParseTime { get; } public TimeSpan BuildTime { get; }
}
```
- [ ] **Step 1:** Write failing tests that build a synthetic MFT (record size 1024) and check:
  - record 5 is the root (named "."); a folder 64 has parent 5, and file 65 has parent 64 with size 1234 and an mtime;
  - a metafile `$MFT` (record 0) and `$Extend` children are dropped;
  - a record not in use is skipped;
  - an extension record 70 supplies the name of base record 66, which has an attribute list and no name;
  - a torn record is skipped;
  - the resulting paths and sizes are correct, and every live entry has `MetadataKnown`.
- [ ] **Steps 2–5:** Fail, implement, pass, commit.

### Task 3: Disk reading, scanner integration and IndexManager fill decision
**Files:**
- Modify: `src/FileHound.Indexing/Interop/Kernel32.cs` (`ReadFile`, `SetFilePointerEx`)
- Modify: `src/FileHound.Indexing/Ntfs/MftScanner.cs` (try raw first, fall back to enumeration; expose `UsedRawReader`)
- Modify: `src/FileHound.Indexing/IndexManager.cs` (`needFill = HasIncompleteMetadata(v)`, `onlyIncomplete: true`)
- Modify: `tools/FileHound.Cli/Program.cs` (`turbo-bench`: raw vs enumeration timings, counts, size agreement)

- [ ] **Step 1:** Implement `RawMftReader.Read`:
  - read volume data at offset 64 (`MftStartLcn`);
  - read and parse record 0, then decode its extents;
  - read with a pipeline (reader `Task` plus `Channel<(byte[] Buffer, int Length, long FirstRecord)>` from an `ArrayPool` of 4 MB buffers).
- [ ] **Step 2:** Wire it into `MftScanner` with the fallback, then build and run all tests. They must stay green.
- [ ] **Step 3:** Run `turbo-bench` elevated (one UAC prompt) and record raw vs enumeration timings, counts and size agreement.
- [ ] **Step 4:** Update the README timings, commit and push.
