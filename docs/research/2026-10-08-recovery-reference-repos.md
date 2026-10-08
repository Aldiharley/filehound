# Reference repositories for FileHound deleted-file recovery

*Date: 2026-10-08. Scope: what FileHound (C#/.NET 10, WPF, Apache-2.0, own NTFS MFT/USN reader in `src/FileHound.Indexing/Ntfs`) can learn from or legally reuse out of three repositories for an undelete feature plus optional DFIR-style recovery helpers.*

Repositories were cloned with `git clone --depth 1` into the session scratchpad (`refs/awesome-forensics`, `refs/digler`, `refs/StepWind`) and read in full where it mattered. Nothing under `M:\Projects\filehound` was modified except this file.

---

## TL;DR

| Repo | What it is | Language | License (file) | Verdict for FileHound |
|---|---|---|---|---|
| cugu/awesome-forensics | Curated link list | Markdown | `LICENSE`: CC0 1.0 Universal | Use as a map. Best leads: MFTECmd/MFT (MIT, C#), NTFSTool (MIT, C++), ntfs-linker (Apache-2.0), go-ntfs (Apache-2.0), PowerForensics (MIT, C#), NIST CFReDS test images. Several high-profile entries are GPL/AGPL/LGPL and must stay reference-only. |
| ostafen/digler | Signature-based file carver (CLI + Wails GUI) | Go 1.23 | `LICENSE`: MIT, (c) 2025 Stefano Scafiti | Port the carving architecture (header registry, validator-returns-exact-size, block-aligned stepping, stitched reader, DFXML output) if and when FileHound adds carving. No NTFS logic, no fragmentation handling, three unit tests in the whole repo. Two validators derive from Go's stdlib (BSD-3), which needs its own attribution. |
| pwnapplehat/StepWind | "Undo for your PC": USN-journal flight recorder plus a content-versioning store | C# .NET 10 (net10.0-windows), WPF + WebView2 | `LICENSE`: MIT, (c) 2026 StepWind Contributors | Use as reference for USN delete semantics on Windows 11 (POSIX-unlink marker), journal-gap policy, safe restore/reversal guards, service/GUI authorization, and the synthetic-stream test style. It does **not** parse the MFT, recover dead records, carve, or read the Recycle Bin or shadow copies. |

Nothing in the three repos implements NTFS undelete from the MFT. FileHound's existing parser is already closer to that than any of them; the gap is small and is spelled out in the final section.

---

## 1. cugu/awesome-forensics

**Purpose.** A curated list of free (mostly open-source) forensic tools, plus `LIBS.md`, a table of parsing libraries that is mostly the libyal catalogue.

**Language / license.** Markdown. `LICENSE` is **CC0 1.0 Universal**, so the list itself can be quoted or copied freely.

**Quality.** Actively maintained (last commit 2026-09-27), weekly link-check CI (`awesome_bot`), entries flagged as recommended / archived / unmaintained. Thin where FileHound needs depth: the *Carving* section has five entries (two are string extractors), there is no papers section, and *NTFS/MFT Processing* has eight entries. It lists tools, not algorithms, so it is a starting map rather than a design source.

### Top entries for FileHound's recovery feature

Licenses below are from the upstream projects (not from the list), stated as of my knowledge; re-check before copying anything.

| # | Entry | URL | License | Why it matters |
|---|---|---|---|---|
| 1 | **MFTECmd / MFT library** (Eric Zimmerman) | https://github.com/EricZimmerman/MFT (library), https://ericzimmerman.github.io | **MIT**, C# | The closest thing to FileHound's parser in the same language. Parses `$MFT`, `$J` (USN), `$Boot`, `$LogFile`, `$SDS`; models in-use vs free records, attribute lists, all four `$FILE_NAME` timestamps, data runs. The reference to check FileHound's record model against. His **RBCmd** (same site, MIT) parses Recycle Bin `$I` files. |
| 2 | **NTFSTool** (thewhiteninja) | https://github.com/thewhiteninja/ntfstool | **MIT**, C++ | Has an actual `undelete` command: walks free MFT records, scores recoverability against `$Bitmap`, extracts by data runs. Also reads `$UsnJrnl`, `$LogFile`, shadow copies and BitLocker. The best end-to-end undelete reference with a compatible license. |
| 3 | **ntfs-linker** (Stroz Friedberg) | https://github.com/strozfriedberg/ntfs-linker | **Apache-2.0**, C++ | Links `$MFT`, `$UsnJrnl` and `$LogFile` records into a per-file history (create/rename/delete), including files whose MFT record was reused. Model for the DFIR "what happened to this file" helper. |
| 4 | **go-ntfs** (Velocidex) | https://github.com/Velocidex/go-ntfs | **Apache-2.0**, Go | Clean, small NTFS reader: boot sector, MFT, attribute lists, `$I30` index parsing, USN. Good for cross-checking offsets and for its handling of fragmented `$MFT` (the case FileHound's raw tier currently refuses). |
| 5 | **PowerForensics** (Invoke-IR) | https://github.com/Invoke-IR/PowerForensics | **MIT**, C# | Unmaintained but readable C# for `$Bitmap`, `$MFT`, `$UsnJrnl`, `$LogFile`, raw volume reads, and copying a file out by data runs (`Invoke-ForensicDD`). A second C# cross-check. |
| 6 | **python-ntfs** (williballenthin) | https://github.com/williballenthin/python-ntfs | **Apache-2.0**, Python | Readable parser; notable for **INDX slack** parsing (deleted directory entries that survive in `$I30` buffers after the MFT record is reused). Reference for a later "names only" tier. |
| 7 | **The Sleuth Kit** | https://github.com/sleuthkit/sleuthkit | **IPL-1.0 / CPL-1.0** (weak copyleft; some GPL parts), C | The reference semantics for deleted-file listing (`fls -d`), orphan files (`$OrphanFiles` when the parent record was reused), and recovery by data runs (`icat`, `tsk_recover`). Read for behaviour; do not copy code. |
| 8 | **Autopsy** | https://www.sleuthkit.org/autopsy | **Apache-2.0**, Java | Has a Recycle Bin ingest module and shows how to use a GPL carver (PhotoRec) by shelling out rather than linking. UI conventions for "deleted" and "recoverable" markers. |
| 9 | **PhotoRec** | https://www.cgsecurity.org/wiki/PhotoRec | **GPL-2.0-or-later**, C | The de-facto carver. Its block-aligned header scan, per-format validators, and brute-force handling of some fragmented JPEGs are the algorithms digler imitates. **Do not copy code or signature tables** into an Apache project; derive signatures from format specs or from Apache Tika / file(1) magic (see section 4). |
| 10 | **RecuperaBit** (Lazza) | https://github.com/Lazza/RecuperaBit | **GPL-3.0**, Python | Reconstructs an NTFS tree bottom-up from scattered MFT records when the boot sector/partition table is gone (carves `FILE` records across the disk). Algorithm ideas only; **GPL, do not copy**. |
| 11 | **libfsntfs / libvshadow / libfusn / libsigscan** (libyal) | https://github.com/libyal | **LGPL-3.0-or-later**, C; docs GFDL | The most complete public **format documentation** for NTFS, VSS and USN lives in these repos' `documentation/` folders. Read the docs; do not port the C. On a live Windows box libvshadow is unnecessary (see section 4). |
| 12 | **Dissect** (fox-it) | https://github.com/fox-it/dissect | **AGPL-3.0**, Python | Modern, well-tested NTFS/VSS implementation; useful to read for edge cases (compressed runs, attribute lists). **AGPL: reference only.** |
| 13 | **Velociraptor** | https://github.com/Velocidex/velociraptor | **AGPL-3.0**, Go | Its `parse_mft` / `parse_usn` artifacts show what analysts expect from a deleted-file listing (IsDeleted, all timestamps, full path with orphan handling). **AGPL: reference only**; the underlying go-ntfs is Apache. |
| 14 | **NIST CFReDS / CFTT** | https://www.cfreds.nist.gov | US Government work (public domain) | CFTT publishes **Deleted File Recovery (DFR)** test methodology and NTFS images with known deleted files, fragmentation and overwrite cases. Use for acceptance tests. |
| 15 | **Digital Corpora** and **DFRWS 2006/2007 challenge images** | https://digitalcorpora.org, https://dfrws.org | Various (research use) | Standard carving benchmarks, including fragmented files; digler's README points at DFRWS 2006. Use for carver validation only. |

Also from the list, lower priority: `fslib` (MIT, Go, NTFS reader), `USN-Journal-Parser` (Python, license unverified), `MFTExtractor` (Go, license unverified), `bulk_extractor` (MIT, not NTFS-specific), `plaso` (Apache-2.0, timelines).

**Not in the list but worth knowing (papers).** Garfinkel, "Carving contiguous and fragmented files with fast object validation" (DFRWS 2007) is the paper behind validator-based carving and bifragment gap carving; Pal and Memon, "The evolution of file carving" (IEEE Signal Processing Magazine, 2009) is the survey; Richard and Roussev, "Scalpel: a frugal, high performance file carver" (DFRWS 2005); Garfinkel's DFXML paper (Digital Investigation, 2012) defines the report format digler emits.

**Verdict:** use as a map. Follow entries 1, 2, 3, 4, 5 for implementation details; treat 7, 9, 10, 11, 12, 13 as read-only.

---

## 2. ostafen/digler

**Purpose.** A file carver: scans a disk image or raw device block by block, matches file-type signatures at block boundaries, validates each candidate with a format-specific decoder that returns the exact file size, and writes a DFXML report. A separate `recover` step extracts files from the report; a Linux-only `mount` exposes the report as a FUSE filesystem. Ships a CLI (cobra) and a Wails/React desktop GUI. Single squashed commit visible (v0.1.0 line, last change 2026-08-14), self-described "early development".

**Language / license.** Go 1.23. `LICENSE` is **MIT** (c) 2025 Stefano Scafiti; every Go file carries the MIT header.

**Important license nuance.** `internal/format/jpeg.go` says it is "adapted from the standard library's `image/jpeg`" and `internal/format/png.go` is visibly `image/png`'s chunk walker. Go's standard library is **BSD-3-Clause**; if FileHound ports those two validators, it must attribute the Go Authors (BSD-3) as well as digler (MIT) in `THIRD-PARTY-NOTICES.md`. Everything else in `internal/format` reads as original.

### Architecture (what is actually there)

```
cmd/cmd/            scan | recover | mount | merge | formats (cobra)
internal/scan/      ScanPartition: discover partitions -> build registry -> Scanner -> DFXML writer (+ optional dump)
internal/format/    Scanner (block loop), FileRegistry (PrefixTable), Reader, SeekAt, 14 format validators
internal/disk/      MBR parser, FAT boot sector, NormalizeVolumePath ("C:" -> "\\.\C:"), unused GuessBlockSize
internal/fs/        os.Open fallback -> CreateFile raw device; sector-aligned ReadAt
pkg/reader/         BufferedReadSeeker (peek/seek over a stream), MultiReadSeeker (stitches readers)
pkg/dfxml/          DFXML header + fileobject/byte_run writer and reader
pkg/table/          PrefixTable: 64 Ki byte-array keyed by a 16-bit rolling hash of the signature prefix
internal/app/       Wails GUI API: scan status, pause/resume (PauseGate), history store of past scans
```

### Carving engine, concretely

- **Registry.** `FileRegistry.Add` inserts each signature into a `PrefixTable`: for every prefix of the key it marks `table[h]` where `h = (h << 2) + byte` (uint16), and marks the full key as an element. `Walk(data)` advances the same hash over the block's first bytes and invokes the scanner for every complete key that is a prefix of the data; it stops at the first unmarked hash. Collisions only cost extra map lookups, never missed matches. Signatures are 1 to 8 bytes (`PCX` is the single byte `0x0A`; MP3 uses six 2-byte frame syncs), so false positives are entirely the validator's problem.
- **Block loop.** `Scanner.Scan` reads a 4 MB buffer (rounded to the block size) with `ReadAt`, then `scanBuffer` tries the registry at every *block start*. On a validated hit it skips `ceil(size / blockSize)` blocks and bumps the next buffer offset past the file, so files embedded inside a carved file (thumbnails in a JPEG) are deliberately not found. Scanning is single-threaded.
- **Validator contract.** `ScanFile(r *Reader) (*ScanResult, error)` must consume the file from its header and return the exact size (and optionally a better extension: ZIP sniffs `[Content_Types].xml` + `word/document.xml` to report `docx`/`pptx`/`xlsx`). Any error means "not a file here". The `Reader` is a `BufferedReadSeeker` (4 KB window) over a `MultiReadSeeker` that stitches the remainder of the in-memory 4 MB buffer with an `io.SectionReader` over the rest of the device, capped at `maxFileSize` (CLI default 4 GB, GUI 10 GB). So validators read as far as they need without the scanner re-reading.
- **Validators** (`internal/format`): JPEG walks markers to EOI (libjpeg-lenient); PNG verifies every chunk CRC through IEND; GIF, BMP, PCX, TIFF, WAV, AU, WMA/ASF, MP3, RAR, ZIP, PDF (`%PDF-` then last `%%EOF` within a hard-coded 16 MB), SQLite (size = page size x page count, only trusted when `change counter == version-valid-for`). `SeekAt` is a boundary-safe signature search with a `len(sig)-1` overlap.
- **Output.** One `<fileobject>` per hit with a single `<byte_run>`; `recover` re-reads those runs. Filenames are `f<block>.<ext>`.
- **Raw device access (Windows).** `CreateFileW(\\.\C:, GENERIC_READ, FILE_SHARE_READ|WRITE)`, `ReadFile` with an `OVERLAPPED` offset, a fresh sector-aligned buffer per call.

### Code quality (concrete)

Good: small, readable packages; clear separation of scanner, registry and validators; a stitched reader that lets validators run on the live device; a pause gate for the GUI; DFXML interop; a `merge` command that builds synthetic test images with random gaps (this is their test strategy).

Weak:

- **Tests.** Three `Test*` functions in the repository (`pkg/reader/multi_read_seeker_test.go` x2, `pkg/sysinfo/sysinfo_test.go`). No validator or scanner tests. CI (`.github/workflows/build.yml`) only builds; it never runs `go test`, and the artifact path is the placeholder `./your_binary_name`.
- **Swallowed errors.** `Scanner.Scan` returns silently on `ReadAt` error and on context cancellation (`// TODO: return error`); `cmd/cmd/scan.go:parseOptions` returns `scan.Options{}, nil` when plugin listing fails.
- **Broken `-o` flag.** `scan.ScanPartition` only sets `reportFileName` when `opts.ReportFile == ""`, so passing `--output` leads to `os.Create("")`.
- **Volume size on Windows.** `fs.WindowsDiskFile.Stat` derives the size from `IOCTL_DISK_GET_DRIVE_GEOMETRY` (cylinders x heads x sectors), which is a disk geometry, not a volume length. Combined with the swallowed read error, a scan of `C:` ends silently wherever the first failing read lands. `IOCTL_DISK_GET_LENGTH_INFO` (or `FSCTL_GET_NTFS_VOLUME_DATA`) is the right call.
- **No NTFS awareness.** Partition discovery recognises FAT types only and treats a GPT disk as one partition sized by the protective entry's 32-bit sector count (2 TiB cap). An NTFS-only MBR disk falls back to "whole device, 512-byte blocks", so the cluster-aligned skip is lost. `GuessBlockSize` exists but is never called. There is no `$Bitmap` use, so allocated (live) files are "recovered" alongside deleted ones.
- **No fragmentation handling.** Each hit is one contiguous run; a fragmented JPEG validates up to the first foreign cluster and is reported truncated or rejected. The README's "regardless of file system, even when metadata is lost" is true only for contiguous files.
- **Allocation churn.** `ReadAt` allocates a new aligned buffer per call; `Walk` allocates a string per candidate prefix.
- **Plugins** use Go's `plugin` package (`.so`), so they do not work on Windows.
- **Hard-coded limits** (`pdfMaxFileSize = 16 MB`) are not configurable and not documented.

### What FileHound could port (legally: MIT, with attribution)

- The *shape* of the engine: `IFileCarver { ReadOnlySpan<byte> Signature; bool TryMeasure(Stream, out long size, out string ext); }`, a prefix dispatch keyed on the first bytes, block-aligned stepping, skip-past-hit, and a reader that stitches the current buffer with the device. This is a two-day port in C#.
- The DFXML model (`pkg/dfxml/dfxml.go`), extended to several `byte_run`s per file, which DFXML already allows, so MFT-recovered fragmented files can be described.
- The ZIP/OOXML extension sniffing and the SQLite size rule.
- The JPEG and PNG validators, **with BSD-3 attribution to the Go Authors** in addition to MIT attribution to digler.

**Verdict: use as reference / port the architecture** when carving is added; it is not an undelete design, and it should not be FileHound's first recovery tier. Its signature table (14 formats, 1 to 8 byte magic values) is factual and trivial; FileHound should build a larger table from Apache Tika's `tika-mimetypes.xml` (Apache-2.0) or file(1)'s magic database (BSD-2) rather than from digler or PhotoRec.

---

## 3. pwnapplehat/StepWind

**Purpose.** "An undo button for your whole PC." Two layers: (1) a *flight recorder* that tails the NTFS USN journal on every fixed volume, reconstructs user-level operations (create/modify/rename/move/delete), attributes them to a process via ETW, and lets the user reverse a move or rename with one click; (2) a *time machine* that watches chosen folders with `FileSystemWatcher`, captures every save as FastCDC-chunked, deduplicated, optionally AES-GCM encrypted blobs, and restores any version (including of deleted files) to a non-colliding path. Also an MCP server for AI agents, an elevated Windows service, a WebView2 tray GUI, an Inno Setup installer, and enterprise ADMX policy. v1.0.4, last commit 2026-09-05.

**Language / license.** C# on `net10.0-windows` (Core has no WPF dependency; App is WPF + WebView2). `LICENSE` is **MIT**, (c) 2026 StepWind Contributors. NuGet: TraceEvent (MIT), ModelContextProtocol (MIT), Microsoft.Web.WebView2 (Microsoft redistributable terms), Hardcodet.NotifyIcon.Wpf (CPOL; FileHound already uses the MIT `H.NotifyIcon.Wpf`, keep that).

### What it is not

It does not open `$MFT`, parse FILE records, read `$Bitmap`, carve, read `$Recycle.Bin` (it only *hides* `\$Recycle.Bin\` paths from the timeline in `FlightRecorder.IsProgramInternal`), or touch Volume Shadow Copies. "Recovering a deleted file" in StepWind means "we captured a copy before it was deleted" (`StepWindHost.RecoverableVersionFor` maps a Delete operation to the latest stored version of that path, or null). So it is not an undelete tool in the NTFS sense; it is a pre-delete capture tool.

### Relevant techniques and data structures

- **`UsnJournalReader`** (`src/StepWind.Core/Journal/UsnJournalReader.cs`): opens `\\.\C:` with `GENERIC_READ`, `FSCTL_QUERY_USN_JOURNAL` for `UsnJournalState(JournalId, FirstUsn, NextUsn, LowestValidUsn, MaxUsn)`, `FSCTL_READ_USN_JOURNAL` with `READ_USN_JOURNAL_DATA_V0` (all reasons, not close-only), parses V2 and V3 layouts (same offsets FileHound's `UsnRecordParser` uses), keeps the **full 64-bit FRN including the sequence number** so `OpenFileById` + `GetFinalPathNameByHandle` can resolve a parent directory path. Notes in the code record two real bugs they hit: `FILE_ID_DESCRIPTOR` must be exactly 24 bytes, and open-by-id needs `FILE_READ_ATTRIBUTES` (not 0) plus full sharing to name an in-use directory.
- **`UsnResyncPolicy`** (pure function): journal id changed -> resume at current end; cursor below `LowestValidUsn` -> resume at `LowestValidUsn` and report a **GapTruncated** loudly; else continue. FileHound's README says it already re-indexes on a failed live loop; this is the explicit three-way version.
- **`OperationReconstructor`**: pure, injectable (`Func<ulong,string?>` resolver), unit-tested on synthetic record arrays. Pairs `RENAME_OLD_NAME`/`RENAME_NEW_NAME` by FRN (same parent = rename, different = move). The valuable, hardware-measured finding: on Windows 11 (build 26200) deletes are usually **POSIX unlinks**: the file is first renamed into `\$Extend\$Deleted` under a marker name (`"16hex:name"` on older builds, or bare 24-hex where the first 16 hex digits are the file's own FRN), and the `FILE_DELETE` record arrives only when the last handle closes, sometimes minutes later. They emit the Delete at the marker rename, using the FRN's last real name and parent, and suppress the later `FILE_DELETE`. `IsMarkerRecord` requires the embedded FRN to match so an all-hex user filename (a git object) is never misclassified.
- **`OperationReverser`**: reversal only for Move/Rename; refuses if the item is no longer at `NewPath` or if `OldPath` is now occupied; takes an `IFileSystemActions` so it is tested without I/O.
- **`VersionStore.RestoreToSafePath`**: writes to `<path>.swtmp`, `Flush(flushToDisk: true)`, then `File.Move(temp, final, overwrite: false)` to a name like `name (restored 2026-07-18 173000).ext`; reapplies the original mtime best-effort. Restores never overwrite.
- **`FlightRecorder.OpToken`**: the GUI gets an opaque `frn:ticks:kind` handle; the service re-derives paths from its own ring entry, so a client cannot forge "move X to Y". Relevant because FileHound's prior research recommends the same service/UI split.
- **`FileAttributionTracker`**: ETW kernel FileIO + Process providers; only *authorship* events (create/write/rename/delete/set-disposition), matched to the USN operation's own kind and time window, with PID names resolved at event time. Out of scope for FileHound but a model for "which process deleted this".
- **`WatchEngine`**: create fast-path (250 ms poll, 400 ms stability, 5 s give-up) so a create-then-delete inside the 2 s debounce still leaves a version; watcher error -> rebuild + reconcile; startup reconcile compares mtime with the latest version.
- **`FastCdc`**: gear-hash CDC, 16/64/256 KiB, deterministic table; `CHANGELOG.md` documents the measurement that moved them off restic's 1 MiB defaults. Not needed by FileHound.

### Code quality (concrete)

Good: 256 `[Fact]`/`[Theory]` attributes across 36 xunit files (README claims ~300 tests; Theories expand), run on `windows-latest` in CI with an installer build, MCP smoke test, SHA256SUMS and optional SignPath signing; pure logic classes are injectable and tested on synthetic inputs; `SECURITY.md` spells out per-user authorization over the pipe; doc comments record *why* (measured numbers, rejected alternatives); atomic cursor persistence (`tmp` + `File.Move(overwrite: true)`).

Weak: `UsnJournalReader` is constructed (CreateFile) and disposed every 2 s poll per volume and `Read` allocates a 1 MB `AllocHGlobal` buffer per call; parsing is `Marshal.ReadInt64` per field rather than spans; V4 records are dropped silently; a failed `DeviceIoControl` inside `Read` just breaks the loop with no error code surfaced (Win32 `ERROR_JOURNAL_ENTRY_DELETED` etc. are not distinguished); `FlightRecorder.Find` is an O(n) scan that formats a token string per element; `StepWindHost.cs` is a 2,155-line class. None of these affect correctness of the parts worth borrowing.

**Verdict: use as reference only** (MIT would allow copying, but the pieces worth taking are a few dozen lines each and FileHound already has its own USN reader). Specifically take: the POSIX-delete marker logic, the resync policy shape, the restore-never-overwrites pattern, the reversal guards, and the op-token idea. Do not look here for MFT undelete.

---

## 4. What FileHound should take from these

FileHound already has the hard part: `MftRecordParser` (fixups, `$STANDARD_INFORMATION`, `$FILE_NAME`, unnamed `$DATA` runs), `DataRuns.Decode`, `RawMftReader` (elevated volume handle, extent walk, record-aligned 4 MB reads), a `UsnRecordParser` and a live journal loop. Undelete is mostly *not discarding* what it already parses. In priority order:

1. **Enumerate dead MFT records instead of skipping them.** `RawMftReader.Accumulator.AddRecord` returns on `!r.InUse`; add a second sink for records that pass `ApplyFixups`, have the `FILE` signature, `InUse == false`, a non-DOS `$FILE_NAME` and a `$DATA`. Extend `MftRecord` with: the record's own **sequence number** (offset 16), the **parent reference's sequence number** (top 16 bits of the `$FILE_NAME` parent ref, currently masked off by `RecordMask`), the four `$FILE_NAME` timestamps (value offsets 8..39), and the non-resident attribute **flags** (attribute offset 12: `0x0001` compressed, `0x4000` encrypted, `0x8000` sparse) and **initialized size** (offset 56). Link extension records exactly as the live path does (`BaseRecord`). Resolve the path through the live index while the parent's current sequence number equals the one stored in the dead record; otherwise show it under an "Orphaned" node, the way TSK's `$OrphanFiles` does. (NTFSTool, MFT library, TSK semantics.)

2. **Score recoverability against `$Bitmap` before promising anything.** Read MFT record 6's unnamed `$DATA` (one bit per cluster, LSB first) once per scan, then for each dead record check every cluster of its data runs: free = still recoverable, allocated = overwritten. Report `recoverable clusters / total` per file, and mark resident data as 100 %, `EFS` as not recoverable, compressed runs as "needs LZNT1" (defer), sparse runs as zero-filled. This single check is what separates a trustworthy undelete from a list of names. (NTFSTool does this; digler and StepWind do not.)

3. **Extract by data runs through the existing raw reader; never write to the source volume.** Resident data comes straight from the record; non-resident data is `Kernel32.ReadExactly` per run at `lcn * bytesPerCluster`, truncated to real size (or initialized size, zero-filling the tail). Restore to a *different* volume by default (warn if the same), and copy StepWind's `RestoreToSafePath` shape: temp file, flush, `Move(overwrite: false)`, `name (restored ...).ext`, re-apply the `$FILE_NAME` timestamps. Keep the volume handle `GENERIC_READ | FILE_SHARE_READ | FILE_SHARE_WRITE`, as both repos do.

4. **Recycle Bin first in the UI: cheapest, no elevation, highest hit rate.** Walk `X:\$Recycle.Bin\<SID>\$I*`; `$I` v2 (Windows 10+) is `int64 version=2, int64 size, FILETIME deleted, int32 nameChars, UTF-16 name`; v1 (Vista to 8.1) is `version=1, size, FILETIME, 520-byte name`. `$R<same suffix>` holds the content (a directory for deleted folders). Restore = move `$R` to the recorded path (non-colliding) and delete `$I`. Zimmerman's RBCmd (MIT, C#) is the reference; no library is needed. Show these as "In Recycle Bin" with the original path, above MFT-recovered items.

5. **Keep a "recently deleted" log from the journal FileHound already tails, with StepWind's POSIX-delete rule.** On `RENAME_NEW_NAME` into a `\$Extend\$Deleted` marker (hex name embedding the FRN) or on `FILE_DELETE`, record `(recordNo, sequence, lastRealName, lastParent, timestampUtc)` before the index entry is dropped. Later, a dead MFT record can be matched exactly: same record number and `record.sequence == frn.sequence + 1` (NTFS increments the sequence when the record is freed, skipping zero; verify on a test image). That yields "deleted 14:02 today from `D:\Reports`" for the undelete list and the DFIR timeline without parsing `$LogFile`. Keep the three-way resync (`JournalChanged`, `GapTruncated`, `None`) explicit so a gap is reported, not silently skipped.

6. **Use Volume Shadow Copies as a recovery source without any library.** On a live system enumerate `Win32_ShadowCopy` (WMI) or `vssadmin list shadows`; each has a `DeviceObject` like `\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3`. `DeviceObject + "\" + relativePath` opens files with normal `CreateFile`/`FindFirstFile` (trailing backslash required for directories), which gives "previous versions" of a deleted file for free. Opening the device object itself without a trailing backslash gives a raw volume handle, so `RawMftReader` can index the snapshot's `$MFT`; if `FSCTL_GET_NTFS_VOLUME_DATA` is refused on the shadow device, parse the boot sector (bytes/sector at 11, sectors/cluster at 13, MFT LCN at 48, clusters-per-record at 64) and keep the rest of the reader unchanged. libvshadow (LGPL) is only needed for offline images.

7. **Be honest about SSDs and overwrites in the UI.** On a TRIM-enabled SSD, freed clusters usually read back as zeros within seconds, so the metadata survives but the content does not. Show the `$Bitmap` score, show "content zeroed" when the first clusters read as all-zero, and say "no copy exists" plainly (StepWind's wording principle) rather than offering a dead Restore button. Also note BitLocker: reads through `\\.\C:` are decrypted, reads through `\\.\PhysicalDrive0` are not; always use the volume handle.

8. **Test the way the good repo tests.** Unit-test the parser changes with the existing `tests/FileHound.Indexing.Tests/MftRecordBuilder.cs` (build dead records, extension records, reused parents, resident/non-resident/sparse/compressed `$DATA`). Add an `Elevated`-trait integration test that creates a small VHD (`New-VHD` + `Format-Volume` or diskpart), writes known files, deletes some, overwrites some, then undeletes through the production classes and compares hashes, which mirrors StepWind's real-hardware E2E. Validate against NIST CFTT Deleted File Recovery images; use DFRWS 2006 only if carving lands.

9. **Carving is the last tier, scoped to unallocated clusters.** If and when it is added, port digler's architecture in C# (MIT attribution for digler; BSD-3 attribution for the Go Authors if the JPEG/PNG validators are ported), but iterate only over clusters that `$Bitmap` marks free, step by cluster size, and run on the volume handle. Build the signature table from Apache Tika's `tika-mimetypes.xml` (Apache-2.0) or file(1) magic (BSD-2), not from PhotoRec (GPL-2+). Skip fragmented-file carving; MFT-based recovery already handles fragmentation because the runs are known.

10. **Export and isolation.** Emit a DFXML report (digler's `pkg/dfxml` model, one `byte_run` per data run) and CSV for DFIR users; keep recovery behind the planned elevated service with StepWind-style opaque operation tokens so the unelevated UI can never request an arbitrary raw read or move. A cheap bonus that falls out of item 5 is StepWind's one-click **undo move/rename** with its two guards (item still at `NewPath`, `OldPath` free).

### Must not be copied into an Apache-2.0 project

PhotoRec / TestDisk (GPL-2.0-or-later), RecuperaBit (GPL-3.0), Velociraptor (AGPL-3.0), Dissect (AGPL-3.0), all libyal libraries (LGPL-3.0; dynamic linking would be permitted but they are C and not needed on live Windows), The Sleuth Kit (IPL/CPL, weak copyleft). Reading them for behaviour and format knowledge is fine; porting code or copying their tables is not. Go's standard library code embedded in digler is BSD-3-Clause and needs its own notice if ported.

### Files referenced

- FileHound: `M:\Projects\filehound\src\FileHound.Indexing\Ntfs\MftRecordParser.cs`, `RawMftReader.cs`, `DataRuns.cs`, `UsnRecordParser.cs`; `M:\Projects\filehound\tests\FileHound.Indexing.Tests\MftRecordBuilder.cs`; `M:\Projects\filehound\THIRD-PARTY-NOTICES.md`.
- digler (scratchpad clone): `internal/format/scanner.go`, `registry.go`, `search.go`, `reader.go`, `jpeg.go`, `png.go`, `zip.go`, `pdf.go`, `sqlite.go`; `internal/scan/scan.go`; `internal/fs/windows.go`; `pkg/table/table.go`; `pkg/dfxml/dfxml.go`; `cmd/cmd/scan.go`.
- StepWind (scratchpad clone): `src/StepWind.Core/Journal/UsnJournalReader.cs`, `UsnResyncPolicy.cs`, `OperationReconstructor.cs`, `OperationReverser.cs`, `FlightRecorder.cs`; `src/StepWind.Core/Storage/VersionStore.cs`; `src/StepWind.Core/Engine/WatchEngine.cs`, `StepWindHost.cs`; `tests/StepWind.Core.Tests/OperationTests.cs`, `PreDeleteCaptureTests.cs`; `SECURITY.md`.
