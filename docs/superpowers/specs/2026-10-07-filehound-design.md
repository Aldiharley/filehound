# FileHound — Product Design Spec

**Date:** 2026-10-07
**Status:** Approved (brainstorming), pending written-spec review
**Companion docs:**
- Architecture & C#/.NET technical spec — `2026-10-07-filehound-architecture.md`
- UI/UX spec — `2026-10-07-filehound-uiux.md`
- Research — `docs/research/2026-10-06-file-search-research.md`

## 1. Summary

FileHound is a Windows 11 desktop app (C#, WPF, .NET 10) that finds files and folders on **every local drive** almost instantly, with fuzzy ("blur") matching that tolerates partial words, missing letters and typos. It looks soft and friendly: pastel claymorphism with a 3D clay hound mascot, based on the style references the user supplied.

## 2. Goals

| # | Goal | Measurable target |
|---|------|-------------------|
| G1 | Whole-disk coverage | Indexes every ready fixed and removable drive (C:, D:, E:, …), whatever the filesystem (NTFS, ReFS, exFAT, FAT32). |
| G2 | Fast first index | NTFS with admin rights: about 1M entries indexed in ≤ 5 s. Without admin: parallel folder scan, with progress shown and results searchable while it runs. |
| G3 | Instant search | Latency from keystroke to results ≤ 100 ms for 2M entries on a modern 8-core CPU (search engine only, measured by a perf test with a ≤ 150 ms budget). |
| G4 | Fuzzy search | Finds `quarterly_report_final.xlsx` from `qrtrly rep`, `qreport`, or `quartelry` (a typo). |
| G5 | Always current | File creates, deletes and renames appear in results within about 2 s (USN journal or FileSystemWatcher). |
| G6 | Fast restart | With a snapshot available, the app can search ≤ 2 s after launch. |
| G7 | Elegant UI | Clean pastel clay style that matches the references, with smooth virtualized scrolling through 100k+ results. |

## 3. Non-goals (v1)

- Searching file *contents* (full text).
- Network shares and optical drives. A setting to enable them may come later, but v1 does not index them.
- A background Windows service or indexing before login. This is planned for v2 (see Architecture §10).
- Dark theme. The design tokens make it possible later.
- Cloud sync, multiple users, or plugins.

## 4. Users and key scenarios

1. **"Where did I save that?"** The user presses Ctrl+Alt+Space anywhere, types `budget 2025`, and the right `.xlsx` is the first result. Enter opens it.
2. **"I only remember part of the name."** Typing `invce scan` finds `Invoice_Scan_0412.pdf` on E:.
3. **"Find big videos."** Clicking the Video chip and typing `size:>1gb` gives videos over 1 GB across all drives, sorted by size.
4. **"Find a folder."** `folder:node_modules` lists every node_modules folder.
5. **"Open the containing folder."** Ctrl+Enter opens Explorer with the file selected.
6. **First run, no admin.** The dashboard shows each drive's progress bar filling, and a "Turbo indexing" card explains that one admin relaunch makes indexing about 10× faster and keeps the index live through the USN journal.

## 5. Functional requirements

### 5.1 Indexing
- **FR-1** On startup, discover drives with `DriveInfo` where `IsReady` is true and `DriveType` is `Fixed` or `Removable`.
- **FR-2** For each NTFS drive, when the process is **elevated**, enumerate the MFT with `FSCTL_ENUM_USN_DATA` ("Turbo" mode). Otherwise, or on any failure, use the parallel directory walker ("Standard" mode).
- **FR-3** Index both files and folders: name, parent, attributes (dir, hidden, system), size, and last-write time. In Turbo mode, a background pass after the name index fills in size and time. Until it finishes, size- and date-dependent UI shows "Filling details…".
- **FR-4** Skip by default: `$Recycle.Bin`, `System Volume Information`, and NTFS `$` metafiles. The user can add excluded folders in Settings.
- **FR-5** Live updates:
  - Turbo drives read the USN journal. Changes are applied in batches every ≤ 1 s.
  - Standard drives use one FileSystemWatcher per drive. A watcher overflow triggers a rescan of that drive.
- **FR-6** Save a binary snapshot per drive on exit and every 15 minutes while changes are pending. On startup, load the snapshots first so search works immediately, then refresh:
  - **Turbo:** replay the USN journal from the saved USN if the journal ID matches; otherwise rescan.
  - **Standard:** rescan in the background.
- **FR-7** Drives that are added or removed while running are detected by polling every 5 s. A removed drive's entries are dropped and the drive is shown as offline.

### 5.2 Search
- **FR-8** Search as you type with a 40 ms debounce. Each new query cancels the one in flight.
- **FR-9** Query syntax (case-insensitive):
  - Words separated by spaces are AND.
  - `a | b` is OR.
  - `!word` is NOT.
  - `"exact phrase"` keeps the spaces in the phrase.
  - Wildcards `*` and `?`: a term that contains one must match the whole name.
  - `ext:pdf;docx` — extension in the list.
  - `type:` or `kind:` with `doc|image|video|audio|archive|app|code|folder|file`.
  - `file:` (files only) and `folder:` (folders only), each optionally followed by a term.
  - `size:` with `>N`, `<N`, `>=N`, `<=N`, `A..B`, or `N`. Units are `b, kb, mb, gb, tb`. Named buckets: `empty, tiny, small, medium, large, huge, gigantic`.
  - `dm:` with `today|yesterday|week|month|year|YYYY|YYYY-MM|YYYY-MM-DD`, or a comparison such as `>2026-01-01`.
  - `path:term` — term in the parent folder path or the name.
  - `drive:E` — restrict to one drive.
  - A term that contains `\` matches against the full path.
- **FR-10** Fuzzy matching tiers, ranked in this order:
  1. exact name (or exact stem)
  2. name prefix
  3. word-boundary start (after `_ - . space`, a camelCase hump, or a letter/digit transition)
  4. substring
  5. subsequence (fzf scoring)
  6. typo: bounded edit distance, where k = 1 for terms of 4–6 characters and k = 2 for terms of 7 or more

  Typo matching runs only when the earlier tiers produce fewer than 200 hits. A Settings toggle, "Fuzzy matching", turns tiers 5–6 off.
- **FR-11** Ties are broken by: a match in the name over a match in the path, then the shorter name, then the shallower path, then the more recent modification.
- **FR-12** Sort modes: Relevance (default), Name, Size (largest first), Modified (newest first). The engine keeps the top 5,000 results by the active sort key and reports the total match count.
- **FR-13** Show hidden and system files by default; a Settings toggle hides them.
- **FR-14** Highlight matched characters in the result name.

### 5.3 Actions
- **FR-15** Enter or double-click opens the item. Ctrl+Enter opens the containing folder with the item selected. Ctrl+Shift+C copies the full path. Ctrl+C copies the file itself so it can be pasted in Explorer. Alt+Enter opens Properties.
- **FR-16** The right-click menu has: Open, Open containing folder, Copy path, Copy name, Copy file, Properties.
- **FR-17** Dragging a row out drops the file into Explorer or another app. While the app is elevated, drag-out is disabled, because Windows UIPI blocks dropping from an elevated app into a normal one; a tooltip explains this.
- **FR-18** When the app is elevated, items open through the unelevated shell, so opened files do not inherit admin rights.

### 5.4 App shell
- **FR-19** The default global hotkey **Ctrl+Alt+Space** shows the window and focuses search. The hotkey can be changed in Settings.
- **FR-20** Tray icon: left-click shows the window. The menu has Show, Rescan all, Settings, and Exit. Closing the window hides it to the tray. The first time this happens, a balloon tip explains it.
- **FR-21** Single instance: a second launch activates the existing window.
- **FR-22** "Start with Windows" toggle, written to the HKCU Run key.
- **FR-23** "Enable Turbo indexing" relaunches the app elevated through `runas`, then the current instance exits.
- **FR-24** Settings and recent-search history are stored in `%LOCALAPPDATA%\FileHound\settings.json`.

### 5.5 Dashboard
- **FR-25** The Dashboard shows:
  - a greeting hero with a search box that jumps to the Search page;
  - stat cards: Files, Folders, Drives, Index status;
  - a Drives card with a capacity bar, entry count, mode badge and live progress for each drive;
  - a file-type donut by category;
  - the last 5 recent searches, each clickable;
  - the 5 largest files;
  - a tip banner.

## 6. Quality requirements

- **Performance:** G2, G3 and G6 above. The UI thread is never blocked for more than 50 ms by indexing or search.
- **Memory:** ≤ 160 B per entry on average (about 320 MB for 2M entries), including both name arenas.
- **Reliability:** an unreadable folder is skipped and counted, never fatal. A corrupt snapshot is discarded and rebuilt. Unhandled exceptions are logged and shown in a friendly dialog, and the app keeps running where it can.
- **Security:** `asInvoker` manifest. Elevation happens only after the user asks for it. Nothing leaves the machine; there is no telemetry.
- **Accessibility:** full keyboard operation, visible focus rings, AutomationProperties names on icon buttons, text contrast ≥ 4.5:1 on cards.

## 7. Acceptance criteria (v1 "done")

1. `dotnet build` with 0 warnings treated as errors in Core and Indexing, and `dotnet test` all green.
2. The perf test passes: 2M synthetic entries, a mixed query set, median ≤ 150 ms.
3. Running the app on this machine indexes all ready drives, and searches return correct results with highlighting.
4. Creating, renaming and deleting a file in a temp folder is reflected in search within 2 s (Standard mode, verified by an integration test).
5. Screenshots of Dashboard, Search, Drives and Settings match the UI/UX spec.
6. Pushed to `https://github.com/Aldiharley/filehound` `main`, with a README covering build, run and query syntax.
