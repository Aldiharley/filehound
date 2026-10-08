# FileHound — Deleted-File Recovery Design Spec

**Date:** 2026-10-08 · **Status:** Approved in brainstorming, pending written-spec review
**Companion docs:**
- UI/UX spec — `2026-10-08-file-recovery-uiux.md`
- Research — `docs/research/2026-10-08-deleted-file-recovery-research.md`, `docs/research/2026-10-08-recovery-reference-repos.md`
- Existing architecture — `2026-10-07-filehound-architecture.md`, `2026-10-07-raw-mft-reader-design.md`

## 1. Summary

A new **Recovery** page lets the user get deleted files back from their own NTFS drives. It draws on five sources, cheapest and most reliable first, and is honest about what can and cannot come back:

| Source | Needs admin | What it gives |
|---|---|---|
| **Recycle Bin** | own bin: no · other users' bins: yes | Full original path, deletion time, guaranteed restore |
| **Recently deleted** (USN journal) | yes | Name, folder and time of every delete in the journal's history, with a live "slot still free?" check |
| **Previous versions** (Volume Shadow Copies) | yes | Earlier copies of a path, when snapshots exist |
| **Undelete** (not-in-use MFT records) | yes | Name, path, size, dates; recoverability scored against the free-space bitmap and content |
| **Deep scan** (signature carving of free space) | yes | Files whose records are gone: type, size, preview, no name |

Forensic extras: CSV and DFXML export of the deletion timeline and recovery results, SHA-256 of every recovered file (shown and written to a manifest), and a documented read-only guarantee.

## 2. Goals

| # | Goal | Target |
|---|---|---|
| R1 | Recycle Bin listing covers every bin FileHound can read | Own bin on every NTFS drive unelevated; all users' bins when elevated |
| R2 | Recycle Bin restore is lossless | `$R` moved back to the recorded path, original timestamps preserved, never overwrites |
| R3 | Recently-deleted list reaches back as far as the journal | On this PC's C: that is hours to days; entries persist across FileHound restarts |
| R4 | Undelete recovers a file deleted minutes ago on an idle data drive | Byte-identical (SHA-256) on the VHD acceptance test |
| R5 | Undelete is honest | Each candidate carries a recoverability grade; "overwritten" and "zeroed (TRIM)" are detected, not guessed |
| R6 | Nothing is written to a source volume during recovery | Read-only handles only; FileHound's own index/log writes to that volume are suspended |
| R7 | Carving finds the common types | JPEG, PNG, GIF, PDF, ZIP/Office, MP4/MOV, MP3, 7z, RAR, SQLite, PE at least; validated size, not header-only |
| R8 | Works on C: even though raw volume reads are blocked there | Probe (2026-10-08): `ReadFile` on `\\.\C:` and `FSCTL_GET_VOLUME_BITMAP` fail with error 50; `\\.\PhysicalDrive0` at the partition offset and the shadow-copy device both read fine. The design uses those. |

## 3. Non-goals (v1)

- Forensic evidence handling: write blocking, chain of custody, legal reports.
- Fragment reassembly in carving (contiguous carving only; MFT undelete handles fragmentation because the data runs are known).
- Recovering EFS-encrypted files, FAT/exFAT/ReFS volumes, network shares, or deleted alternate data streams.
- Creating shadow copies on the user's behalf. Existing snapshots are used; creating one writes to the source volume, so it is only offered as a clearly labelled advanced action ("Freeze this drive now").
- Repairing damaged volumes or MFTs.

## 4. Key scenarios

1. **"I emptied the bin an hour ago."** Recovery → Recently deleted shows `budget.xlsx` deleted at 14:02 from `Documents`, badge *Recoverable*. The user picks a destination on E:, clicks Recover, and gets the file with a green SHA-256 line.
2. **"It's in the bin."** Recovery → Recycle Bin lists it with the original folder; Restore puts it back; FileHound's index picks the file up again through the journal.
3. **"Deleted it last week, lots of use since."** Undelete lists the record with grade *Partially overwritten (40%)*; the user is told why and offered Deep scan, which finds a JPEG of the right size and shows its preview.
4. **"Which version did I have on Monday?"** Previous versions lists the snapshot copies of the path with dates and sizes; the user saves one next to the current file.
5. **"Prove what happened."** The user exports the deletion timeline as CSV and the recovery session as DFXML with hashes.

## 5. Functional requirements

### 5.1 Recycle Bin (`RecycleBinSource`)
- **FR-1** Enumerate `X:\$Recycle.Bin\<SID>\` on every ready NTFS drive. Unelevated: the current user's SID only. Elevated: all SIDs; each is mapped to an account name via `LookupAccountSid` for display.
- **FR-2** Parse `$I` files, version 1 (544 bytes, fixed 260-char path) and version 2 (u64 version, u64 size, FILETIME deleted, u32 path chars incl. NUL, UTF-16 path). Items whose `$R` is missing are listed as *metadata only*; `$R` files without `$I` are listed as *orphaned* with their current name and no original path.
- **FR-3** Restore moves `$R` to the recorded path with `MoveFileEx` without replace, recreating missing parent folders, then deletes `$I`. If the target exists, the user chooses *Keep both* (`name (restored).ext`) or cancels. Folders restore with their whole tree.
- **FR-4** Recover-to-other-drive copies `$R` instead of moving it (the bin entry stays).

### 5.2 Recently deleted (`DeletionLog`)
- **FR-5** Hook the existing `UsnUpdater`: on `FILE_DELETE|CLOSE`, or on `RENAME_NEW_NAME` into a `\$Extend\$Deleted` marker (Windows 11 POSIX delete, see research §StepWind), capture `{name, FRN with sequence, parent FRN, parent path resolved now, size, directory flag, modified time, deletion timestamp}` **before** the index entry is removed.
- **FR-6** A later `RENAME_NEW_NAME` for the same FRN into `$Recycle.Bin\<SID>` tags the entry *In Recycle Bin* and links it to its `$I`. A later `FILE_CREATE` with the same name and parent within 2 s tags it *Replaced by a new version* (hidden by default).
- **FR-7** On startup and on *Refresh*, backfill from the journal's `FirstUsn` so deletes that happened while FileHound was not running are listed.
- **FR-8** Persist the log per drive in `%LOCALAPPDATA%\FileHound\recovery\{letter}_{serial}.dlog` (ring buffer, 50,000 entries, binary, XxHash64-checked like snapshots). While a recovery session is open on that drive, the log is kept in memory only (R6).
- **FR-9** Each entry's state is computed lazily with the gap oracle: `FSCTL_GET_NTFS_FILE_RECORD(record)` returning a lower record number means the slot is still free → *Recoverable (slot intact)*; returning the same number means reused → *Record reused — try Deep scan*. Entries that are directories show the count of children captured.
- **FR-10** Default noise filter (toggle): `*.tmp`, `~$*`, `*.etl`, `*.log`, `*.part`, `*.crdownload`, browser cache folders, `AppData\Local\Temp`, `$Recycle.Bin`, `System Volume Information`, `\$Extend`.

### 5.3 Undelete (`MftUndeleteSource`)
- **FR-11** Enumerate not-in-use FILE records with the existing parser. Accepted when: signature `FILE`, fixups valid, in-use flag clear, base record (ref @0x20 == 0), record number ≥ 16, at least one non-DOS `$FILE_NAME`. The parser is extended with: the record's sequence number (@0x10), the parent reference's sequence (high 16 bits), the four `$FILE_NAME` timestamps, non-resident attribute flags (compressed 0x0001, encrypted 0x4000, sparse 0x8000), initialized size (@0x38), and `$FILE_NAME` real size (@0x30).
- **FR-12** Record bytes come from the **volume reader** (§6.2), which picks the first path that works: the volume handle, the physical disk at the partition offset, or a shadow-copy device.
- **FR-13** Path reconstruction: resolve the parent through the live index when the parent record is in use and its sequence matches the reference; recurse through deleted parent records when their sequence equals the reference's (or +1); otherwise show `<unknown folder>\name` and try the deletion log (FR-5) for the last known parent. Bounded depth 64; stops at record 5 (root).
- **FR-14** Recoverability grade, computed from `$Bitmap` (read as file record 6's `$DATA` through the volume reader; `FSCTL_GET_VOLUME_BITMAP` is tried first) and a content check:
  - *Resident* → **Excellent** (data is inside the record)
  - all clusters free, first cluster passes the type check (signature for known extensions, not all zeros) → **Excellent**
  - all clusters free, first cluster all zeros while initialized size > 0 → **Zeroed (SSD TRIM)**
  - some clusters allocated → **Partially overwritten (N %)**
  - all allocated → **Overwritten**
  - encrypted flag → **Encrypted (cannot recover)**
  - compressed flag → **Compressed** (recovered through the LZNT1 decoder; graded like non-resident)
- **FR-15** Recovery reads the data runs through the volume reader, truncates to the real size, zero-fills sparse runs, decompresses LZNT1 compression units, writes to the destination, sets the original timestamps, and computes SHA-256 while writing. Just before copying, the chosen runs' bitmap bits and the first cluster's hash are re-checked; a change downgrades the grade and is shown.
- **FR-16** De-duplication: a candidate whose record and sequence match a *Recoverable* entry in the deletion log is shown once, with the log's richer path.

### 5.4 Previous versions (`ShadowCopySource`)
- **FR-17** Enumerate snapshots with WMI `Win32_ShadowCopy` (fallback: parse `vssadmin list shadows`), mapped to drive letters through `GetVolumePathNamesForVolumeName`. Requires elevation; otherwise the tab explains that.
- **FR-18** For a path the user types or picks from any other tab, open `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopyN\<relative path>` in each snapshot, newest first, and list the versions that exist with date and size (a directory path lists its entries).
- **FR-19** Save a version to a chosen destination, or *Restore in place* as `name (from <date>).ext` next to the current file (never overwriting).
- **FR-20** *Freeze this drive now* creates a snapshot via `Win32_ShadowCopy.Create` after an explicit warning that it writes to the drive. It is off the main flow (an advanced button in the tab's header).

### 5.5 Deep scan (`CarvingSource`)
- **FR-21** Scan only clusters that are free in `$Bitmap`, at cluster boundaries, through the volume reader in 4 MB pipelined chunks. Dispatch on the first byte, then compare full magics.
- **FR-22** Validators are pure `ReadOnlySpan<byte>` parsers that return the exact size or reject: JPEG (marker walk to EOI), PNG (chunk walk with CRC), GIF, BMP, TIFF, WebP, ISO-BMFF (MP4/MOV/M4A/HEIC, box walk), MKV (EBML), WAV/AVI (RIFF), FLAC, OGG, MP3 (frame walk), PDF (last `%%EOF`), ZIP incl. OOXML/ODF/EPUB subtype, 7z, RAR, GZIP, SQLite, PE, OLE2, RTF, PST, LNK. Signature table from Scalpel (Apache-2.0) / Foremost (public domain) conventions; nothing from PhotoRec.
- **FR-23** Results carry type, validated size, start LCN, and are de-duplicated against undelete candidates whose first run starts at the same LCN (the named record wins). Previews: images via WPF decoders from memory (capped at 50 MB), text snippet, media dimensions/duration from headers, ZIP entry list.
- **FR-24** Recovered names: `<type>_<LCN>.<ext>` in a `FileHound Recovery <date>` folder on the destination.
- **FR-25** The scan can be paused, resumed and cancelled; progress is bytes scanned / free bytes with an ETA.

### 5.6 Common recovery rules
- **FR-26** Destination: for Undelete and Deep scan, the destination volume GUID must differ from the source's (`GetVolumeNameForVolumeMountPoint`); the picker refuses the source volume with an explanation. Recycle Bin restore and shadow-copy restore-in-place write to the original location.
- **FR-27** Every recovered file gets a SHA-256, shown in the results list and appended to `manifest.csv` in the recovery folder (`original path, recovered path, size, sha256, grade, source`).
- **FR-28** Export: the Recently deleted list and any results list as CSV; a recovery session as DFXML (one `fileobject` per file, `byte_run`s from data runs, hashes).
- **FR-29** While the Recovery page has an active source drive: FileHound's snapshot saves, log writes and preview temp files for that drive are suspended (`IndexManager.SuspendWrites(letter)`); logs go to the other-drive data folder if the source is the system drive. This is released when the page is left.
- **FR-30** First use of an elevated source shows a one-time explanation: what is scanned ("all free space on C:, including data deleted by other user accounts"), the SSD/TRIM caveat, and "use the PC as little as possible until you've recovered what you need".

## 6. Architecture

### 6.1 Projects and layout
```
src/FileHound.Core/Recovery/        pure logic, no Win32
  Carving/   Signatures.cs, Validators/*.cs (one per format), Carver.cs
  Lznt1.cs                          LZNT1 decoder (adapted from DiscUtils, MIT — credited in THIRD-PARTY-NOTICES)
  RecoveryGrade.cs, RecoveryCandidate.cs, Dfxml.cs, CsvExport.cs, Sha256Stream.cs
src/FileHound.Indexing/Recovery/    Win32
  VolumeReader.cs                   read path selection (volume → physical disk → shadow device)
  ClusterBitmap.cs                  FSCTL_GET_VOLUME_BITMAP or $Bitmap via VolumeReader
  RecycleBinSource.cs, DeletionLog.cs, MftUndeleteSource.cs, ShadowCopySource.cs, CarvingSource.cs
  RecoveryWriter.cs                 destination rules, timestamps, SHA-256, manifest
  RecoverySession.cs                orchestrates sources for one drive, owns write suspension
src/FileHound.App/                  Recovery page (see UI/UX spec)
```
`MftRecordParser` gains the fields listed in FR-11; `RawMftReader.Accumulator` gains an optional sink for not-in-use records so one pass serves both indexing and undelete.

### 6.2 `VolumeReader`
```csharp
public sealed class VolumeReader : IDisposable
{
    public static VolumeReader Open(DriveDescriptor drive, bool allowShadow = true);  // tries paths in order, remembers the winner
    public VolumeReadPath Path { get; }            // Volume | PhysicalDisk | ShadowCopy
    public long BytesPerCluster { get; } public int RecordSize { get; } public long MftStartLcn { get; }
    public void ReadClusters(long lcn, int count, Span<byte> dest);
    public void ReadBytes(long volumeOffset, Span<byte> dest);           // sector-aligned
    public SafeFileHandle ControlHandle { get; }   // \\.\X: for FSCTLs (always works)
}
```
- **Volume:** `\\.\X:`, `GENERIC_READ`, backup semantics. Tested with one 4 KB read at the MFT start.
- **PhysicalDisk:** `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` → disk number and `StartingOffset` (refuse more than one extent); `\\.\PhysicalDriveN` with `FILE_FLAG_NO_BUFFERING`, 4 KB-aligned buffers; the first sector must carry `NTFS` at offset 3 (a `-FVE-FS-` sector means BitLocker ciphertext → path rejected).
- **ShadowCopy:** the newest existing snapshot device for the volume, opened as a block device. Point-in-time, so it is also the preferred path for carving when available. Only used when `allowShadow`; it does not read newer deletions than the snapshot.
- All handles are `GENERIC_READ` with `FILE_SHARE_READ|FILE_SHARE_WRITE`; the class exposes no write API.

### 6.3 `RecoverySession`
One per drive; created when the Recovery page selects a drive and disposed when the page is left or the drive changes. It owns the `VolumeReader`, the `ClusterBitmap` (loaded lazily, re-read on demand), the elevated-feature consent flag, and `IndexManager.SuspendWrites`. Sources are created from it and share its reader.

### 6.4 Data model
```csharp
public enum RecoverySource { RecycleBin, DeletionLog, Undelete, ShadowCopy, Carving }
public enum RecoveryGrade { Excellent, Good, Partial, Overwritten, Zeroed, Encrypted, Unknown }
public sealed record RecoveryCandidate(
    RecoverySource Source, string Name, string? OriginalFolder, long Size, DateTime? Modified, DateTime? Deleted,
    RecoveryGrade Grade, int PercentIntact, bool IsDirectory, string? Detail,   // "Record reused", "In Recycle Bin", ...
    object Key);                                                                  // source-specific handle for recovery
```

### 6.5 Threading and progress
Scans run on the thread pool with `IProgress<ScanProgress>` and a `CancellationToken`; results stream into the UI in batches of 200 through the dispatcher. Carving reports bytes/free bytes; undelete reports records/total; the deletion log is instant.

## 7. Error handling
- A read path that fails mid-scan (device removed, snapshot deleted) ends the scan with a message and keeps the results so far.
- Access denied on another user's bin or a shadow device → the row says why instead of vanishing.
- Destination full or unwritable → the file is marked *Failed* with the Win32 message; the session continues.
- A source volume going offline closes the session cleanly.
- Everything is logged to the normal log (which is relocated off the source drive per FR-29).

## 8. Security and privacy
- `asInvoker` manifest unchanged; elevated sources require the existing Turbo relaunch and show the FR-30 consent text once.
- No data leaves the machine. Hashes and manifests are written only to the user's chosen destination.
- Recovered executables are written with the Mark-of-the-Web zone identifier (`Zone.Identifier` = 3) so SmartScreen treats them as untrusted.

## 9. Testing
| Layer | Tests |
|---|---|
| Core | `$I` v1/v2 parser; LZNT1 against known vectors; each carving validator on synthetic files (valid, truncated, garbage-tailed); DFXML/CSV writers; grade rules |
| Indexing | Deleted-record parsing via `MftRecordBuilder` (not-in-use, sequence numbers, timestamps, flags, parent sequence mismatch); `ClusterBitmap` chunked reads on a fake; deletion-log capture from synthetic USN buffers incl. POSIX-delete marker and Recycle Bin rename; `VolumeReader` path selection with injected probes |
| Elevated (Category=Elevated, VHD) | `New-VHD` 256 MB → format NTFS → create files → delete some → overwrite some → run undelete and carving through production classes → compare SHA-256; Recycle Bin restore round trip; shadow-copy read when a snapshot exists |
| App | ViewModel state machine for sources/destination/consent; destination refusal rule |
| Manual | Recovery page screenshots per tab via `--snapshot`; a real deleted-file recovery on E: and on C: |

## 10. Delivery stages
1. **Stage A — Recycle Bin + Recently deleted + page shell + exports.** No raw reads; works on every drive.
2. **Stage B — Undelete + Previous versions + SHA-256 manifest + write suspension.** Needs the `VolumeReader` and bitmap.
3. **Stage C — Deep scan (carving) + previews.**
Each stage is complete and tested on its own. Tabs for stages not yet built are simply absent from the page, so no build ever shows a placeholder.
