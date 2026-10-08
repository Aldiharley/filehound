# File Recovery — Stage A Implementation Plan (Recycle Bin, Recently deleted, page shell, exports)

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the Recovery page with its first two sources — Recycle Bin (list + restore + recover-to) and Recently deleted (a USN-journal deletion log with a live "slot still free" check) — plus CSV/DFXML export and the read-only session rules, so a user can get back most recently deleted files without any raw disk reads.

**Architecture:** Core gets pure parsers/models (`RecycleBinMetadata`, `RecoveryCandidate`, `DeletionLogStore`, exports). Indexing gets the Win32 sources (`RecycleBinSource`, `DeletionLog` fed by a new `UsnUpdater` event, `JournalGapOracle`) and a `RecoverySession` that owns write suspension. The App gets `AppPage.Recovery`, `RecoveryViewModel` with tab view models, and `RecoveryView` in the existing clay style. Stages B (undelete, previous versions) and C (carving) are planned separately and plug into the same page.

**Tech Stack:** .NET 10 / C# 14, WPF + CommunityToolkit.Mvvm, xUnit, existing `Kernel32` interop, `System.IO.Hashing`.

**Specs:** `docs/superpowers/specs/2026-10-08-file-recovery-design.md` (FR-x), `docs/superpowers/specs/2026-10-08-file-recovery-uiux.md` (UI §x).

## Global Constraints
- Recovery code never writes to a source volume except the explicit Recycle Bin *Restore* (move `$R` back in place). All volume/journal handles are `GENERIC_READ`.
- Undelete/Deep scan destinations must be a different volume GUID; Recycle Bin *Restore* goes to the original location (FR-26). Stage A implements the destination rule in the footer for *Recover to…*.
- `TreatWarningsAsErrors` stays on in Core and Indexing; the Release build must have 0 warnings.
- UI uses only existing tokens/styles from `Themes/*.xaml` plus the grade chip colors in UI §2; no new third-party packages.
- Grade copy and the SSD caveat use the exact wording in UI §8.
- Commit after every task with the trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## File Structure
```
src/FileHound.Core/Recovery/
  RecoveryModels.cs          RecoverySource, RecoveryGrade, RecoveryCandidate, RecoveredFile
  RecycleBinMetadata.cs      $I v1/v2 parser (pure)
  DeletionLogStore.cs        ring buffer + binary persistence of DeletionEntry
  Exports.cs                 CsvExport, DfxmlExport
src/FileHound.Indexing/Recovery/
  RecycleBinSource.cs        enumerate bins, restore/copy
  DeletionLog.cs             subscribes to UsnUpdater.Deleted, backfills, persists via DeletionLogStore
  JournalGapOracle.cs        FSCTL_GET_NTFS_FILE_RECORD "slot still free?" check
  RecoverySession.cs         per-drive session: sources, write suspension, destination rule
src/FileHound.Indexing/Ntfs/UsnUpdater.cs      + Deleted event with pre-removal context
src/FileHound.Indexing/IndexManager.cs         + SuspendWrites/ResumeWrites, + access to UsnUpdater per drive
src/FileHound.App/ViewModels/Recovery/         RecoveryViewModel, RecycleBinTabViewModel, DeletedTabViewModel, RecoveryItem
src/FileHound.App/Views/RecoveryView.xaml(.cs)
src/FileHound.App/Assets/                      hound-dig.png, hound-found.png, icon-recycle/timeline/undelete/shadow/scan/shield/export.png
tests/FileHound.Core.Tests/RecoveryTests.cs
tests/FileHound.Indexing.Tests/RecoveryTests.cs
```

---

### Task 1: Recovery models and `$I` parser (Core)

**Files:**
- Create: `src/FileHound.Core/Recovery/RecoveryModels.cs`, `src/FileHound.Core/Recovery/RecycleBinMetadata.cs`
- Test: `tests/FileHound.Core.Tests/RecoveryTests.cs`

**Produces:**
```csharp
namespace FileHound.Core.Recovery;
public enum RecoverySource { RecycleBin, DeletionLog, Undelete, ShadowCopy, Carving }
public enum RecoveryGrade { Excellent, Good, Partial, Overwritten, Zeroed, Encrypted, Unknown }
public sealed record RecoveryCandidate(RecoverySource Source, string Name, string? OriginalFolder, long Size, DateTime? ModifiedUtc,
    DateTime? DeletedUtc, RecoveryGrade Grade, int PercentIntact, bool IsDirectory, string? Detail, object Key)
{ public string OriginalPath => OriginalFolder is null ? Name : Path.Combine(OriginalFolder, Name); }
public sealed record RecoveredFile(RecoveryCandidate Candidate, string RecoveredPath, long Bytes, string Sha256Hex, RecoveryGrade FinalGrade, string? Error);
public sealed record RecycleBinMetadata(int Version, long Size, DateTime DeletedUtc, string OriginalPath)
{
    public static bool TryParse(ReadOnlySpan<byte> bytes, out RecycleBinMetadata? meta);
}
```
`TryParse` rules: v2 = u64 version 2 @0, u64 size @8, FILETIME @16, u32 chars incl. NUL @24, UTF-16 path @28 (trim the NUL, reject if chars ≤ 1 or 28+2·chars > length). v1 = u64 version 1, size, FILETIME, 520-byte NUL-padded UTF-16 path @24 (length must be ≥ 544). Reject other versions, out-of-range FILETIME, or empty paths. Size for folders is the total.

- [ ] **Step 1: Write the failing tests**
```csharp
using FileHound.Core.Recovery;
public class RecycleBinMetadataTests
{
    private static byte[] V2(string path, long size, DateTime deleted)
    {
        var b = new byte[28 + (path.Length + 1) * 2];
        BitConverter.GetBytes(2L).CopyTo(b, 0);
        BitConverter.GetBytes(size).CopyTo(b, 8);
        BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(b, 16);
        BitConverter.GetBytes((uint)(path.Length + 1)).CopyTo(b, 24);
        System.Text.Encoding.Unicode.GetBytes(path).CopyTo(b, 28);
        return b;
    }
    [Fact] public void Parses_v2()
    {
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(RecycleBinMetadata.TryParse(V2(@"C:\Users\x\report.docx", 523551, when), out var m));
        Assert.Equal(2, m!.Version); Assert.Equal(523551, m.Size); Assert.Equal(when, m.DeletedUtc); Assert.Equal(@"C:\Users\x\report.docx", m.OriginalPath);
    }
    [Fact] public void Parses_real_header_seen_on_this_pc()
    {   // "02 00 00 00 00 00 00 00 1f fa 07 00 00 00 00 00 70 68 3c 00 52 2d dd 01 56 00 00 00 43 00 3a 00 ..."
        var b = V2(@"C:\" + new string('a', 82), 0x7FA1F, DateTime.FromFileTimeUtc(0x01DD2D52003C6870));
        Assert.True(RecycleBinMetadata.TryParse(b, out var m)); Assert.Equal(200, b.Length); Assert.Equal(0x7FA1F, m!.Size);
    }
    [Fact] public void Parses_v1()
    {
        var b = new byte[544]; BitConverter.GetBytes(1L).CopyTo(b, 0); BitConverter.GetBytes(77L).CopyTo(b, 8);
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 16); System.Text.Encoding.Unicode.GetBytes(@"D:\old.txt").CopyTo(b, 24);
        Assert.True(RecycleBinMetadata.TryParse(b, out var m)); Assert.Equal(1, m!.Version); Assert.Equal(@"D:\old.txt", m.OriginalPath);
    }
    [Theory] [InlineData(0)] [InlineData(10)] [InlineData(27)]
    public void Rejects_truncated(int len) => Assert.False(RecycleBinMetadata.TryParse(new byte[len], out _));
    [Fact] public void Rejects_bad_version() { var b = V2(@"C:\a", 1, DateTime.UtcNow); BitConverter.GetBytes(9L).CopyTo(b, 0); Assert.False(RecycleBinMetadata.TryParse(b, out _)); }
    [Fact] public void Rejects_oversized_char_count() { var b = V2(@"C:\a", 1, DateTime.UtcNow); BitConverter.GetBytes(9999u).CopyTo(b, 24); Assert.False(RecycleBinMetadata.TryParse(b, out _)); }
    [Fact] public void Candidate_original_path_joins() => Assert.Equal(@"C:\d\f.txt", new RecoveryCandidate(RecoverySource.RecycleBin, "f.txt", @"C:\d", 1, null, null, RecoveryGrade.Excellent, 100, false, null, 0).OriginalPath);
}
```
- [ ] **Step 2:** `dotnet test tests/FileHound.Core.Tests --filter RecycleBinMetadataTests` → compile FAIL.
- [ ] **Step 3:** Implement both files (parser with `BinaryPrimitives`, `DateTime.FromFileTimeUtc` guarded by `0 < ft < DateTime.MaxValue.ToFileTimeUtc()`).
- [ ] **Step 4:** Run → PASS. **Step 5:** Commit `feat(core): recovery models and Recycle Bin $I parser`.

### Task 2: Deletion log store (Core)

**Files:**
- Create: `src/FileHound.Core/Recovery/DeletionLogStore.cs`
- Test: `tests/FileHound.Core.Tests/RecoveryTests.cs` (append)

**Produces:**
```csharp
public enum DeletionKind : byte { Deleted, Recycled, Replaced }
public sealed record DeletionEntry(long RecordNo, ushort Sequence, long ParentRecordNo, string Name, string ParentPath, long Size,
    bool IsDirectory, long ModifiedUtcTicks, long DeletedUtcTicks, DeletionKind Kind, long Usn);
public sealed class DeletionLogStore
{
    public DeletionLogStore(int capacity = 50_000);
    public int Count { get; } public long LastUsn { get; }
    public void Add(DeletionEntry e);                       // ring buffer, drops oldest; keeps entries sorted by insertion
    public bool TryMark(long recordNo, ushort sequence, DeletionKind kind);   // Recycled/Replaced retag by FRN (most recent match)
    public IReadOnlyList<DeletionEntry> Snapshot();          // newest first
    public void Save(string path);                           // FHDL magic, version, count, entries, XxHash64 trailer; atomic tmp+move
    public static DeletionLogStore Load(string path, int capacity = 50_000);  // empty store on missing/corrupt
}
```
- [ ] **Step 1: Write the failing tests**
```csharp
public class DeletionLogStoreTests
{
    static DeletionEntry E(long rec, string name, long usn, DeletionKind k = DeletionKind.Deleted) => new(rec, 3, 5, name, @"C:\Docs", 10, false, 1, 2, k, usn);
    [Fact] public void Ring_buffer_drops_oldest() { var s = new DeletionLogStore(3); for (int i = 0; i < 5; i++) s.Add(E(i, $"f{i}", i)); Assert.Equal(3, s.Count); Assert.Equal(["f4", "f3", "f2"], s.Snapshot().Select(e => e.Name)); Assert.Equal(4, s.LastUsn); }
    [Fact] public void Mark_retags_latest_matching_frn() { var s = new DeletionLogStore(); s.Add(E(7, "a", 1)); s.Add(E(7, "a", 2)); Assert.True(s.TryMark(7, 3, DeletionKind.Recycled)); Assert.Equal(DeletionKind.Recycled, s.Snapshot()[0].Kind); Assert.Equal(DeletionKind.Deleted, s.Snapshot()[1].Kind); Assert.False(s.TryMark(7, 4, DeletionKind.Recycled)); }
    [Fact] public void Round_trips_and_rejects_corruption()
    {
        var dir = Directory.CreateTempSubdirectory("fh-dlog-"); var p = Path.Combine(dir.FullName, "C_1.dlog");
        try
        {
            var s = new DeletionLogStore(); s.Add(E(1, "one.txt", 10)); s.Add(E(2, "two", 11, DeletionKind.Recycled)); s.Save(p);
            var l = DeletionLogStore.Load(p); Assert.Equal(2, l.Count); Assert.Equal("two", l.Snapshot()[0].Name); Assert.Equal(DeletionKind.Recycled, l.Snapshot()[0].Kind); Assert.Equal(11, l.LastUsn);
            var b = File.ReadAllBytes(p); b[b.Length / 2] ^= 1; File.WriteAllBytes(p, b); Assert.Equal(0, DeletionLogStore.Load(p).Count);
            Assert.Equal(0, DeletionLogStore.Load(Path.Combine(dir.FullName, "none.dlog")).Count);
        }
        finally { dir.Delete(true); }
    }
}
```
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement with a `DeletionEntry[]` ring, `BinaryWriter`/`BinaryReader`, `XxHash64` trailer (same pattern as `SnapshotSerializer`). **Step 4:** PASS. **Step 5:** Commit `feat(core): deletion log store with persistence`.

### Task 3: CSV and DFXML export (Core)

**Files:**
- Create: `src/FileHound.Core/Recovery/Exports.cs`
- Test: append to `tests/FileHound.Core.Tests/RecoveryTests.cs`

**Produces:**
```csharp
public static class CsvExport
{
    public static void WriteCandidates(TextWriter w, IEnumerable<RecoveryCandidate> items);   // header: source,name,original_folder,size,modified_utc,deleted_utc,grade,percent_intact,is_directory,detail
    public static void WriteDeletions(TextWriter w, IEnumerable<DeletionEntry> items);       // header: deleted_utc,name,parent_path,size,is_directory,kind,record_no,sequence,usn
    public static void WriteManifest(TextWriter w, IEnumerable<RecoveredFile> items);        // header: original_path,recovered_path,size,sha256,grade,source,error
}
public static class DfxmlExport
{
    public static void Write(TextWriter w, string volumeDescription, IEnumerable<RecoveredFile> items, DateTime startedUtc, DateTime finishedUtc);
}
```
CSV: RFC 4180 quoting (quote when the field contains `,` `"` CR/LF; double embedded quotes), UTF-8, `\n` line ends, ISO-8601 UTC timestamps. DFXML: `<dfxml version="1.2.0" xmlns="http://www.forensicswiki.org/wiki/Category:Digital_Forensics_XML">` with `<creator><program>FileHound</program><version/>`, `<source><image_filename>`, one `<fileobject>` per item with `<filename>`, `<filesize>`, `<mtime>` when known, `<hashdigest type="sha256">`, and `<byte_runs>` left empty in Stage A (Stage B fills them). Use `XmlWriter` with indentation.

- [ ] **Step 1: Write the failing tests**
```csharp
public class ExportTests
{
    [Fact] public void Csv_quotes_commas_and_quotes()
    {
        var sw = new StringWriter(); CsvExport.WriteCandidates(sw, [new RecoveryCandidate(RecoverySource.RecycleBin, "a,b \"q\".txt", @"C:\x", 5, null, new DateTime(2026,1,2,3,4,5,DateTimeKind.Utc), RecoveryGrade.Excellent, 100, false, null, 0)]);
        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("source,name,original_folder,size,modified_utc,deleted_utc,grade,percent_intact,is_directory,detail", lines[0]);
        Assert.Equal("RecycleBin,\"a,b \"\"q\"\".txt\",C:\\x,5,,2026-01-02T03:04:05Z,Excellent,100,false,", lines[1]);
    }
    [Fact] public void Dfxml_is_well_formed_with_hash()
    {
        var sw = new StringWriter(); var c = new RecoveryCandidate(RecoverySource.DeletionLog, "f.bin", @"C:\d", 3, new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc), null, RecoveryGrade.Good, 100, false, null, 0);
        DfxmlExport.Write(sw, @"\\.\C:", [new RecoveredFile(c, @"E:\r\f.bin", 3, "abc123", RecoveryGrade.Good, null)], DateTime.UtcNow, DateTime.UtcNow);
        var doc = System.Xml.Linq.XDocument.Parse(sw.ToString()); var ns = doc.Root!.Name.Namespace;
        Assert.Equal("dfxml", doc.Root.Name.LocalName); var fo = doc.Root.Element(ns + "fileobject")!;
        Assert.Equal(@"C:\d\f.bin", fo.Element(ns + "filename")!.Value); Assert.Equal("abc123", fo.Element(ns + "hashdigest")!.Value); Assert.Equal("sha256", fo.Element(ns + "hashdigest")!.Attribute("type")!.Value);
    }
}
```
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5:** Commit `feat(core): CSV and DFXML exports for recovery`.

### Task 4: `UsnUpdater.Deleted` event and `DeletionLog` (Indexing)

**Files:**
- Modify: `src/FileHound.Indexing/Ntfs/UsnUpdater.cs` (ApplyBuffer: raise `Deleted` before `Delete(e)`; detect `$Extend\$Deleted` marker renames and Recycle Bin renames)
- Create: `src/FileHound.Indexing/Recovery/DeletionLog.cs`
- Test: `tests/FileHound.Indexing.Tests/RecoveryTests.cs`

**Produces:**
```csharp
// UsnUpdater
public sealed record UsnDeletion(long RecordNo, ushort Sequence, long ParentRecordNo, string Name, string? ParentPath, long Size, bool IsDirectory, long ModifiedUtcTicks, long DeletedUtcTicks, long Usn, DeletionKind Kind);
public event EventHandler<UsnDeletion>? Deleted;   // raised while the index entry still exists (ParentPath resolved)
// DeletionLog
public sealed class DeletionLog : IDisposable
{
    public DeletionLog(UsnUpdater updater, string storePath);   // loads the store, subscribes, saves every 30 s when dirty and on Dispose
    public IReadOnlyList<DeletionEntry> Entries { get; }        // newest first
    public event EventHandler? Changed;
    public void SuspendPersistence(bool on);                     // FR-8/FR-29: keep in memory only during a session
}
```
Rules in `ApplyBuffer`: (a) `FILE_DELETE` with a known live entry → raise `Deleted(Kind=Deleted)` with `ParentPath = PathBuilder.GetParentPath` **before** `_index.Delete(e)`; sequence comes from the USN record's FRN high 16 bits (parser change: `UsnRecord.Sequence`). (b) `RENAME_NEW_NAME` whose new parent resolves to a path containing `\$Recycle.Bin\` or whose new name matches the POSIX marker (name is 24 hex chars whose first 16 hex equal the FRN, or `16hex:name`) → raise `Deleted` with `Kind=Recycled` or `Deleted` respectively (the entry is then removed from the index as today, because it left the tree). (c) `FILE_CREATE` with the same name+parent as a `Deleted` raised ≤ 2 s earlier → raise `Deleted(Kind=Replaced)` for that earlier FRN so the log can retag it (the log uses `TryMark`).

- [ ] **Step 1: Write the failing tests** (uses `UsnRecordParserTests.V2` helper; extend it with a `sequence` parameter default 3 encoded in the FRN high 16 bits)
```csharp
public class DeletionLogTests
{
    [Fact] public void Delete_raises_with_parent_path_before_removal()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo); index.SetRecord(0, 5);
        int docs = index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        int f = index.Add(docs, "report.docx", EntryFlags.MetadataKnown, 4321, 777, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1)); UsnDeletion? got = null; u.Deleted += (_, d) => got = d;
        u.ApplyBuffer(UsnRecordParserTests.V2(200, 100, "report.docx", UsnReason.FileDelete | UsnReason.Close, usn: 500, sequence: 3));
        Assert.NotNull(got); Assert.Equal(@"Q:\Docs", got!.ParentPath); Assert.Equal(4321, got.Size); Assert.Equal(777, got.ModifiedUtcTicks); Assert.Equal(3, got.Sequence); Assert.Equal(500, got.Usn); Assert.False(index.IsLive(f));
    }
    [Fact] public void Rename_into_recycle_bin_is_recycled()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo); index.SetRecord(0, 5);
        int bin = index.Add(0, "$Recycle.Bin", EntryFlags.Directory | EntryFlags.Hidden, 0, 0, recordNo: 30);
        int sid = index.Add(bin, "S-1-5-21-1", EntryFlags.Directory, 0, 0, recordNo: 31);
        int f = index.Add(0, "a.txt", 0, 1, 1, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1)); UsnDeletion? got = null; u.Deleted += (_, d) => got = d;
        u.ApplyBuffer(UsnRecordParserTests.V2(200, 31, "$RX1Y2Z3.txt", UsnReason.RenameNewName | UsnReason.Close));
        Assert.Equal(DeletionKind.Recycled, got!.Kind); Assert.Equal("a.txt", got.Name); Assert.Equal(@"Q:\", got.ParentPath);
    }
    [Fact] public void Posix_delete_marker_is_a_delete()
    {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo); index.SetRecord(0, 5);
        int ext = index.Add(0, "$Extend", EntryFlags.Directory, 0, 0, recordNo: 11); int del = index.Add(ext, "$Deleted", EntryFlags.Directory, 0, 0, recordNo: 12);
        int f = index.Add(0, "notes.md", 0, 9, 9, recordNo: 0x1234);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1)); UsnDeletion? got = null; u.Deleted += (_, d) => got = d;
        u.ApplyBuffer(UsnRecordParserTests.V2(0x1234, 12, "0000000000001234" + "00030000", UsnReason.RenameNewName | UsnReason.Close, sequence: 3));
        Assert.Equal(DeletionKind.Deleted, got!.Kind); Assert.Equal("notes.md", got.Name); Assert.False(index.IsLive(f));
    }
    [Fact] public void Log_persists_and_retags_replaced()
    {
        var dir = Directory.CreateTempSubdirectory("fh-dl-"); try {
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo); index.SetRecord(0, 5); int docs = index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        index.Add(docs, "x.txt", 0, 1, 1, recordNo: 200);
        using var u = new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1)); var store = Path.Combine(dir.FullName, "Q.dlog");
        using (var log = new DeletionLog(u, store))
        {
            u.ApplyBuffer(UsnRecordParserTests.V2(200, 100, "x.txt", UsnReason.FileDelete | UsnReason.Close, usn: 1));
            u.ApplyBuffer(UsnRecordParserTests.V2(201, 100, "x.txt", UsnReason.FileCreate | UsnReason.Close, usn: 2));
            Assert.Equal(DeletionKind.Replaced, log.Entries[0].Kind);
        }
        using var log2 = new DeletionLog(new UsnUpdater(index, 'Q', TimeSpan.FromSeconds(1)), store); Assert.Single(log2.Entries); Assert.Equal("x.txt", log2.Entries[0].Name);
        } finally { dir.Delete(true); }
    }
}
```
- [ ] **Step 2:** FAIL. **Step 3:** Implement (parser: expose `Sequence` from the FRN; `ApplyBuffer`: compute the deletion before mutating; POSIX marker check; recent-deletes dictionary keyed by (parent, name) with a 2 s window; `DeletionLog` with a `PeriodicTimer` save). **Step 4:** PASS, plus all existing Indexing tests. **Step 5:** Commit `feat(indexing): deletion log from the USN journal (delete, recycle, POSIX marker, replace)`.

### Task 5: Journal backfill, gap oracle, Recycle Bin source (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/JournalGapOracle.cs`, `src/FileHound.Indexing/Recovery/RecycleBinSource.cs`
- Modify: `src/FileHound.Indexing/Recovery/DeletionLog.cs` (`BackfillAsync`)
- Test: append to `tests/FileHound.Indexing.Tests/RecoveryTests.cs`

**Produces:**
```csharp
public static class JournalGapOracle
{
    public enum SlotState { Free, Reused, Unknown }
    public static SlotState Check(char letter, long recordNo);     // FSCTL_GET_NTFS_FILE_RECORD(recordNo): returned < recordNo → Free; == → Reused; failure → Unknown
}
public sealed class DeletionLog { public Task BackfillAsync(CancellationToken ct); }   // reads the journal from FirstUsn to the store's LastUsn, raising the same rules as live (elevated only; no-op otherwise)
public sealed record RecycleBinItem(char Drive, string Sid, string? Account, string MetadataPath, string? DataPath, RecycleBinMetadata? Meta, string DisplayName, bool IsDirectory, long Size);
public sealed class RecycleBinSource
{
    public static IReadOnlyList<RecycleBinItem> Enumerate(IEnumerable<DriveDescriptor> drives, bool allUsers);   // own SID unelevated; all SIDs when allUsers && elevated
    public static string Restore(RecycleBinItem item, bool keepBoth);     // move $R to original path (never replace; keepBoth → "name (restored).ext"), delete $I; returns the final path
    public static string CopyTo(RecycleBinItem item, string destinationFolder);   // copy $R (file or tree) to destination, original name; returns the path
    public static IEnumerable<RecoveryCandidate> ToCandidates(IEnumerable<RecycleBinItem> items);
}
```
- [ ] **Step 1: Write the failing tests.** Use a temp folder as a fake drive root containing `$Recycle.Bin\<currentSID>\` with `$I` files built by Task 1's `V2` helper and matching `$R` files: one file, one folder tree, one `$I` without `$R` (grade Unknown, detail "data file missing"), one `$R` without `$I` (detail "no metadata — original name unknown"). Assert `Enumerate` (via a `DriveDescriptor` whose Root is the temp folder) lists 4 items with the right display names/sizes; `Restore` moves the file to its original path (inside another temp folder used as the "original" location), refuses when the target exists unless `keepBoth`, and deletes the `$I`; `CopyTo` leaves the bin untouched. `JournalGapOracle.Check('C', 16)` returns `Reused` (record 16 is always in use) and `Check('C', long.MaxValue / 4)` returns `Free` — tagged `[Trait("Category","Elevated")]` since the FSCTL needs the volume handle.
- [ ] **Step 2:** FAIL. **Step 3:** Implement (`LookupAccountSid` via `[LibraryImport("advapi32.dll")]`; `Directory.Move`/`File.Move` with `overwrite:false`; `MoveFileEx` not needed). **Step 4:** PASS. **Step 5:** Commit `feat(indexing): Recycle Bin source, journal backfill and gap oracle`.

### Task 6: `RecoverySession` and write suspension (Indexing)

**Files:**
- Create: `src/FileHound.Indexing/Recovery/RecoverySession.cs`
- Modify: `src/FileHound.Indexing/IndexManager.cs` (`SuspendWrites(char)`, `ResumeWrites(char)`, `TryGetUsnUpdater(char, out UsnUpdater?)`, and `DeletionLog` ownership per Turbo slot so the log runs whenever the updater runs)
- Test: append to `tests/FileHound.Indexing.Tests/RecoveryTests.cs`

**Produces:**
```csharp
public sealed class RecoverySession : IDisposable
{
    public RecoverySession(IndexManager manager, DriveDescriptor drive, string dataDirectory);
    public DriveDescriptor Drive { get; }
    public bool IsElevated { get; }
    public DeletionLog? Log { get; }                       // null when not elevated / not Turbo
    public static bool IsDifferentVolume(string destinationFolder, char sourceLetter, out string reason);  // GetVolumeNameForVolumeMountPoint on both
    public Task<RecoveredFile> RecoverAsync(RecoveryCandidate c, string destinationFolder, CancellationToken ct);  // Stage A: RecycleBin (copy) and DeletionLog (not yet recoverable → Error "Needs undelete (coming in the next build)")
    public Task<string> RestoreAsync(RecoveryCandidate c, bool keepBoth);  // Recycle Bin in place
}
```
`IndexManager.SuspendWrites(letter)` makes `SaveSnapshotsAsync` skip that slot and `DeletionLog.SuspendPersistence(true)`; `ResumeWrites` reverses it and triggers a save. `RecoverAsync` writes to `<destination>\FileHound Recovery <yyyy-MM-dd HHmm>\`, hashes with SHA-256 while copying (`IncrementalHash`), and appends to `manifest.csv` (Task 3).

- [ ] **Step 1: Write the failing tests:** `IsDifferentVolume` returns false for a folder on the same drive letter and true for `%TEMP%` vs another letter (skip when only one volume exists); `SuspendWrites` prevents `SaveSnapshotsAsync` from touching that drive's `.fhx` (use the fake-drive `IndexOptions` from `IndexManagerTests`), `ResumeWrites` writes it; `RecoverAsync` on a Recycle Bin candidate copies to a temp destination and returns a 64-char SHA-256 that equals `SHA256.HashData(File.ReadAllBytes(...))`.
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS + all existing tests. **Step 5:** Commit `feat(indexing): recovery session with write suspension and hashed recovery`.

### Task 7: Assets (Higgsfield)

**Files:**
- Modify: `tools/assets/generate.ps1` (add the 9 assets from UI §2 to `$jobs`), `tools/assets/process.ps1` (resize entries)
- Create: `src/FileHound.App/Assets/hound-dig.png`, `hound-found.png`, `icon-recycle.png`, `icon-timeline.png`, `icon-undelete.png`, `icon-shadow.png`, `icon-scan.png`, `icon-shield.png`, `icon-export.png`

- [ ] **Step 1:** Add the jobs with the mascot/icon style preambles already in the script (hound-dig and hound-found reference `hound-hero.png`; icons reference the style sheet). Run `generate.ps1 -Only hound-dig,hound-found,icon-recycle,...` then `process.ps1`.
- [ ] **Step 2:** Review the PNGs (contact sheet), regenerate any off-style ones. **Step 3:** Commit `feat(app): recovery mascot poses and clay icons`.

### Task 8: Recovery page — view models (App)

**Files:**
- Create: `src/FileHound.App/ViewModels/Recovery/RecoveryItem.cs`, `RecycleBinTabViewModel.cs`, `DeletedTabViewModel.cs`, `RecoveryViewModel.cs`
- Modify: `src/FileHound.App/ViewModels/MainViewModel.cs` (`AppPage.Recovery`, `Recovery` property, title, status chip progress), `src/FileHound.App/App.xaml.cs` (composition)
- Test: `tests/FileHound.App.Tests/RecoveryViewModelTests.cs`

**Produces:**
```csharp
public sealed class RecoveryItem { RecoveryCandidate Candidate; string Name, Folder, SizeText, WhenText, GradeText, GradeKey ("Excellent"…"Unknown"), Detail; ImageSource? Icon; bool IsSelected; string? Status; string? Sha256; }
public sealed partial class RecoveryViewModel : ObservableObject
{
    ObservableCollection<DriveItem> Drives; DriveItem? SelectedDrive; string ReadPathText; bool IsElevated;
    RecoveryTab CurrentTab (RecycleBin | Deleted | …); int RecycleBinCount, DeletedCount;
    string? DestinationFolder; string? DestinationError; bool CanRecover; string RecoverButtonText;
    RecycleBinTabViewModel RecycleBin; DeletedTabViewModel Deleted;
    IRelayCommand PickDestination, Recover, OpenRecoveryFolder, ExportCsv, ExportDfxml;
    double Progress; string ProgressText; bool IsBusy;
}
```
Rules: changing `SelectedDrive` disposes the old `RecoverySession` and creates a new one; `DestinationFolder` is validated with `RecoverySession.IsDifferentVolume` → `DestinationError` (UI §4 wording) and `CanRecover`; `Recover` runs the selected items through `RecoverAsync`/`RestoreAsync` sequentially with progress; results update `Status`/`Sha256` and a success toast via `MainViewModel.ShowToast`. The Deleted tab applies the noise filter (FR-10 patterns) when `HideNoise` is on and computes `GradeKey` from `JournalGapOracle` lazily on a background thread, 200 rows at a time.

- [ ] **Step 1: Write the failing tests:** noise filter hides `~$x.docx`, `a.tmp`, `AppData\Local\Temp\…`, shows `report.docx`; destination on the source letter sets `DestinationError` and `CanRecover=false`; `RecoverButtonText` is "Restore 2" on the Recycle Bin tab with 2 selected and "Recover 2" on the Deleted tab; grade text mapping for each `RecoveryGrade` and `DeletionKind` (Recycled → "In Recycle Bin", Replaced → "Replaced").
- [ ] **Step 2:** FAIL. **Step 3:** Implement. **Step 4:** PASS. **Step 5:** Commit `feat(app): recovery view models`.

### Task 9: Recovery page — view (App)

**Files:**
- Create: `src/FileHound.App/Views/RecoveryView.xaml(.cs)`
- Modify: `src/FileHound.App/Views/MainWindow.xaml` (nav item, DataTemplate), `src/FileHound.App/Themes/Colors.xaml` (grade chip brushes: `GradeExcellent`, `GradePartial`, `GradeOverwritten`, `GradeZeroed`, `GradeUnknown` + text brushes per UI §2), `src/FileHound.App/Themes/Controls.xaml` (`GradeChip` style keyed by `GradeKey` via DataTriggers), `src/FileHound.App/Services/SnapshotMode.cs` (add the Recovery page)

- [ ] **Step 1:** Build the layout per UI §4–§5.2: drive & source strip, two tab chips (Recycle Bin, Recently deleted; the other three chips appear in Stages B/C), tab content card with toolbar/list/selection footer, sticky recovery footer with destination pill + Recover button + progress bar, empty states with `hound-dig.png`, the one-time journal banner, lock copy for non-elevated tabs with the Turbo button, and the results status column + `hound-found` toast.
- [ ] **Step 2:** `dotnet build` with 0 warnings; run `FileHound.exe --snapshot <dir>` and review `recovery.png` against the UI spec; fix spacing/contrast issues. Then run the app for real: open Recovery, restore one Recycle Bin item on E:, recover one to another drive, export CSV.
- [ ] **Step 3:** Commit `feat(app): Recovery page (Recycle Bin, Recently deleted)`.

### Task 10: Validation, docs, review

- [ ] **Step 1:** `dotnet build FileHound.sln -c Release` (0 warnings) and `dotnet test FileHound.sln -c Release` all green; run the `Elevated` tests via a UAC-approved `dotnet test … --filter Category=Elevated` for the gap oracle and backfill.
- [ ] **Step 2:** End-to-end on this PC: delete a file on E: normally (Recycle Bin) and with Shift+Delete; verify both appear in the right tabs, restore the first, confirm the index reflects the restore; export DFXML of the session and validate it parses.
- [ ] **Step 3:** README: a *Recovery* section (what each source can and can't do, the different-drive rule, the SSD caveat) and a `### 1.2.0` changelog entry; bump `<Version>` to 1.2.0 only when Stage A is merged.
- [ ] **Step 4:** Request code review (requesting-code-review skill) on the Stage A range; fix findings; merge `feat/recovery` → `main`; push.

---

## Stages B and C (outline — expanded into their own plans when Stage A is merged)

**Stage B — Undelete + Previous versions:** `VolumeReader` (volume → physical disk → shadow device; probe-verified on C:), `ClusterBitmap` (FSCTL first, `$Bitmap` record 6 through the reader otherwise), parser extensions (sequence numbers, four timestamps, attribute flags, initialized size), `RawMftReader.Accumulator` deleted-record sink, `MftUndeleteSource` with path reconstruction and grades, `Lznt1` (DiscUtils, MIT), `ShadowCopySource` (WMI enumeration, GLOBALROOT reads, restore-in-place, Freeze with consent), DFXML byte_runs, the Undelete and Previous versions tabs, a VHD-based Elevated acceptance test (format, create, delete, overwrite, recover, compare hashes).

**Stage C — Deep scan:** `Signatures` + validators (JPEG, PNG, GIF, BMP, TIFF, WebP, ISO-BMFF, MKV, RIFF, FLAC, OGG, MP3, PDF, ZIP/OOXML/ODF/EPUB, 7z, RAR, GZIP, SQLite, PE, OLE2, RTF, PST, LNK), `Carver` over free clusters with pause/resume, de-duplication against undelete, previews, consent card, the Deep scan tab, and synthetic-image tests per validator.
