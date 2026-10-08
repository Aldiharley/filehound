# File Recovery — Stage C Implementation Plan (Deep scan: signature carving + previews)

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the last recovery source — **Deep scan** — which reads the drive's free clusters through `VolumeReader`, recognises files by signature with pure validators that return an exact size, de-duplicates against Undelete, previews results, and recovers them as `<type>_<LCN>.<ext>`; with pause/resume/cancel, an ETA, and the one-time FR-30 consent card.

**Architecture:** Core gets a `Carving` namespace: `CarveType` (id, label, extension, category, magics, max size, validator), `Signatures` (the table + first-byte dispatch) and one validator per format (pure `ReadOnlySpan<byte>` → `CarveResult {Ok(size, info) | Reject | NeedMore}`); `NeedMore` lets the carver grow the window instead of validators ever seeing more than they need. Indexing gets `Carver`: walks free-cluster runs from `ClusterBitmap` in 4 MB chunks through `VolumeReader`, dispatches on the first byte of every cluster, validates with a window that grows on `NeedMore` up to the type's max or the end of the contiguous free run, streams `CarvedFile`s in batches, and supports pause/resume/cancel with bytes-scanned progress. `RecoverySession` recovers a carved file by reading its range. The App gets `DeepScanTabViewModel` (consent, scan controls, type chips, sort, preview pane) and `PreviewService` (image decode from memory capped at 50 MB, text snippet, ZIP entry list, info strings from validators).

**Tech Stack:** .NET 10 / C# 14, WPF + CommunityToolkit.Mvvm, xUnit, `System.IO.Hashing` (CRC-32 for PNG), existing `VolumeReader`/`ClusterBitmap`/`RecoverySession`.

**Specs:** design FR-21…FR-25, FR-30, §6.5, §8 (Mark-of-the-Web on recovered executables); UI §5.5, §6, §8.

## Global Constraints
- Carving reads only free clusters, only through `VolumeReader`; it never writes to the source volume. Recovered files go to a different volume (`IsDifferentVolume`), named `<type>_<LCN>.<ext>`.
- Validators are pure, allocation-light span parsers with no I/O and a hard upper bound on work per call (bounded loops, every length checked against the span: `NeedMore` when the span ends early, `Reject` when the structure is wrong). No validator may read past the span or loop on a zero-length element.
- Signature conventions follow Scalpel (Apache-2.0) / Foremost (public domain); nothing from PhotoRec/TSK.
- Recovered `.exe`/`.dll`/`.scr`/`.msi`/`.bat`/`.cmd`/`.ps1`/`.js`/`.vbs`/`.lnk` get `Zone.Identifier` = 3 (§8).
- FR-30 consent text verbatim: *"Deep scan reads all free space on {X}:, including data deleted by other user accounts. On SSDs, Windows tells the drive to discard deleted data within seconds, so recent deletions may already be gone. Use the PC as little as possible until you've recovered what you need."* Shown once per install (`AppSettings.DeepScanConsented`).
- `TreatWarningsAsErrors` stays on; Release build 0 warnings; commit per task with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## File Structure
```
src/FileHound.Core/Carving/
  CarveTypes.cs            CarveStatus, CarveResult, CarveValidator delegate, CarveType record
  Signatures.cs            the table + ByFirstByte dispatch + AnyOffset types (ISO-BMFF)
  ImageValidators.cs       Jpeg, Png, Gif, Bmp, Tiff, WebP
  MediaValidators.cs       Riff (WAV/AVI), IsoBmff (MP4/MOV/M4A/HEIC), Mkv (EBML), Ogg, Mp3, Flac
  DocumentValidators.cs    Pdf, Zip (+ OOXML/ODF/EPUB/JAR subtype), SevenZip, Rar, Gzip (bounded), Sqlite, Pe, Ole2, Rtf, Pst, Lnk
  ZipListing.cs            central-directory entry names for previews
src/FileHound.Indexing/Recovery/
  Carver.cs                free-space walk, dispatch, window growth, pause/resume, progress, batches
  CarvedFile.cs            the Key for Carving candidates
  RecoverySession.cs       + CarveAsync, RecoverCarved (+ Zone.Identifier), dedup with _lastUndelete
src/FileHound.App/
  Services/PreviewService.cs, Services/SettingsService.cs (+ DeepScanConsented)
  ViewModels/Recovery/DeepScanTabViewModel.cs, RecoveryViewModel.cs (+ tab), RecoveryItem.cs (+ carving fields)
  Views/RecoveryView.xaml(.cs)  Deep scan panel, consent card, preview pane; SnapshotMode + recovery-deepscan.png
tests/FileHound.Core.Tests/Carving/ImageValidatorTests.cs, MediaValidatorTests.cs, DocumentValidatorTests.cs, SignaturesTests.cs, SyntheticFiles.cs
tests/FileHound.Indexing.Tests/CarverTests.cs (synthetic volume), VhdAcceptanceTests.cs (+ carve)
tests/FileHound.App.Tests/RecoveryViewModelTests.cs (+ deep scan VM)
```

---

### Task 1: Validator framework and image validators (Core)

**Files:** Create `src/FileHound.Core/Carving/CarveTypes.cs`, `Signatures.cs` (images only for now), `ImageValidators.cs`; Test `tests/FileHound.Core.Tests/Carving/SyntheticFiles.cs`, `ImageValidatorTests.cs`, `SignaturesTests.cs`.

**Interfaces:**
```csharp
namespace FileHound.Core.Carving;
public enum CarveStatus { Ok, Reject, NeedMore }
public readonly record struct CarveResult(CarveStatus Status, long Size, string? Info)
{
    public static CarveResult Ok(long size, string? info = null); public static readonly CarveResult Reject; public static readonly CarveResult NeedMore;
}
public delegate CarveResult CarveValidator(ReadOnlySpan<byte> data);
public sealed record CarveType(string Id, string Label, string Extension, FileCategory Category, long MaxSize, int MagicOffset, byte[][] Magics, CarveValidator Validate)
{ public bool MatchesMagic(ReadOnlySpan<byte> data); }
public static class Signatures
{
    public static IReadOnlyList<CarveType> All { get; }
    public static IReadOnlyList<CarveType> ByFirstByte(byte b);      // types with MagicOffset 0 whose magic starts with b
    public static IReadOnlyList<CarveType> AtOffset { get; }          // MagicOffset > 0 (ISO-BMFF): checked at every cluster
    public static CarveType? ById(string id);
}
public static class ImageValidators { public static CarveResult Jpeg(ReadOnlySpan<byte> d); Png; Gif; Bmp; Tiff; WebP; }
```
Rules: JPEG = marker walk (SOI; segments with BE length; SOS entropy scan to a marker other than `FF00`/RST; EOI → size; `Info` = "W×H" from SOF). PNG = signature + chunk walk with CRC-32 check per chunk; IEND → size; Info from IHDR. GIF = header + LSD (+GCT) + blocks (image descriptor with LCT and LZW sub-blocks, extensions with sub-blocks) until trailer `3B`. BMP = `BM` + file size @2 (≥ 54, DIB header size ∈ {12,40,52,56,108,124}); Info from DIB. TIFF = `II*\0`/`MM\0*` + IFD walk (≤ 64 IFDs, ≤ 4096 entries each): size = max extent over out-of-line values and StripOffsets+StripByteCounts / TileOffsets+TileByteCounts; `NeedMore` when an extent lies past the span. WebP = `RIFF` size `WEBP` → size + 8; Info from VP8/VP8L/VP8X. Max sizes: JPEG 64 MB, PNG 64 MB, GIF 32 MB, BMP 128 MB, TIFF 512 MB, WebP 64 MB.

- [ ] **Step 1: Synthetic files helper** (`SyntheticFiles.cs`): `Jpeg(w, h, entropyBytes)`, `Png(w, h, badCrc = false)`, `Gif(w, h)`, `Bmp(w, h)`, `Tiff(stripBytes)`, `WebP(w, h)` — all built by hand with correct lengths/CRCs.
- [ ] **Step 2: Tests** — for each format: valid → `Ok` with exact size and info; truncated (cut 10 bytes before the end) → `NeedMore`; garbage appended → `Ok` with the original size; corrupt structure (bad PNG CRC, JPEG segment length past the span then a non-marker, GIF missing trailer with wrong block) → `Reject`/`NeedMore` as appropriate. `SignaturesTests`: every type's `Magics` dispatch through `ByFirstByte`; `MatchesMagic` true for its own sample and false for the others; ids are unique.
- [ ] **Step 3:** FAIL → implement → PASS. **Step 4:** Commit `feat(core): carving framework and image validators`.

---

### Task 2: Media validators (Core)

**Files:** Create `MediaValidators.cs`; extend `Signatures.cs`; Test `MediaValidatorTests.cs` (+ `SyntheticFiles` builders).

Rules: **RIFF** (`RIFF` size `WAVE`/`AVI `) → size + 8, Info "PCM 44.1 kHz stereo" from `fmt ` / "W×H" from `avih`. **ISO-BMFF** (`ftyp` @4): box walk (32-bit size, 1 = 64-bit largesize, 0 = to end → Reject at top level unless `mdat` is last), accept only known top-level boxes (`ftyp moov mdat free skip wide meta uuid moof mfra sidx styp`), size = end of last box; Info = duration from `mvhd` + dimensions from the first `tkhd`. **MKV/WebM** (`1A45DFA3`): EBML header, then the Segment element — unknown-size segments → walk children; size = Segment end; Info duration from `Info/Duration` × TimecodeScale. **OGG** (`OggS`): page walk (27-byte header, segment table) until the EOS flag page; `NeedMore` otherwise. **MP3**: optional ID3v2 tag (sync-safe size), then frames (version/layer/bitrate/samplerate tables; frame length formula) walked until a non-frame byte; require ≥ 4 valid consecutive frames; size = end of last valid frame (+128 if an `TAG` ID3v1 follows); Info = bitrate/sample rate. **FLAC** (`fLaC`): metadata blocks (STREAMINFO required); frames: sync `FFF8`/`FFF9` at frame starts with a valid header CRC-8 — walk frames while the sync+CRC-8 holds; size = last valid frame end; Info from STREAMINFO (sample rate, channels, total samples → duration). Max sizes 2 GB for MP4/MKV/AVI, 512 MB others.

- [ ] Tests per format (valid, truncated, garbage tail, wrong structure); commit `feat(core): media validators (RIFF, ISO-BMFF, MKV, OGG, MP3, FLAC)`.

---

### Task 3: Document and archive validators (Core)

**Files:** Create `DocumentValidators.cs`, `ZipListing.cs`; extend `Signatures.cs`; Test `DocumentValidatorTests.cs`.

Rules: **PDF** (`%PDF-`): size = position after the last `%%EOF` (+ optional CR/LF) within max size; `NeedMore` until one is found; Info = "/Pages N" if `/Type /Pages` with `/Count N` appears. **ZIP** (`PK\3\4`): local headers walked (sizes from the header; data-descriptor flag → scan for the next `PK\3\4`/`PK\1\2` signature), then central directory to the EOCD record; size = EOCD end (+ comment); subtype from the first entry name: `[Content_Types].xml` → OOXML (`.docx`/`.xlsx`/`.pptx` by `word/`/`xl/`/`ppt/`), `mimetype` → ODF/EPUB by its content, `META-INF/MANIFEST.MF` → JAR; `ZipListing.Entries(span)` lists names from the central directory. **7z** (`7z\xBC\xAF\x27\x1C`): size = 32 + NextHeaderOffset + NextHeaderSize. **RAR** (`Rar!\x1A\x07\x00` v4 / `…\x01\x00` v5): block walk to the end-of-archive block. **GZIP** (`1F 8B 08`): no size field, so inflate through `System.IO.Compression.DeflateStream` over the span (bounded 256 MB) to find the end (+8 trailer). **SQLite** (`SQLite format 3\0`): page size @16 (65536 when 1) × page count @28 → size; Info "N pages". **PE** (`MZ`, `PE\0\0` at e_lfanew): size = max(section PointerToRawData + SizeOfRawData, certificate table end); Info = "x64 DLL" etc. **OLE2** (`D0CF11E0A1B11AE1`): sector size from header; walk the FAT (DIFAT in header + DIFAT sectors) counting non-free sectors; size = 512 + usedSectors × sectorSize; subtype `.doc`/`.xls`/`.ppt`/`.msg` from directory entry names (`WordDocument`, `Workbook`/`Book`, `PowerPoint Document`, `__properties_version1.0`). **RTF** (`{\rtf`): brace depth walk with `\'xx` and `\\` escapes until depth 0. **PST** (`!BDN`): version @10 (14/15 ANSI, 23 Unicode) → file size @0xA8 (u32) or @0xB8 (u64). **LNK** (`4C 00 00 00` + CLSID `00021401-0000-0000-C000-000000000046`): header 76, LinkTargetIDList (flag 0x01, u16 size), LinkInfo (flag 0x02, u32 size), string data (flags 0x04..0x40, u16 count × 2 if Unicode flag 0x80 else × 1), extra data blocks (u32 size ≥ 4) until a terminal block < 4.

- [ ] Tests per format incl. OOXML subtype, ZIP listing, PE with two sections, OLE2 with three FAT entries; commit `feat(core): document and archive validators`.

---

### Task 4: `Carver` (Indexing)

**Files:** Create `src/FileHound.Indexing/Recovery/CarvedFile.cs`, `Carver.cs`; Test `tests/FileHound.Indexing.Tests/CarverTests.cs`.

**Interfaces:**
```csharp
public sealed record CarvedFile(CarveType Type, long StartLcn, long Size, string? Info)
{ public string SuggestedName => $"{Type.Id}_{StartLcn}{Type.Extension}"; }
public sealed record CarveProgress(long BytesScanned, long FreeBytes, int Found, TimeSpan? Eta);
public sealed class Carver(VolumeReader reader, ClusterBitmap bitmap)
{
    public int ChunkClusters { get; init; }                         // 4 MB / cluster
    public IReadOnlyCollection<string>? TypeFilter { get; init; }   // null = all
    public event Action<IReadOnlyList<CarvedFile>>? Batch;          // ≤ 200 per raise, from the scan thread
    public void Pause(); public void Resume(); public bool IsPaused { get; }
    public List<CarvedFile> Run(IProgress<CarveProgress>? progress, CancellationToken ct);
    internal static IEnumerable<(long Lcn, long Clusters)> FreeRuns(ClusterBitmap bitmap);   // contiguous free runs
}
```
Algorithm: for each free run, read it in chunks (ChunkClusters, pipelined: one read-ahead task); for each cluster start in the chunk, look up `Signatures.ByFirstByte(b)` and `AtOffset`; for each type whose magic matches: validate over the window `[clusterStart, min(chunkEnd, start + MaxSize)]`; on `NeedMore` and more free run remains, re-read a larger window (double, up to `MaxSize` and the run end) and retry; on `Ok(size)`: emit, and skip the clusters the file covers (cluster-aligned) so nested matches (a JPEG thumbnail inside a JPEG) are not re-emitted; one hit per cluster (first type in table order wins). Progress every 200 ms: bytes/free bytes, found, ETA from the rolling rate. Pause = `ManualResetEventSlim` checked between chunks.

- [ ] Tests on `SyntheticVolume` (1024 clusters): plant a PNG at LCN 300 (free), a JPEG at 400 spanning two clusters with garbage after it, a PNG at 500 inside an *allocated* cluster (must not be found), a truncated JPEG at 600 at the end of a free run (not found); assert the two files, sizes, LCNs, Info; `TypeFilter = ["png"]` finds only the PNG; cancellation mid-run stops; pause/resume round trip; `Batch` raised; progress ends at `BytesScanned == FreeBytes`. Commit `feat(indexing): free-space carver with window growth, pause/resume and progress`.

---

### Task 5: Session integration and recovery of carved files (Indexing)

**Files:** Modify `RecoverySession.cs`; Test `tests/FileHound.Indexing.Tests/RecoveryTests.cs` (carved recovery on the synthetic volume via a `RecoverySession` over a memory reader — add an internal ctor `RecoverySession(IndexManager, DriveDescriptor, VolumeReader)` for tests).

Rules: `CarveAsync(typeFilter, progress, ct)` runs `Carver.Run`, then de-duplicates (FR-23): drop a carved file whose `StartLcn` equals the first run LCN of a `_lastUndelete` candidate (the named record wins). Candidates: `RecoveryCandidate(Carving, SuggestedName, null, Size, null, null, Excellent, 100, false, Info, carved)`. `RecoverAsync` for `Carving`: `UndeleteWriter`-style copy of `[StartLcn*cluster, +Size)` in 1024-cluster pieces, SHA-256, `ByteRun`, into `UniquePath(folder, SuggestedName)`; after writing, `MarkOfTheWeb(path)` for the executable extensions (write the `:Zone.Identifier` stream `[ZoneTransfer]\r\nZoneId=3\r\n`). Carving destination must pass `IsDifferentVolume`.

- [ ] Tests: carved PNG recovered byte-for-byte with hash and byte run; a carved `.exe` gets the zone stream; dedup drops the LCN shared with an undelete candidate. Commit `feat(indexing): carved-file recovery with Mark-of-the-Web and undelete de-duplication`.

---

### Task 6: Deep scan tab, consent, previews (App)

**Files:** Create `Services/PreviewService.cs`, `ViewModels/Recovery/DeepScanTabViewModel.cs`; Modify `SettingsService.cs` (`DeepScanConsented`), `RecoveryViewModel.cs` (tab `DeepScan`, scan state for the header chip, `RecoverAsync` dispatch), `RecoveryItem.cs` (`TypeLabel`, `LocationText` = "block N", `Thumbnail`), `RecoveryView.xaml(.cs)`, `SnapshotMode.cs` (+ `recovery-deepscan.png`), `App.xaml.cs` (settings into the VM); Test `RecoveryViewModelTests.cs`.

**Interfaces:**
```csharp
public sealed partial class DeepScanTabViewModel : ObservableObject
{
    public DeepScanTabViewModel(Func<bool> consented, Action markConsented, Func<RecoverySession?, IReadOnlyCollection<string>?, IProgress<CarveProgress>, CancellationToken, Task<IReadOnlyList<RecoveryCandidate>>>? scanner = null);
    [ObservableProperty] bool _needsConsent; [ObservableProperty] bool _isScanning; [ObservableProperty] bool _isPaused; [ObservableProperty] double _progress; [ObservableProperty] string _progressText; // "12.4 GB of 410 GB · ETA 6 min"
    public IReadOnlyList<ChipItem<FileCategory?>> TypeChips { get; }   // All / Images / Video / Audio / Documents / Archives / Apps (reuse Theme.ForCategory)
    public IReadOnlyList<ChipItem<DeepScanSort>> SortChips { get; }    // Type / Size / Location
    public ObservableCollection<RecoveryItem> Items { get; }  [ObservableProperty] RecoveryItem? _previewItem;  [ObservableProperty] PreviewContent? _preview;
    public IAsyncRelayCommand ScanCommand, PauseResumeCommand; public IRelayCommand StopCommand, ConsentCommand;
}
public sealed record PreviewContent(string Kind /* image|text|info|zip|none */, ImageSource? Image, string? Text, IReadOnlyList<string>? Entries, string Caption /* "Validated · 1920×1080" */);
public static class PreviewService { public static PreviewContent Build(RecoverySession session, CarvedFile f, int maxBytes = 50 << 20); }
```
UI §5.5: consent card (shield icon, three bullets from the FR-30 text, **I understand, scan** / Cancel) shown when `NeedsConsent`; toolbar **Scan free space** → progress bar + text + **Pause/Resume** + **Stop**; type chips filter results live; sort chips; rows: type icon (`Theme.ForCategory` tile), type label, size, "block 1,204,332", 40 px thumbnail for images (decoded lazily from the first 50 MB); selecting a row fills the 320 px preview `ClayCard` on the right; empty state "Nothing scanned yet". Header status chip: "Scanning free space… 42%" while carving (extend `RecoveryViewModel.IsScanning`). Consent is remembered in `AppSettings.DeepScanConsented`.

- [ ] VM tests: consent gate (scanner not called until `ConsentCommand`; remembered via `markConsented`), progress text formatting ("12.4 GB of 410 GB · ETA 6 min"), type-chip filtering, sort by location, pause/resume state, Stop cancels. `PreviewService` test for a synthetic PNG (decodes to 2×2) and a text carve (snippet). Snapshot review of `recovery-deepscan.png`. Commit `feat(app): Deep scan tab with consent, previews and live filters`.

---

### Task 7: Acceptance, docs, review, merge

- [ ] Extend `VhdAcceptanceTests` with a carve: plant a real PNG (from `SyntheticFiles.Png(64, 64)`) as a file, delete it, overwrite its MFT record by creating many small files, then `Carver.Run` must find it by LCN with the exact size and hash.
- [ ] README: Deep scan row becomes real; `### 1.4.0` changelog; version 1.4.0; architecture block adds `Carving`.
- [ ] Release build 0 warnings; all tests; elevated tests under UAC (VHD carve).
- [ ] Code review (Code Reviewer agent): validator bounds on hostile input (every validator fuzzed with 10k random mutations in a test — add `ValidatorFuzzTests` that asserts no exception and no size beyond the span + `NeedMore` contract), carver window growth termination, pause/cancel races, preview decode limits, Zone.Identifier correctness.
- [ ] Fix findings, merge `feat/recovery-c` → `main`, push.

## Self-review notes
- FR-21 → Task 4 (free runs, cluster boundaries, 4 MB chunks, first-byte dispatch). FR-22 → Tasks 1–3 (all 23 formats; GZIP bounded by inflating). FR-23 → Tasks 5 (dedup) and 6 (previews). FR-24 → Task 5 naming. FR-25 → Tasks 4/6. FR-30 → Task 6 consent. §8 MotW → Task 5. UI §5.5/§6 → Task 6.
- Names shared across tasks: `CarveResult.Ok/Reject/NeedMore`, `CarveType.Id/Label/Extension/Category/MaxSize/MagicOffset/Magics/Validate`, `Signatures.ByFirstByte/AtOffset/ById`, `CarvedFile.SuggestedName`, `Carver.Run/Pause/Resume/Batch/TypeFilter`, `CarveProgress(BytesScanned, FreeBytes, Found, Eta)`, `RecoverySession.CarveAsync`, `PreviewService.Build`, `DeepScanTabViewModel.ScanCommand/PauseResumeCommand/StopCommand/ConsentCommand`.
