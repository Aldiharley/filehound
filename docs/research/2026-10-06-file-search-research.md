# FileHound: research on fast whole-disk file search for Windows

*Date: 2026-10-06. Scope: C#/.NET desktop app for Windows 11, Everything-style name search across all local drives, with fuzzy matching and a polished UI.*

---

## TL;DR recommendations

| Area | Recommendation |
|---|---|
| Runtime | **.NET 10 (LTS, Nov 2025)**. .NET 8 and .NET 9 both reach end of support in Nov 2026. |
| NTFS indexing | Do a full scan with `FSCTL_ENUM_USN_DATA` (`MFT_ENUM_DATA_V1`, versions 2..3). Keep it live with a blocking `FSCTL_READ_USN_JOURNAL` loop for each volume. Save `UsnJournalID` and `NextUsn` with each snapshot. |
| Non-NTFS / no admin | Use `FileSystemEnumerable<T>`, which calls `NtQueryDirectoryFile` underneath. Walk directories in parallel on SSDs. Use `FileSystemWatcher` for updates and rescan the subtree when its buffer overflows. |
| Elevation | Run a **separate indexer** as a Windows service (LocalSystem) and keep the UI unelevated. Connect them with a **named pipe** whose ACL is restricted. Avoid `requireAdministrator` on the UI process. |
| Index | Struct-of-arrays layout with names in one contiguous arena, plus a separate case-folded arena that we search directly. Target 60–90 B per entry (Everything uses about 75–100 MB per million files; floki uses about 55 MB). |
| Persistence | One binary snapshot per volume GUID: a header followed by raw array dumps. Load it with one read per array. Replay the USN journal after loading. |
| Search | Tier 1: vectorized `Span.IndexOf` over the whole folded arena. Tier 2: fzf-style subsequence scoring on candidates that pass a char-mask prefilter. Tier 3: bounded Myers/Hyyrö edit distance (k≤2) when tiers 1–2 return few hits. Write this ourselves; do not depend on FuzzySharp for the hot path. |
| UI | **WPF on .NET 10 with WPF-UI (lepoco, MIT)**. Use a custom virtualizing ListView (recycling) bound to a lazy `IList`. Avalonia is the strongest alternative if native *inset* shadows matter a lot for the clay look. |

---

## 1. Existing projects

| Project | Lang | License | Stars (approx.) | What to learn / reuse |
|---|---|---|---|---|
| [voidtools Everything](https://www.voidtools.com/) | C | Closed (SDK is MIT) | n/a | The reference design. It reads the MFT for the initial index and uses the USN journal only to stay current. A SYSTEM "Everything Service" serves an unelevated client. It uses roughly 75–100 MB per 1M files ([forum](https://www.voidtools.com/forum/viewtopic.php?t=12117)). Its syntax is what users expect ([docs](https://www.voidtools.com/support/everything/searching/)). |
| [Lertaro/Lertaro](https://github.com/Lertaro/Lertaro) | C# .NET 10, WPF | **MIT** | ~765 | The closest analogue to FileHound. It has three processes: a SYSTEM service that parses the MFT and USN journal, a WPF UI, and a hook helper. It also has fuzzy search and plugins. Read it for the service/UI split and its IPC. |
| [cyb3rcr4t0712/tachyon](https://github.com/cyb3rcr4t0712/tachyon) | C# .NET 8, WPF | **MIT** | new | Small and readable. It makes one `FSCTL_ENUM_USN_DATA` pass into a 256 KB buffer, polls the USN journal, caches the index for 50–150 ms startup, and renders results in a virtualized WPF list. It indexes about 226k files/s (3.4M files in about 15 s), which is slow, so use it for structure and not for speed tricks. |
| [wangfu91/UsnParser](https://github.com/wangfu91/UsnParser) | C# (.NET 10) | **MIT** | ~23 | A clean P/Invoke layer for NTFS and **ReFS** covering MFT search, journal reads and journal monitoring. A good source for interop structs. There is also a Rust port, [usn-parser-rs](https://github.com/wangfu91/usn-parser-rs). |
| [bighamx/UsnExplorer](https://github.com/bighamx/UsnExplorer) | C# .NET 8 | check | small | A USN viewer that rebuilds full paths from the MFT and handles millions of rows. Useful for path-rebuild and grid code. |
| [nikkoxgonzales/floki](https://github.com/nikkoxgonzales/floki) | Rust | **MIT** | new | **The best index design to copy.** Each entry is 24 bytes (FRN, parent ref, name offset and length, flags), and names live in a shared byte arena. Paths are built only when needed. Queries run in parallel over the arena using block-level presence bitmaps, and a refined query searches inside the previous results. It snapshots `index.bin` every 10 minutes (about 2.5 s to load), reads the journal every 750 ms, and grows the journal to 256 MB. Measured: 11.8M files in 630 MB, and "python" returned 1.09M hits in 115 ms. |
| [skyllc-ai/UltraFastFileSearch](https://github.com/skyllc-ai/UltraFastFileSearch) | Rust | MPL-2.0 | — | Reads the **raw $MFT** with IOCP and a sliding window, skipping unused records via the bitmap (40–55% less I/O). This path gives sizes and dates as well. It indexed 25.9M records on 7 drives in 68 s. MPL is file-level copyleft, so learn from it but do not paste its code. |
| [ChrisS85/FastFileSearch](https://github.com/ChrisS85/FastFileSearch) | C++ | none stated | ~157 | An old USN-based DLL. Treat it as reference only, since it has no license. |
| [LeiHao0/Fake-Everything](https://github.com/LeiHao0/Fake-Everything) | C++ | none stated | ~533 | A Chinese write-up of the FRN-to-parent hash-map path rebuild. Educational only. |
| [srwi/EverythingToolbar](https://github.com/srwi/EverythingToolbar) | C# WPF | **MIT** | ~14.8k | **UX reference.** Shows the Windows 11 taskbar search box, a shell context menu, filters, Win+Alt+S, drag-out, QuickLook preview and theming. Third-party parts carry their own licenses: NHotkey is Apache-2.0 and the shell context menu code is CPOL. |
| [Flow-Launcher](https://github.com/Flow-Launcher/Flow.Launcher) | C# WPF | MIT | ~13.9k | Hotkey launcher patterns, result ranking, and its Everything/Explorer plugin. |
| [PowerToys](https://github.com/microsoft/PowerToys) (Command Palette) | C#/C++ | MIT | very large | WinUI 3 launcher patterns. Note that its file search still uses the Windows Search indexer. |
| [SwiftSearch](https://sourceforge.net/projects/swiftsearch/) | C++ | CC BY-NC 2.0 | — | Parses the raw MFT for every search. **Its non-commercial license rules out reuse.** |
| NtfsReader (Danny Couture, CodeProject) | C# | LGPL | — | A raw MFT parser in C# that returns sizes and dates. LGPL plus its age make it reference only. |
| Rust crates `ntfs` (Colin Finck), `mft` (omerbenamram) | Rust | MIT/Apache | — | Good references for parsing NTFS FILE records (fixups, attributes, data runs) if we build a raw MFT reader. |

**License policy:** we may copy or adapt MIT and Apache code with attribution in `THIRD-PARTY-NOTICES`. We do not copy GPL, LGPL, MPL, CC-NC, CPOL or unlicensed code; reading them for ideas is fine. Write P/Invoke ourselves with **Microsoft.Windows.CsWin32** (MIT source generator) or use **Vanara** (MIT).

---

## 2. Whole-disk indexing

### 2.1 Volume discovery

- Enumerate volumes with `FindFirstVolumeW`/`FindNextVolumeW` and `GetVolumePathNamesForVolumeNameW`. This also finds volumes mounted in folders with no drive letter. Key every index on the **volume GUID**, not the drive letter.
- Get the filesystem name ("NTFS", "ReFS", "FAT32", "exFAT") and the serial number from `GetVolumeInformationW`. Get the drive type (FIXED, REMOVABLE, REMOTE, CDROM) from `GetDriveTypeW`.
- Watch `WM_DEVICECHANGE` (`DBT_DEVICEARRIVAL` and `DBT_DEVICEREMOVECOMPLETE`) to pick up USB drives as they come and go.

### 2.2 Initial NTFS scan with `FSCTL_ENUM_USN_DATA`

```text
h = CreateFileW(@"\\.\C:", GENERIC_READ, FILE_SHARE_READ|FILE_SHARE_WRITE, null,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, null)   // needs admin/SYSTEM
DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, ...) -> USN_JOURNAL_DATA_V1  (journal id, NextUsn)
med = { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = journal.NextUsn,
        MinMajorVersion = 2, MaxMajorVersion = 3 }
loop:
  DeviceIoControl(h, FSCTL_ENUM_USN_DATA, &med, sizeof(med), buf, 1 MB, &ret)
  if error == ERROR_HANDLE_EOF: break
  med.StartFileReferenceNumber = *(ulong*)buf          // first 8 bytes = next start FRN
  for (p = buf+8; p < buf+ret; p += rec->RecordLength)  // records are 8-byte aligned
      switch rec->MajorVersion { 2: parse V2; 3: parse V3 }
```

The calls and structs used ([FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data), [USN_RECORD_V3](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v3)):

```csharp
[StructLayout(LayoutKind.Sequential)]
struct MFT_ENUM_DATA_V1 { public ulong StartFileReferenceNumber; public long LowUsn, HighUsn;
                          public ushort MinMajorVersion, MaxMajorVersion; }

// USN_RECORD_V2 byte offsets (do NOT use sizeof; always use RecordLength/FileNameOffset)
//  0 uint  RecordLength      4 ushort Major   6 ushort Minor
//  8 ulong FileReferenceNumber            16 ulong ParentFileReferenceNumber
// 24 long  Usn   32 long TimeStamp(FILETIME)  40 uint Reason  44 uint SourceInfo
// 48 uint  SecurityId  52 uint FileAttributes  56 ushort FileNameLength(bytes)  58 ushort FileNameOffset

// USN_RECORD_V3: identical except FILE_ID_128 (16 bytes) for both FRNs:
//  8 FRN[16]  24 ParentFRN[16]  40 Usn  48 TimeStamp  56 Reason  60 SourceInfo
// 64 SecurityId  68 FileAttributes  72 FileNameLength  74 FileNameOffset
```

Points that matter in practice:

- **What an NTFS FRN contains:** the low 48 bits are the MFT record number and the high 16 bits are a sequence number. Size the lookup as `int[] recordToIndex` indexed by record number (4 B per record). This avoids a `Dictionary<ulong,int>`, which costs about 24+ B per entry. Use the sequence number to detect a reused record.
- **Root directory:** the root is MFT record 5 (FRN `0x0005000000000005`). Start every path at the volume mount point.
- **Rebuilding paths:** store only `parentIndex` and walk up the chain on demand into a `stackalloc char[]` buffer. Cache paths only for rows on screen. A parent that is missing (an orphan) goes under a synthetic `<orphaned>` node, and any `$`-prefixed system metafiles that show up can be filtered out.
- **ReFS:** IDs are 128 bits, so you get V3 records. Store the full 128-bit ID in a side array and fall back to a hash map from ID to index, because there is no dense record number. UsnParser and Lertaro both report ReFS support. Verify this on the target build, and use the directory-walk fallback if the ioctl fails.
- **Hard links:** you get one record (one name) per file record, so additional hard-link names are not seen. Everything has the same limitation.
- **No sizes or dates:** `FSCTL_ENUM_USN_DATA` returns name, parent and attributes only. Three ways to fill them in:
  1. **Lazy (start here):** look up sizes and dates only for rows on screen, using `GetFileInformationByHandleEx` or `FindFirstFileExW`, and cache the results.
  2. **Background fill:** after the scan, walk each directory with `FileSystemEnumerable` and fill a `long size[]` and a `long mtime[]`.
  3. **Raw $MFT parser (later):** this is what Everything and UFFS do. Get the extents of `C:\$MFT` with `FSCTL_GET_RETRIEVAL_POINTERS` and read the volume sequentially in multi-MB chunks. For each 1 KB FILE record, apply the update-sequence fixups, then parse `$STANDARD_INFORMATION` (timestamps), `$FILE_NAME` (name and parent) and the unnamed `$DATA` (size). This gives everything in one pass and is needed for fast `size:` and `dm:` filters on the whole index.

### 2.3 Live updates from the USN journal

```csharp
struct USN_JOURNAL_DATA_V1 { public ulong UsnJournalID; public long FirstUsn, NextUsn, LowestValidUsn, MaxUsn;
                             public ulong MaximumSize, AllocationDelta; public ushort MinSupportedMajorVersion, MaxSupportedMajorVersion; }
struct READ_USN_JOURNAL_DATA_V1 { public long StartUsn; public uint ReasonMask, ReturnOnlyOnClose;
                                  public ulong Timeout, BytesToWaitFor, UsnJournalID; public ushort MinMajorVersion, MaxMajorVersion; }
```

- Run one dedicated thread per volume with `BytesToWaitFor = 1` and `Timeout = 0`, so `DeviceIoControl` blocks until there is new data. Cancel with `CancelIoEx` on shutdown. The output buffer starts with 8 bytes holding the next USN.
- Set `ReturnOnlyOnClose = 1` so you mostly receive one record per file close, with all its reasons combined. Set `ReasonMask` to FILE_CREATE | FILE_DELETE | RENAME_OLD_NAME | RENAME_NEW_NAME | BASIC_INFO_CHANGE | DATA_EXTEND | DATA_TRUNCATION | DATA_OVERWRITE | HARD_LINK_CHANGE | CLOSE.
- How to handle each reason:
  - `FILE_CREATE`: add the entry.
  - `FILE_DELETE`: tombstone it by setting a flag, and compact during the next snapshot.
  - `RENAME_NEW_NAME`: update the name and the parent (a move is a rename that changes the parent).
  - `RENAME_OLD_NAME`: ignore it when using close-only mode.
  - `BASIC_INFO_CHANGE`: update the attributes.
  - `DATA_*`: mark the size and date as dirty.
- Apply changes in batches every 250–1000 ms under a writer lock, or swap in copy-on-write segments, so searches never stall.
- **Errors:**
  - `ERROR_JOURNAL_ENTRY_DELETED` means the journal wrapped: rescan the volume.
  - `ERROR_JOURNAL_NOT_ACTIVE` means there is no journal. `FSCTL_CREATE_USN_JOURNAL` can create one, but that is a persistent change to the volume, so ask the user first.
  - `ERROR_JOURNAL_DELETE_IN_PROGRESS`: wait, then rescan.
- **On startup:** load the snapshot. If the `UsnJournalID` matches and the saved `NextUsn` is at least the journal's `FirstUsn`, replay from the saved USN; this takes a second or less. Otherwise rescan. A full ENUM scan should take about 1–3 s per million files on an NVMe drive.

### 2.4 Fallback for FAT32, exFAT, network, no admin, or ReFS failures

- **Use the .NET enumerator:** `new FileSystemEnumerable<Entry>(root, (ref FileSystemEntry e) => ..., new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint /* avoid loops */, ReturnSpecialDirectories = false, BufferSize = 64 * 1024 })`. Underneath it calls `NtQueryDirectoryFile` with large buffers, and `FileSystemEntry` exposes the name as a span, along with length, times and attributes, without allocating. It is about as fast as hand-written `FindFirstFileExW(..., FindExInfoBasic, ..., FIND_FIRST_EX_LARGE_FETCH)` and needs no interop.
- **Parallelism:** do our own recursion with a work queue, using about `Environment.ProcessorCount` workers on SSD or NVMe and 1–2 on HDD or network drives (the drive type comes from `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceSeekPenaltyProperty`).
- **Updates:** use one `FileSystemWatcher` per root with `IncludeSubdirectories = true` and `InternalBufferSize = 65536`. On the `Error` event (buffer overflow), rescan the affected root or subtree. Debounce events, because watchers fire duplicates. For network shares, prefer periodic rescans (for example every N minutes) over watchers.

### 2.5 Elevation strategy

| Option | Pros | Cons |
|---|---|---|
| `requireAdministrator` manifest on the app | Simplest | UAC prompt on every launch. Cannot autostart through the Run key. **UIPI blocks drag-and-drop between the elevated app and non-elevated Explorer.** Files the user opens from results launch elevated, which is a security risk. Global hooks and integration get awkward. |
| Elevated helper launched on demand (`runas`) | No service install | UAC prompt per session. Lifecycle management is complicated. |
| Scheduled task "Run with highest privileges" at logon, running a headless indexer | No UAC prompt after install. No service code needed. | Runs per user. Task setup is somewhat brittle. |
| **Windows service (LocalSystem), as Everything does**, with an unelevated UI | One admin prompt at install. Indexes before the user logs in. One index shared by all users. The UI behaves like a normal app (drag/drop, open-as-user). | Requires an installer and IPC. **Every file name is visible to any client**, which matches Everything's model, but document it. |

**Recommendation:** build a `FileHound.Indexer` Windows service (`Microsoft.Extensions.Hosting.WindowsServices`) and a `FileHound.App` UI.

- **IPC:** a `NamedPipeServerStream` created with a `PipeSecurity` that grants access to Interactive Users or Authenticated Users only, and rejects remote clients (`PipeOptions.CurrentUserOnly` cannot be used across users). Use a compact binary protocol (MessagePack or hand-rolled). Run the query inside the service and send back only page windows of results (`offset`, `count`, sorted by …) plus the total count, so millions of rows never cross the pipe.
- **Fallback mode:** if the service is not installed, the UI runs **in-process with directory walking only**. This keeps the app useful as a portable exe.

### 2.6 In-memory index design (struct-of-arrays)

```csharp
sealed class VolumeIndex {
    // per entry i (≈ 20–28 B fixed)
    int[]    Parent;        // index of parent dir, -1 for root
    int[]    NameOffset;    // into NameArena / FoldArena
    ushort[] NameLength;    // chars
    ushort[] Flags;         // IsDir, Hidden, System, Deleted, ExtIndex(≤ 4096 interned exts)…
    ulong[]  CharMask;      // optional 8 B: bit per [a-z0-9 + common] present → fuzzy prefilter
    long[]   Size; long[] ModifiedUtc;   // optional, filled lazily / by raw MFT reader
    // name storage
    char[]   NameArena;     // original names, '\0'-separated
    char[]   FoldArena;     // ToLowerInvariant (or ordinal-ignore-case fold), same offsets
    int[]    RecordToIndex; // NTFS: MFT record number -> entry index
}
```

- Fixed fields cost about 20 B per entry, plus about 2×2×L bytes for the two name arenas. Names average about 15–25 characters, so a UTF-16 arena costs about 40 B per name. That puts us at roughly **100–120 B per entry**, which is about 500–600 MB for 5M files.
- Two ways to get to Everything's size, about **60–75 B per entry**:
  - (a) Store the original names as UTF-8 and keep only the folded arena as UTF-16, since UTF-16 is what `IndexOf` searches. Or keep both as UTF-8 and search with `IndexOf(ReadOnlySpan<byte>)`, which is just as vectorized.
  - (b) Drop the char mask and use **per-block (e.g. 4 KB) bitmaps** as floki does.
- Allocate arenas in large chunks (`GC.AllocateUninitializedArray`, or `NativeMemory` for anything over 2 GB). Avoid having millions of `string` objects or per-entry classes, because GC stops scale with object count.
- Sorting: keep a precomputed `int[] NameRank` per volume (each entry's position in a name-sorted order). Sorting results by name then becomes sorting ints, which a radix sort does in milliseconds for 1M items. Compute path order at query time only when the user asks for it.

### 2.7 Persistence

- Write one file per volume to `%LOCALAPPDATA%\FileHound\index\{volume-guid}.fhidx` (or ProgramData when the service owns it).
- File layout: a header with magic, version, FS type, volume serial, `UsnJournalID`, `NextUsn`, entry count and arena lengths, followed by each array dumped as raw bytes with `MemoryMarshal.AsBytes(span)` and a CRC32 or XxHash64 checksum (`System.IO.Hashing`).
- Write to a temp file and then `File.Replace`, so the swap is atomic. Write snapshots on idle, every 10–15 minutes, and at shutdown.
- Load by reading straight into pre-sized arrays (`RandomAccess.Read`). About 100 MB loads in roughly 100–300 ms on NVMe. Optional LZ4 compression (K4os.Compression.LZ4, MIT) cuts the size by about 2–3x but costs CPU.

---

## 3. Fuzzy search with sub-100 ms latency at millions of names

**Tiered pipeline.** Run one query over N volumes in parallel, with each volume split into chunks of about 64k entries:

1. **Exact and substring fast path.** Fold the query, then run `MemoryExtensions.IndexOf(foldArena, needle)` (ordinal) over the **entire arena** at once, rather than once per name. This call is vectorized in .NET 8+ with AVX2 or AVX-512 and compares packed first and last characters. Each hit offset maps back to an entry through a binary search over `NameOffset` (or an `int[]` lookup per block); then skip ahead to the next name. Scanning a 100–200 MB arena on 8 cores takes about 5–20 ms.
   - For OR terms and `ext:` lists, use `SearchValues.Create(string[], StringComparison.OrdinalIgnoreCase)` (.NET 9+; Aho-Corasick or Teddy internally). Use `SearchValues<char>` for separators.
   - Wildcards `*` and `?` compile to a split-on-`*` sequence of IndexOf calls with `?` handled by position, not to Regex. Use `regex:` only when the user asks for it, with `RegexOptions.Compiled | RegexOptions.NonBacktracking` and a timeout.
2. **Subsequence (fzf-style) tier.** Prefilter with `(queryMask & ~entryMask) == 0` using the 64-bit char mask (or block bitmaps). Then score the survivors with **fzf FuzzyMatchV1**: a greedy forward scan followed by a backward pass that tightens the match. Apply fzf's scoring constants (MIT, port with attribution, [algo.go](https://github.com/junegunn/fzf/blob/master/src/algo/algo.go)):
   - match +16
   - gap start −3, gap extension −1
   - word boundary +8, after whitespace +10, after a delimiter (`\ / _ - .`) +9
   - camelCase or letter-digit transition +7
   - consecutive +4
   - first character ×2

   Rescore only the **top ~1k with V2** (Smith-Waterman-like, O(nm)) to find the best alignment for highlighting.
3. **Typo tier.** Run this only when tiers 1–2 return fewer than about 50 hits, or always for queries of 4+ characters at low priority. Use bounded approximate substring matching: **Myers' 1999 bit-parallel algorithm** for patterns of at most 64 characters, which finds the minimum edit distance from the query to *any substring* of the name. Use **Hyyrö's extension** for Damerau transpositions. Allow k = 1 for 4–6 characters and k = 2 for 7 or more. Prefilter with `popcount(queryMask & ~entryMask) <= k`. Cost is O(len) per name with a handful of ALU operations, so 5M names finish in about 10–20 ms across 8 cores.
4. **Trigram index:** skip it. 5M names at about 18 trigrams each produce 90M postings, or about 360 MB, which defeats the memory budget. Block bitmaps and brute-force SIMD are fast enough at this scale, and they are what Everything and floki rely on.

**Ranking** (lexicographic tier, then score, then tie-breakers):
`exact name` > `name prefix` > `word-boundary start` (after space, `_`, `-`, `.`, or a camelCase hump) > `contiguous substring` > `fzf subsequence score` > `typo (distance 1, then 2)`.
Tie-breakers: a match in the name beats a match in the path; frecency (how often and how recently the user opened the item, stored locally); shorter name; shallower path; folder or file preference; more recent mtime. Use a **bounded top-K heap** (for example K = 5,000 for display) and still report the full match count. For plain substring queries with no ranking, sort by `NameRank`.

**Responsiveness:**
- Debounce keystrokes by about 30–60 ms, and cancel the previous search through `CancellationToken`.
- When the new query extends the old one (a user typing "pyth" then "pytho"), search only within the previous result set, as floki does.
- Stream the first page to the UI before the total count is final.

**Libraries:**
- [Raffinert.FuzzySharp](https://github.com/Raffinert/FuzzySharp) (MIT) is a bit-parallel rewrite that runs about 28x faster than [JakeBayer/FuzzySharp](https://github.com/JakeBayer/FuzzySharp) (MIT), with almost no allocations. Both implement FuzzyWuzzy whole-string ratios (`Ratio`, `PartialRatio`, `TokenSetRatio`) over `string` inputs. Those are the wrong semantics for filename search, and they are too slow for a 5M scan. They are acceptable for re-ranking a few hundred candidates.
- [Quickenshtein](https://github.com/Turnerj/Quickenshtein) (MIT, SIMD) and Fastenshtein (MIT) compute *full-string* Levenshtein, which is also the wrong semantics, since we need substring distance.
- **Verdict:** write the hot path ourselves. It is about 400 lines: fzf V1/V2 ports, Myers/Hyyrö matching, and mask prefilters. Every inner loop should run over `ReadOnlySpan<char>` slices of the arena, use `Parallel.For` across partitions, and allocate nothing.

---

## 4. Query syntax: the subset to ship

Everything's syntax is documented [here](https://www.voidtools.com/support/everything/searching/). Recommended v1 subset:

| Syntax | Meaning |
|---|---|
| `foo bar` | AND (space) |
| `foo \| bar` | OR |
| `!foo` | NOT |
| `"foo bar"` | phrase containing a literal space |
| `< >` | grouping (Everything uses angle brackets; also accept `( )`) |
| `*` and `?` | wildcards. A term containing a wildcard must match the whole name, as in Everything. |
| `\` inside a term, or `path:` | match against the full path instead of the name |
| `ext:pdf;docx` | extension list |
| `file:` and `folder:` | restrict the result type |
| `size:>10mb`, `size:1mb..1gb`, `size:empty\|tiny\|small\|medium\|large\|huge\|gigantic` | size filters. The named buckets are 0, <10 KB, <100 KB, <1 MB, <16 MB, <128 MB, and >128 MB. |
| `dm:today\|yesterday\|thisweek\|lastweek\|2026-10\|>2026-01-01` | date modified (also `dc:`) |
| `parent:C:\x` | direct children of a folder |
| `case:`, `ww:`, `regex:` | case-sensitive, whole word, regular expression |
| macros `doc:`, `pic:`/`image:`, `video:`, `audio:`, `zip:`, `exe:` | map to the UI filter chips (see §6) |

Defaults: matching is case-insensitive. A term without a wildcard matches anywhere in the name and falls back to fuzzy. A term with a wildcard matches exactly. Parse with a small hand-written recursive-descent parser that produces an AST of `IPredicate` nodes. Run cheap predicates (flags, `ext:` via an interned extension id) before name matching.

---

## 5. UI framework

| | **WPF (.NET 10) + WPF-UI** | **WinUI 3 / Windows App SDK** | **Avalonia 11** |
|---|---|---|---|
| Virtualization at 100k–10M rows | Mature. `VirtualizingStackPanel` with `VirtualizationMode=Recycling`, `ScrollUnit=Item`, and an `ItemsSource` set to a custom non-generic `IList` that materializes rows lazily, so it never copies millions of items. Used by EverythingToolbar, Lertaro and Tachyon. | `ItemsView`/`ListView` virtualize well and scroll smoothly. Very large collections need the same lazy `IList` trick, and the community has less experience with that. | Good. `ListBox` and the standard DataGrid virtualize. **TreeDataGrid now needs a commercial Accelerate license (or AGPL) since 11.2**, so avoid it. |
| Clay styling (rounded corners, soft shadows, pastels) | `CornerRadius` on Border is easy. `DropShadowEffect` is GPU-rendered but expensive to put on every row, so apply it to cards and containers only. **No native inset shadow**: fake it with a blurred inner border plus an `OpacityMask` or layered gradients. | `CornerRadius` everywhere, `ThemeShadow`, and Mica/Acrylic built in. No inset shadows without Composition API work. | **`BoxShadow` supports multiple shadows and `inset`**, which gives the best claymorphism out of the box. CSS-like styles. |
| Windows 11 look | WPF-UI (MIT, ~9.7k stars) provides `FluentWindow`, Mica, a Snap Layout title bar, a tray icon and dialogs. .NET 9+ also ships the built-in Fluent theme (`ThemeMode`). | Native | Fluent theme, but Mica only through extra work |
| Unpackaged single exe | Trivial: self-contained, `PublishSingleFile`, ReadyToRun. No trimming or AOT. | Possible (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), but more friction and a larger output. | Trivial, and supports AOT. |
| Tooling | VS designer, Hot Reload, a huge ecosystem, simple shell interop (HWND, `DoDragDrop`) | No XAML designer; debugging and the build pipeline have rough edges | Previewer and a good ecosystem; cross-platform (not needed here) |

**Recommendation: WPF on .NET 10 with WPF-UI.** Shell integration is simplest in WPF (HWND hooks, OLE drag-drop, `IContextMenu`, `RegisterHotKey`), its virtualization is proven for this kind of app, and three comparable open-source apps already ship with it. For the clay look, build a small control library:

- `ClayCard` (outer soft shadow plus a faux inner highlight and shadow via two gradient borders)
- `ClayChip`, `ClaySearchBox`
- a pastel palette as theme resources with light and dark variants

Keep result rows **flat**, with no per-row effects: just a hover and selection fill. Put shadows only on containers. Choose **Avalonia** instead only if real inset shadows and AOT outweigh the shell-interop convenience.

Performance rules for the list:
- Use one `ListView` + `GridView` (or a custom lightweight `VirtualizingPanel`).
- Do not rebind per row. Replace `ItemsSource` with a new lazy-list wrapper for each query.
- Load icons asynchronously from a cache keyed by extension.
- Set `ScrollViewer.IsDeferredScrollingEnabled=False` and `VirtualizingPanel.CacheLength="1,2"`.

---

## 6. UX features of best-in-class tools

- **Global hotkey:** `RegisterHotKey`, configurable. Default to something like `Ctrl+Alt+Space`, because Alt+Space is taken by PowerToys Command Palette and Win+Alt+S by EverythingToolbar. Use NHotkey (Apache-2.0) or a ten-line P/Invoke. Show or hide the window instantly: keep the process resident with a tray icon (H.NotifyIcon, MIT, or WPF-UI's tray) and focus the search box on show.
- **Instant as you type:** search on every keystroke after a 30–60 ms debounce. The first page should appear in under 50 ms. Show the result count with timing, for example "1,092,658 results · 18 ms", plus a per-volume index status indicator.
- **Keyboard:**
  - Up/Down and PgUp/PgDn move through results; Enter opens.
  - `Ctrl+Enter` opens the containing folder. `Ctrl+Shift+C` copies the full path; `Ctrl+C` copies the file itself (`FileDrop`).
  - `Shift+Enter` runs as admin; `Alt+Enter` shows Properties.
  - `Esc` clears the query, or hides the window if it is already empty. `Tab` cycles through the filter chips.
- **Actions:**
  - **Open:** `ShellExecuteEx` (`UseShellExecute = true`). Always launch from the unelevated UI process.
  - **Open containing folder:** `SHOpenFolderAndSelectItems`, which reuses an Explorer window and can select several items. This is better than `explorer /select,`.
  - **Properties:** `SHObjectProperties`, or `ShellExecuteEx` with the "properties" verb and `SEE_MASK_INVOKEIDLIST`.
  - **Run as admin:** the "runas" verb.
- **Shell context menu:** `SHParseDisplayName`, then `IShellFolder.GetUIObjectOf(IID_IContextMenu)`, then `QueryContextMenu`/`TrackPopupMenuEx`/`InvokeCommand`. Write this ourselves with CsWin32 rather than copying the CPOL CodeProject class. Prepend our own items (Copy path, Copy name, Open in terminal).
- **Drag out:** `DragDrop.DoDragDrop(row, new DataObject(DataFormats.FileDrop, paths), Copy | Move | Link)`. This works only because the UI is not elevated.
- **Icons:**
  - Most files: `SHGetFileInfo(ext, FILE_ATTRIBUTE_NORMAL, SHGFI_USEFILEATTRIBUTES | SHGFI_SYSICONINDEX)`, cached by extension, then pull images from `SHGetImageList(SHIL_SMALL/SHIL_LARGE)`.
  - Per file for `.exe`, `.lnk`, `.ico`, `.url`, and for folders with custom icons.
  - Optional thumbnails via `IShellItemImageFactory.GetImage`.
  - Do all of this on a dedicated STA background thread, freeze the `BitmapSource` objects, and use an LRU cache.
- **Type filter chips** (map to macros and an interned extension-id bitset for speed):
  - **Documents:** pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt md csv epub
  - **Images:** jpg jpeg png gif bmp tif tiff webp heic avif svg ico raw cr2 nef arw dng psd
  - **Video:** mp4 mkv avi mov wmv webm m4v mpg mpeg flv 3gp ts
  - **Audio:** mp3 flac wav aac m4a ogg opus wma aiff mid
  - **Archives:** zip 7z rar tar gz tgz bz2 xz zst iso cab
  - **Executables:** exe msi bat cmd ps1 com scr lnk appx msix
  - **Folders:** the IsDir flag
- **Other features users expect:**
  - Match highlighting in the name, from fzf positions.
  - Sortable columns (Name, Path, Size, Modified, Type).
  - Include/exclude folders and drives, with defaults that skip `C:\Windows\WinSxS` and `$Recycle.Bin` but let the user opt in.
  - Hidden and system file toggles.
  - Search history and frecency.
  - A preview pane, or hand-off to QuickLook/Seer.
  - Start with Windows.
  - A light/dark/system theme following `UISettings` and the `AppsUseLightTheme` registry value.
  - An "index building" progress state per volume.
  - Portable mode (no service, directory walking only).

---

## Suggested build order

1. Interop for volume discovery and the `FSCTL_ENUM_USN_DATA` scanner, then the struct-of-arrays index and path rebuild, then a console benchmark against the target of at most 2 s per 1M files.
2. Substring search over the arena, with SIMD and parallelism, then the query parser for the §4 subset.
3. Snapshot persistence and USN journal replay and monitoring.
4. The WPF-UI shell: virtualized list, icons, actions, hotkey, tray.
5. The service split with named-pipe IPC, and the fallback walker for non-NTFS volumes.
6. Fuzzy tiers 2–3 and ranking with frecency. Sizes and dates come lazily at first, then from a raw MFT parser.

## Sources

- MS Docs: [FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data), [FSCTL_READ_USN_JOURNAL](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_read_usn_journal), [USN_RECORD_V3](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v3), [Walking a buffer of change journal records](https://learn.microsoft.com/en-us/windows/win32/fileio/walking-a-buffer-of-change-journal-records)
- voidtools: [search syntax](https://www.voidtools.com/support/everything/searching/), [memory usage thread](https://www.voidtools.com/forum/viewtopic.php?t=12117), [why other software doesn't use the MFT](https://www.voidtools.com/forum/viewtopic.php?f=7&t=5433)
- Repos: [Lertaro](https://github.com/Lertaro/Lertaro), [tachyon](https://github.com/cyb3rcr4t0712/tachyon), [UsnParser](https://github.com/wangfu91/UsnParser), [UsnExplorer](https://github.com/bighamx/UsnExplorer), [floki](https://github.com/nikkoxgonzales/floki), [UltraFastFileSearch](https://github.com/skyllc-ai/UltraFastFileSearch), [FastFileSearch](https://github.com/ChrisS85/FastFileSearch), [Fake-Everything](https://github.com/LeiHao0/Fake-Everything), [EverythingToolbar](https://github.com/srwi/EverythingToolbar), [Flow Launcher](https://github.com/Flow-Launcher/Flow.Launcher), [SwiftSearch](https://sourceforge.net/projects/swiftsearch/), [fzf algo.go](https://github.com/junegunn/fzf/blob/master/src/algo/algo.go), [Raffinert.FuzzySharp](https://github.com/Raffinert/FuzzySharp), [FuzzySharp](https://github.com/JakeBayer/FuzzySharp), [Quickenshtein](https://github.com/Turnerj/Quickenshtein), [WPF-UI](https://github.com/lepoco/wpfui), [Avalonia TreeDataGrid license change](https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid/issues/307)
- UI comparisons: [WinUI vs WPF 2026](https://www.ctco.blog/posts/winui-vs-wpf-2026-practical-comparison/), [WPF vs Avalonia 2026](https://wpfsharp.com/wpf-vs-avalonia-2026/). Their benchmark numbers are blog-level, so treat them as indicative only.
