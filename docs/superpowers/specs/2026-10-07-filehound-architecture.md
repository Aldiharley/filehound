# FileHound — Architecture & C#/.NET Technical Spec

**Date:** 2026-10-07 · **Companion:** `2026-10-07-filehound-design.md` (requirements FR-x / G-x referenced below)

## 1. Technology baseline

| Item | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10 (LTS)**, `net10.0` / `net10.0-windows` | C# 14, nullable enabled, implicit usings |
| UI | **WPF** | Custom "Clay" resource dictionary, no third-party theme library |
| MVVM | `CommunityToolkit.Mvvm` (MIT) | Source-generated `[ObservableProperty]` and `[RelayCommand]` |
| Tray | `H.NotifyIcon.Wpf` (MIT) | `TaskbarIcon` |
| Hashing | `System.IO.Hashing` (MIT, Microsoft) | `XxHash64` snapshot checksum |
| Interop | Hand-written `[LibraryImport]` P/Invoke | Kernel32 and Shell32 only; no CsWin32, to keep the build simple |
| Tests | xUnit + `Microsoft.NET.Test.Sdk` | Core and Indexing test projects |
| Packaging | Framework-dependent `dotnet publish`, plus an optional self-contained single-file build | `asInvoker` manifest |

Third-party *code* adapted: fzf's scoring constants and the shape of its algorithm (MIT, © Junegunn Choi). They are credited in `THIRD-PARTY-NOTICES.md`. No GPL, LGPL, MPL or unlicensed code is copied.

## 2. Solution layout

```
FileHound.sln
├─ src/
│  ├─ FileHound.Core/            net10.0          pure logic, no Win32
│  │  ├─ Index/                  VolumeIndex, EntryFlags, FileCategory, Categorizer, PathBuilder
│  │  ├─ Query/                  QueryParser, Query AST (Clause, Term, Filters), SizeParser, DateParser
│  │  ├─ Matching/               FuzzyScorer (fzf V1), TypoMatcher (Myers), WildcardMatcher, TermMatcher
│  │  ├─ Search/                 SearchEngine, SearchRequest/Result, SearchHit, BoundedHeap, HitComparers
│  │  ├─ Persistence/            SnapshotSerializer
│  │  └─ Stats/                  IndexStatistics
│  ├─ FileHound.Indexing/        net10.0-windows  Win32 indexing
│  │  ├─ Interop/                Kernel32 (CreateFile, DeviceIoControl, GetVolumeInformation), structs
│  │  ├─ Drives/                 DriveDiscovery, DriveDescriptor
│  │  ├─ Ntfs/                   MftScanner, UsnJournalReader, UsnRecordParser, UsnUpdater
│  │  ├─ Walk/                   DirectoryWalker, WatcherUpdater, MetadataFiller
│  │  ├─ IndexManager.cs         orchestration, drive states, snapshots, timers
│  │  └─ Elevation.cs            IsElevated
│  └─ FileHound.App/             net10.0-windows  WPF exe (WinExe)
│     ├─ Assets/                 mascot PNGs, clay icons, app.ico
│     ├─ Themes/                 Colors.xaml, Typography.xaml, Controls.xaml (Clay styles)
│     ├─ Controls/               DonutChart, HighlightTextBlock behavior, ClayIcon
│     ├─ Services/               Settings, Shell, ShellIcons, Hotkey, Tray, Startup, SingleInstance, Log
│     ├─ ViewModels/             Main, Dashboard, Search, Drives, Settings, ResultItem, DriveItem
│     └─ Views/                  MainWindow, DashboardView, SearchView, DrivesView, SettingsView
├─ tests/
│  ├─ FileHound.Core.Tests/
│  └─ FileHound.Indexing.Tests/
└─ tools/FileHound.Cli/          net10.0-windows  console: scan/search/bench (for validation)
```

Dependency direction: `App → Indexing → Core`, `Cli → Indexing → Core`. Core has **no** Windows dependencies, so it can be unit-tested anywhere.

## 3. Core: the in-memory index (`VolumeIndex`)

One `VolumeIndex` per drive. It uses a struct-of-arrays layout and grows by doubling.

```csharp
public sealed class VolumeIndex
{
    public string Root { get; }          // "C:\"
    public IndexMode Mode { get; set; }  // Turbo | Standard
    public int Count { get; }            // allocated entries (incl. deleted)
    public int LiveCount { get; }

    // per-entry arrays (index = entry id, stable until rebuild)
    int[]        _parent;     // -1 => root entry
    int[]        _nameStart;  // offset into _names/_fold
    ushort[]     _nameLen;
    EntryFlags[] _flags;      // Directory, Hidden, System, ReparsePoint, Deleted, MetadataKnown
    byte[]       _category;   // FileCategory
    byte[]       _depth;      // 0 = root, capped at 255
    long[]       _size;       // bytes, 0 for dirs
    long[]       _modified;   // UTC ticks (DateTime.Ticks), 0 = unknown

    char[] _names;            // original-case names, contiguous
    char[] _fold;             // lower-invariant copy, same offsets
    int    _arenaLength;

    int[]? _recordMap;        // Turbo only: NTFS record number -> entry id (-1 = none)
    Dictionary<ChildKey,int>? _children; // lazy (parent, fold-hash) -> entry, see below
    public ReaderWriterLockSlim Lock { get; }
}
```

- **Record map (Turbo):** `int[] recordToEntry`, indexed by `FRN & 0x0000_FFFF_FFFF_FFFF` (the MFT record number), with -1 meaning absent. It grows when needed. This costs 4 B per MFT record, not 24+ B as a `Dictionary` would.
- **Child lookup (path → entry):** `Dictionary<ChildKey, int>`, where `ChildKey` is `(int Parent, int FoldHash)`. It is built lazily on the first use (by `FindChild`, `FindByPath`, the watcher updater or the metadata filler) and kept up to date by `Add`, `Rename` and `Delete` once it exists. A hash collision falls back to scanning the parent's children for the exact name.
- **Mutations** (`Add`, `Rename`, `Delete`, `SetMetadata`) take the **write lock**. Searches take the **read lock** for each volume. A rename appends the new name to the arena; the space it leaves behind is reclaimed when the snapshot is reloaded, because the snapshot writes a compacted copy.
- **Deleting a directory** marks every live descendant as deleted. This costs O(n·depth) and is acceptable for occasional events.
- **Path building:** `PathBuilder.GetFullPath(index, entry)` walks the parents into a stack buffer (`stackalloc char[1024]`, with a heap fallback). The root entry's name is `"C:"` and its path is `"C:\"`.
- **Memory:** fixed fields take about 26 B per entry, plus about 2 × 2 × average name length (about 20) for the arenas, so roughly 110 B per entry. The lazy child dictionary adds about 28 B once it is built, giving about 140 B, which is still under the 160 B budget. `EntryFlags` is a `[Flags] enum : ushort`.
- **Categories:** `Categorizer` maps an extension (from the fold name) to a `FileCategory` (Folder, Document, Image, Video, Audio, Archive, App, Code, Other) using a static `FrozenDictionary<string, FileCategory>` searched by span via `GetAlternateLookup<ReadOnlySpan<char>>()`.

`VolumeIndexBuilder` is a bulk path that skips the locks. It appends raw records `(id, parentId, name, attrs, size, mtime)`, then `Build()` resolves parents through the id map, computes depth, assigns categories and produces a `VolumeIndex`. Both the MFT scan and snapshot loading use it.

## 4. Core: query language

### 4.1 AST
```
Query      := Clause*                       // implicit AND between clauses
Clause     := Alternative ('|' Alternative)* , Negated flag (prefix '!')
Alternative:= Term | Filter
Term       := Text (folded), Kind {Plain, Wildcard, PathTerm}, precomputed matcher state
Filter     := ExtFilter(set) | CategoryFilter(set) | KindFilter(File|Folder)
            | SizeFilter(min,max) | DateFilter(minTicks,maxTicks) | DriveFilter(letter)
            | PathFilter(Term)
```
- The tokenizer splits on whitespace outside quotes. `!` at the start of a token negates its clause. `|` inside a token (or as its own token) joins alternatives (Everything semantics: `a b|c` means `a AND (b OR c)`).
- `folder:` alone becomes `KindFilter(Folder)`; `folder:abc` becomes `KindFilter(Folder)` plus the term `abc`. `file:` works the same way.
- `type:` and `kind:` accept `doc|document(s)|image(s)|pic|video(s)|audio|music|archive(s)|zip|app(s)|exe|code|folder|file`, separated by `;` or `,`.
- `size:` grammar: `[<|>|<=|>=]N[unit]`, `N[unit]..M[unit]`, or a bucket. Units are base 1024. Buckets: empty = 0, tiny < 10 KB, small < 100 KB, medium < 1 MB, large < 16 MB, huge < 128 MB, gigantic ≥ 128 MB.
- `dm:` keywords are resolved against local time at parse time and converted to a half-open UTC ticks range. `IClock` is injectable for tests.
- A parse error (for example `size:abc`) does not throw: the filter is dropped and `Query.Errors` lists it, and the UI shows a soft warning chip.
- An empty query with no filters returns an empty result. A filter-only query matches everything that passes the filters.

### 4.2 Evaluation order (per entry)
1. The flags gate: deleted entries are always rejected, and hidden or system entries are rejected when `IncludeHidden` is false.
2. Clauses made only of filters, which are cheap: kind, category, ext, drive, size, date.
3. Term clauses, which run the name matchers (§5).
4. Path clauses, which are the most expensive (§4.3).

Negated clauses match only by exact substring, never fuzzily, so `!tmp` excludes names that contain "tmp".

### 4.3 Path matching
- **A term without `\`:** for `path:foo`, an entry matches if its own fold name contains `foo`, or if any ancestor directory's fold name contains `foo`. The ancestor check is memoized for each search in a `byte[]` holding 0 (unknown), 1 (yes) or 2 (no) per entry, allocated only when a path clause exists.
- **A term with `\`**, such as `docs\rep` or `path:src\core`: the entry's name must contain the last segment (a cheap prefilter). Only then is the full fold path built and searched with `IndexOf`.

## 5. Core: matching & ranking

### 5.1 Tiers (FR-10)
```csharp
public enum MatchTier : byte { Exact = 0, Prefix = 1, WordStart = 2, Substring = 3, Subsequence = 4, Typo = 5, None = 255 }
```
`TermMatcher.Match(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, Term t, MatchOptions o, out int score)`:
1. **Wildcard terms** use `WildcardMatcher.IsMatch(fold, pattern)`, an iterative glob that backtracks on the last `*` and needs no allocation. The whole name must match. The tier is Exact.
2. **`fold.IndexOf(term)`** (ordinal, vectorized):
   - The whole fold equals the term, or the term equals the stem before the last `.`: **Exact**.
   - The term is found at index 0: **Prefix**.
   - Any occurrence sits at a word boundary: **WordStart**. A boundary is the start of the name, a position after one of `space _ - . ( [ { , ; + @ #`, or a camel/digit transition (a lower-to-upper change or a letter-to-digit change in the original-case name).
   - The term is found elsewhere: **Substring**.
3. **Subsequence (fzf V1)**, only when `o.Fuzzy` is set. First a char-mask prefilter: a `ulong` mask of `[a-z0-9]` plus an "other" bit for each term, computed over the fold name in one pass, which is cheap because names are short. Then a forward greedy scan, then a backward scan to tighten the window, then fzf scoring. A **noise guard** rejects the match if the window length is greater than `max(3·m, m + 12)`, where m is the term length. Tier: **Subsequence**.
4. **Typo (Myers 1999, bit-parallel, semi-global)**, only when `o.Typo` is set, the term has 4–64 characters, and the term contains no wildcard. k = 1 for 4–6 characters and k = 2 for 7 or more. The prefilter is `popcount(termMask & ~nameMask) <= k`. Tier: **Typo**. The score is `100 − 30·distance`.

The **score within a tier** is the fzf score of the matched window for tiers 0–4, which gives boundary, consecutive and first-character bonuses.

### 5.2 fzf scoring constants
`match 16, gapStart −3, gapExt −1, boundary 8, nonWord 8, camel123 7, consecutive 4, firstCharMultiplier 2, boundaryWhite 10, boundaryDelimiter 9`. The character classes are white, nonWord, delimiter (`\ / , : ; |`), lower, upper and number. For filenames, `_ - .` are delimiters and space is white.

### 5.3 Multi-term combination
The entry's tier is the **worst** tier across its matched positive term clauses, and its score is the **sum** of their scores. When a clause has OR alternatives, the best alternative is used.

### 5.4 Ranking keys
- **Relevance:** the comparison runs in this order:
  - tier, ascending (lower is better);
  - score, descending;
  - the name matched, ahead of a path-only match;
  - name length, ascending;
  - depth, ascending;
  - modified, descending;
  - entry id, which keeps the order stable.

  These are packed into a `long` key so the hot path compares one integer:
  `key = (255−tier)<<52 | clamp(score+2048, 0..4095)<<40 | nameBit<<39 | (255−min(len,255))<<31 | (255−depth)<<23 | (modifiedDays & 0x7FFFFF)`. A higher key is better. `modifiedDays` is days since 2000, clamped to 23 bits.
- **Size:** `size` descending. **Modified:** `modified` descending. **Name:** ordinal fold-name comparison, which uses a comparer instead of a key.

### 5.5 Search engine
```csharp
public sealed record SearchRequest(string Text, SortMode Sort = SortMode.Relevance, int MaxResults = 5000,
                                   bool Fuzzy = true, bool IncludeHidden = true, FileCategory? Category = null);
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCount, TimeSpan Elapsed,
                                  IReadOnlyList<string> Errors, bool UsedTypoPass);
public readonly record struct SearchHit(VolumeIndex Volume, int Entry, MatchTier Tier, int Score, long Key);

public sealed class SearchEngine
{
    public SearchResult Search(IReadOnlyList<VolumeIndex> volumes, SearchRequest request, CancellationToken ct);
    public static IReadOnlyList<(int Start, int Length)> GetHighlights(string name, Query query); // for top hits only
}
```
- **Work partitioning:** the volumes are split into chunks of 32,768 entries and processed with `Parallel.For` (`MaxDegreeOfParallelism = Environment.ProcessorCount`). Each worker:
  - keeps a local `BoundedHeap<SearchHit>` of capacity `MaxResults`;
  - accumulates a local count that it adds to the total with `Interlocked.Add`;
  - checks `ct` once per chunk.
- **Read locks:** each worker takes the volume's read lock for each chunk. Locks are not held across chunks, so writers can interleave.
- **Two passes:**
  - Pass 1 allows tiers 0–4.
  - Pass 2 runs only if all of these hold:
    - the Pass 1 total is below 200;
    - fuzzy is on;
    - some positive term is typo-eligible.

    It re-runs the scan with the typo tier enabled, and its result replaces Pass 1. That is safe because Pass 2's results are a superset of Pass 1's.
- **Merge:** the per-worker heaps are merged and sorted by the comparer for the active sort.
- **Budget:** 2M entries and mixed queries at a median of ≤ 150 ms on CI (the perf test). The target is about 30–60 ms on an 8-core desktop.

## 6. Core: persistence (`SnapshotSerializer`)

File: `%LOCALAPPDATA%\FileHound\index\{letter}_{serialHex}.fhx`
```
Header (little-endian):
  magic "FHX1" (4) | version u16 (=1) | mode u8 | reserved u8
  root (u16 length + UTF-16 chars)
  volumeSerial u32 | usnJournalId u64 | nextUsn i64 | savedUtcTicks i64
  entryCount i32 | arenaLength i32
Arrays (raw via MemoryMarshal.AsBytes):
  parent i32[n] | nameStart i32[n] | nameLen u16[n] | flags u16[n] | size i64[n] | modified i64[n]
  names char[arenaLength]          (fold, category and depth are recomputed on load)
Trailer: XxHash64 of everything above (u64)
```
- On save, deleted entries are left out and the survivors renumbered, so the file is a compacted copy. This happens on a background thread while holding the read lock: the arrays are copied quickly, then written outside the lock.
- On load, any mismatch (magic, version, hash or length) causes the file to be deleted and `null` to be returned.
- Writes are atomic: data goes to `*.tmp`, then `File.Move(tmp, final, overwrite: true)`.

## 7. Indexing (Windows)

### 7.1 Drive discovery
`DriveDiscovery.GetDrives()` returns each drive from `DriveInfo.GetDrives()` where `IsReady` is true and `DriveType` is `Fixed` or `Removable`, as a `DriveDescriptor`:
- `Letter`, `Root`, `Format`, `Label`, `TotalSize`, `FreeSpace`;
- `Serial`, from `GetVolumeInformationW`;
- `IsNtfs`.

`IndexManager` polls every 5 s to detect drives that come and go (FR-7).

### 7.2 Turbo: `MftScanner` (FR-2)
1. Open the volume with `CreateFileW(@"\\.\C:", GENERIC_READ, FILE_SHARE_READ|FILE_SHARE_WRITE, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS)`.
2. `FSCTL_QUERY_USN_JOURNAL` (`0x000900F4`) returns `USN_JOURNAL_DATA_V1`, which gives the journal id and `NextUsn`. If this fails, the drive falls back to Standard mode.
3. Loop `FSCTL_ENUM_USN_DATA` (`0x000900B3`) with `MFT_ENUM_DATA_V1 { Start=0, Low=0, High=NextUsn, Min=2, Max=3 }` and a 1 MB pinned buffer. The first 8 bytes of each buffer are the next start FRN. Stop on `ERROR_HANDLE_EOF` (38).
4. Parse V2 and V3 records by offset (§ research 2.2). For V3, take the low 64 bits of the 128-bit ID; NTFS fits in them, and anything that does not fit falls back to Standard. Each record goes to the builder as `(recordNo, parentRecordNo, name, attrs)`.
5. Add a synthetic root entry for record 5 named `"C:"`. Mark orphans (no resolvable parent) as deleted. Skip names that start with `$` whose parent is the root and that are NTFS metafiles (`$MFT`, `$LogFile`, `$Extend`, …), along with `$Recycle.Bin` and `System Volume Information`. User excludes are applied after the build, by marking the matched subtrees deleted.
6. Report progress as `records / estimated` through `IProgress<ScanProgress>`.

After the scan, `MetadataFiller` runs on a background thread. It visits directories in parallel: for each one it builds the path, enumerates it with `FileSystemEnumerable`, matches each child via `FindChild`, and calls `SetMetadata(size, mtime)`. Batches of 4,096 updates are applied under the write lock.

### 7.3 Turbo live updates: `UsnUpdater` (FR-5)
- **Polling loop:** one long-running task per drive calls `FSCTL_READ_USN_JOURNAL` (`0x000900BB`) every 500 ms with `READ_USN_JOURNAL_DATA_V1 { StartUsn=saved, ReasonMask=CREATE|DELETE|RENAME_NEW|RENAME_OLD|DATA_*|BASIC_INFO|CLOSE, ReturnOnlyOnClose=1, Timeout=0, BytesToWaitFor=0, JournalId }`. Because `BytesToWaitFor` is 0 the call returns immediately, so cancellation is easy.
- **Applying records**, under one write lock per batch. The `FILE_DELETE` check is decisive: a record that carries it gets only that handling.
  - `FILE_DELETE`: delete the entry by record number.
  - Otherwise, `FILE_CREATE` or `RENAME_NEW_NAME`: upsert the entry with its new name and parent. Upsert means add it if the record is unknown, and rename or move it if it is known.
  - In addition, `DATA_*` or `BASIC_INFO_CHANGE`: queue a metadata refresh. The refresh is a `File.GetAttributes` and `FileInfo` stat done outside the lock.
- **Errors:**
  - `ERROR_JOURNAL_ENTRY_DELETED` (1181) or a journal ID mismatch: rescan the drive.
  - `ERROR_JOURNAL_NOT_ACTIVE` (1179): switch the drive to Standard mode.

### 7.4 Standard: `DirectoryWalker` (FR-2/FR-3)
- **Workers:** `min(ProcessorCount, 8)` workers on fixed drives and 2 on removable ones, pulling from a `Channel<(string path, int entry)>`.
- **Enumeration:** each directory is read with `FileSystemEnumerable<RawEntry>` and `EnumerationOptions { RecurseSubdirectories=false, IgnoreInaccessible=true, AttributesToSkip=0, ReturnSpecialDirectories=false, BufferSize=65536 }`. The transform captures the name, attributes, `Length` and `LastWriteTimeUtc` from `FileSystemEntry`.
- **Adding entries:** a directory's children are added in **one write-lock acquisition per directory**. That returns their entry ids, and subdirectories are enqueued with their ids. Reparse-point directories are indexed but **not descended into**, which avoids junction cycles and duplicates.
- **Excludes:** these paths are skipped: `$Recycle.Bin`, `System Volume Information`, and the user's excluded paths (compared case-insensitively as prefixes).
- **Progress and errors:** directories visited, entries and inaccessible-count are all reported. An `UnauthorizedAccessException` or `IOException` on a directory increments the skip counter.
- **Two modes:** on a **first run** (no snapshot) the walker writes straight into the live published index, so results stream in as it goes. On a **refresh** after a snapshot load, it builds a new index and then swaps it in atomically (`IndexManager.Replace`).

### 7.5 Standard live updates: `WatcherUpdater` (FR-5)
- **Setup:** one `FileSystemWatcher(root)` per drive with `IncludeSubdirectories=true`, `InternalBufferSize=65536` and `NotifyFilter = FileName|DirectoryName|Size|LastWrite`. Events go into a `ConcurrentQueue`.
- **Draining:** a timer drains the queue every 500 ms under the write lock:
  - Created: resolve the parent with `FindByPath` and stat the item to add it. If it is a directory, walk its subtree with the walker.
  - Deleted: delete the entry and its subtree.
  - Renamed: rename or move the entry.
  - Changed: queue a metadata refresh.
- **Overflow:** the `Error` event (buffer overflow) triggers a rescan of that drive.

### 7.6 `IndexManager` (orchestration)
```csharp
public sealed class IndexManager : IAsyncDisposable
{
    public IReadOnlyList<DriveState> Drives { get; }     // observable snapshot list
    public IReadOnlyList<VolumeIndex> Volumes { get; }   // ready-or-partial indexes for search
    public event EventHandler? StateChanged;             // throttled to 4 Hz
    public event EventHandler? IndexChanged;             // after live-update batches (UI re-runs query)
    public Task StartAsync(IndexOptions options, CancellationToken ct);
    public Task RescanAsync(char letter);
    public Task SaveSnapshotsAsync();
}
public sealed record DriveState(DriveDescriptor Drive, IndexMode Mode, DriveStatus Status, long Entries,
                                double Progress, int Skipped, bool MetadataComplete, string? Error);
public enum DriveStatus { Loading, Scanning, FillingDetails, Ready, Offline, Error }
```
- **Startup sequence (each drive in parallel):**
  1. Load the snapshot, if any, and publish it. Status becomes Ready (stale).
  2. If the process is elevated and the drive is NTFS:
     - With a valid snapshot whose journal ID matches and whose `nextUsn` is at least the journal's `FirstUsn`, start the `UsnUpdater` from the saved USN.
     - Otherwise, run the `MftScanner`, publish, run the `MetadataFiller`, then start the `UsnUpdater`.
  3. Otherwise, run the `DirectoryWalker`. It streams results on a first run and swaps them in on a refresh. Then start the `WatcherUpdater`.
- **Snapshots** are saved on exit and every 15 minutes when anything is dirty.
- **Failure handling:** any exception during a Turbo step logs it and falls back to Standard for that drive (FR-2).

### 7.7 Elevation
- `Elevation.IsElevated` checks `WindowsPrincipal.IsInRole(Administrator)`.
- The App relaunches through `ProcessStartInfo { Verb="runas", UseShellExecute=true, Arguments="--turbo --after <pid>" }`. The new instance waits up to 10 s for the old pid to exit before it claims the single-instance mutex.
- If the user cancels the UAC prompt (Win32Exception 1223), the app shows a toast and nothing else changes.

## 8. App (WPF)

### 8.1 Composition
`App.OnStartup` does the following:
1. Takes the single-instance mutex.
2. Loads settings and creates the services: `IndexManager`, `SearchService`, `ShellService`, `HotkeyService`, `TrayService` and `StartupService`.
3. Creates the ViewModels and shows `MainWindow`, unless started with `--minimized`.
4. Starts `IndexManager.StartAsync`.

This is manual composition with no DI container, which is enough for about 10 services.

### 8.2 SearchService
- Wraps `SearchEngine`. `Query(text)` debounces (40 ms), cancels any search in flight, and runs on the thread pool.
- Results are marshalled to the UI dispatcher as a `SearchResult` plus a `ResultItem[]`, built for at most 5,000 hits: path built, highlights computed, size and date formatted.
- On `IndexManager.IndexChanged`, the current query is re-run, throttled to at most once per second.

### 8.3 ShellService
| Action | Implementation |
|---|---|
| Open | Not elevated: `Process.Start(new ProcessStartInfo(path){UseShellExecute=true})`. Elevated: `Process.Start("explorer.exe", $"\"{path}\"")`, which hands off to the unelevated shell (FR-18). |
| Open containing folder | `SHParseDisplayName` followed by `SHOpenFolderAndSelectItems`. Falls back to `explorer.exe /select,"path"`. |
| Copy path / name | `Clipboard.SetText` |
| Copy file | `Clipboard.SetFileDropList` |
| Properties | `ShellExecuteEx` with `lpVerb="properties"` and `SEE_MASK_INVOKEIDLIST` |
| Drag out | `DragDrop.DoDragDrop(new DataObject(DataFormats.FileDrop, new[]{path}), Copy\|Link)`. Disabled when elevated. |

**ShellIconProvider** gets icons with `SHGetFileInfo(ext, FILE_ATTRIBUTE_NORMAL, SHGFI_ICON|SHGFI_SMALLICON|SHGFI_USEFILEATTRIBUTES)`, cached per extension. Folders use a cached folder icon. `.exe`, `.lnk`, `.ico` and `.url` files use a per-path lookup with a 512-entry LRU cache, loaded on a background STA thread. Each `HICON` is converted with `Imaging.CreateBitmapSourceFromHIcon`, frozen, and released with `DestroyIcon`.

### 8.4 HotkeyService
- `RegisterHotKey(hwnd, 0xB001, MOD_CONTROL|MOD_ALT|MOD_NOREPEAT, VK_SPACE)` via `HwndSource.AddHook`, listening for `WM_HOTKEY = 0x0312`. The hotkey string is parsed from settings, for example `"Ctrl+Alt+Space"`.
- If registration fails (the combination is already taken), the settings page shows "Hotkey in use".

### 8.5 Window chrome
- `WindowChrome` is set to `CaptionHeight=48`, `ResizeBorderThickness=6`, `GlassFrameThickness=0`, and `UseAeroCaptionButtons=false`.
- `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND)` gives native Windows 11 rounded corners.
- The caption buttons are custom (minimize, maximize, close). **No** `AllowsTransparency`, because that would force a layered window and hurt performance.

### 8.6 Settings (`settings.json`)
```json
{ "hotkey": "Ctrl+Alt+Space", "fuzzy": true, "includeHidden": true, "startWithWindows": false,
  "startMinimized": false, "excludedPaths": [], "recentSearches": [], "closeToTray": true,
  "trayHintShown": false }
```
- Files are written atomically: to a temp file, then moved into place.
- If the JSON is malformed, the defaults are used and the bad file is renamed to `settings.bad.json`.

### 8.7 Logging
`Log.Info/Warn/Error` writes to `%LOCALAPPDATA%\FileHound\logs\filehound-{yyyyMMdd}.log` through a background `Channel<string>` writer. Files older than 7 days are deleted. `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException` and `AppDomain.UnhandledException` are all logged; a dispatcher exception also shows a friendly dialog and is marked handled.

## 9. Testing strategy

| Layer | Tests |
|---|---|
| Core / Query | Tokenizer (quotes, `|`, `!`), every filter grammar plus error cases, `dm:` against a fixed `IClock`, size units and buckets |
| Core / Matching | Wildcard edge cases. Each tier classification. fzf scores, both ordering properties and known values. Myers distance against a reference DP over random strings (property test using a fixed seed). |
| Core / Index | Add, rename (move), delete (subtree), path building, `FindByPath`, the record map, growth past the initial capacity |
| Core / Search | Ranking order for crafted fixtures, multi-term AND/OR/NOT, filter-only queries, the typo pass trigger, sort modes, MaxResults with TotalCount, cancellation |
| Core / Persistence | Round trip (including compaction and fold/category rebuild). Corruption detection: bad magic, a flipped byte, truncation. |
| Core / Perf | 2M synthetic entries, 12 mixed queries, median ≤ 150 ms (`[Trait("Category","Perf")]`) |
| Indexing | `DirectoryWalker` on a temp tree (counts, hidden, excludes, reparse skip). `WatcherUpdater` for create, rename and delete within 2 s. `MftScanner` on C: is skipped unless elevated. `UsnRecordParser` against hand-built byte buffers for V2 and V3. `SnapshotSerializer` on a real walked tree. |
| App | Launch smoke test plus a screenshot review of each page. ViewModels hold no logic that isn't covered by the Core tests. |

## 10. Roadmap (post-v1, explicitly out of scope)
- A `FileHound.Indexer` Windows service (LocalSystem) with a named pipe whose ACL is restricted. The UI would never need elevation, and drag-out would always work.
- A raw $MFT reader to get sizes and dates in a single pass.
- Dark theme, content search, network shares.
