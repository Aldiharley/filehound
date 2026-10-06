# FileHound 🐾

**Fast, friendly file finding for every drive on Windows 11.**
FileHound indexes every file and folder on all your local drives (C:, D:, E:, USB disks…) and finds them as you type, even with missing letters or typos, in a soft pastel "clay" interface.

![Dashboard](docs/screenshots/dashboard.png)

| Search (fuzzy) | Drives |
|---|---|
| ![Search](docs/screenshots/search.png) | ![Drives](docs/screenshots/drives.png) |

## Highlights

- **Whole-disk coverage.** Every ready fixed and removable drive is indexed, whatever its filesystem (NTFS, exFAT, FAT32, ReFS).
- **Instant search.** A parallel, allocation-free engine searches 4 million entries in about 10–80 ms.
- **Fuzzy ("blur") matching.** Results are ranked by match quality, best first:
  - exact name
  - prefix
  - start of a word (`final_report`, `FinalReport`)
  - anywhere in the name
  - letters in order: `qrtrly` → *quarterly_report.xlsx*
  - small typos: `quartelry`, `budjet`
- **Always fresh.** Live updates come from the NTFS USN journal (Turbo mode) or FileSystemWatcher (Standard mode).
- **Fast restarts.** Each drive's index is saved as a compact binary snapshot, and 4 million entries load in about 1 second.
- **Everything-style query syntax** with category chips, sorting and match highlighting.
- **Built for the keyboard.** Global hotkey, tray icon, Enter to open, Ctrl+Enter to open the containing folder, Ctrl+Shift+C to copy the path, and drag a result out to Explorer.

## Indexing modes

| | Standard (default) | Turbo |
|---|---|---|
| How | Parallel folder walk (`FileSystemEnumerable`) | Reads the NTFS master file table (`FSCTL_ENUM_USN_DATA`), the technique Everything uses |
| Live updates | FileSystemWatcher | USN change journal |
| Speed (C:, 4–5M entries) | 24 s warm / 161 s cold | MFT scan 9 s warm / 78 s cold, sizes filled in ~19 s |
| Restart | snapshot in ~1 s, then a background refresh walk | snapshot in ~1.5 s, journal catch-up, ready in ~2.3 s |
| Needs | nothing | administrator approval, given once per launch via **Enable Turbo** |

When FileHound runs elevated it opens files through the normal desktop shell, so they never inherit admin rights. Drag-out is disabled while elevated, because Windows blocks dragging from an elevated app into a normal one.

## Query syntax

| Query | Meaning |
|---|---|
| `budget 2025` | both words (AND) |
| `invoice \| receipt` | either word (OR) |
| `report !draft` | exclude names containing *draft* |
| `"my notes"` | exact phrase, including the space |
| `*.log`, `img_????.jpg` | wildcards (must match the whole name) |
| `ext:pdf;docx` | by extension |
| `type:image`, `kind:video` | by category: `doc`, `image`, `video`, `audio`, `archive`, `app`, `code`, `folder` |
| `folder:projects`, `file:readme` | folders only / files only |
| `size:>1gb`, `size:10mb..2gb`, `size:huge` | by size; the buckets are `empty`, `tiny`, `small`, `medium`, `large`, `huge` and `gigantic` |
| `dm:today`, `dm:week`, `dm:2026-05`, `dm:>2026-01-01` | by date modified |
| `path:work`, `src\core` | matches anywhere in the folder path |
| `drive:E` | one drive only |

## Keyboard

| Keys | Action |
|---|---|
| **Ctrl+Alt+Space** | Show/hide FileHound from anywhere. If another app owns it, FileHound falls back to Ctrl+Shift+Space; you can change it in Settings. |
| ↓ / ↑ | Move between the search box and results |
| Enter / double-click | Open |
| Ctrl+Enter | Open the containing folder with the item selected |
| Ctrl+Shift+C / Ctrl+C | Copy the path / copy the file |
| Alt+Enter | Properties |
| Ctrl+1…9 | Pick a category chip |
| Esc | Clear the search, or hide the window if the search is already empty |

## Build & run

Requirements: Windows 10/11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build FileHound.sln -c Release
```

```bash
dotnet run --project src/FileHound.App -c Release
```

```bash
dotnet test FileHound.sln -c Release
```

To publish a self-contained single-file executable:

```bash
dotnet publish src/FileHound.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Data lives in `%LOCALAPPDATA%\FileHound`: `settings.json`, the `index\*.fhx` snapshots, and `logs\`.

### Headless tools

```bash
dotnet run --project tools/FileHound.Cli -c Release -- scan C D --fresh
```

```bash
dotnet run --project tools/FileHound.Cli -c Release -- search "qrtrly report" --top 10
```

```bash
dotnet run --project tools/FileHound.Cli -c Release -- bench "report" "ext:pdf invoice" "size:>1gb"
```

The app also has a QA mode that renders every page to PNG: `FileHound.exe --snapshot <dir> [--query text] [--drives C] [--data dir]`.

## Architecture

```
FileHound.Core       pure .NET: struct-of-arrays VolumeIndex, query parser, matchers (fzf-style + Myers), SearchEngine, snapshots
FileHound.Indexing   Win32: drive discovery, MFT scanner, USN updater, parallel walker, FileSystemWatcher, IndexManager
FileHound.App        WPF + CommunityToolkit.Mvvm: clay theme, Dashboard / Search / Drives / Settings, tray, hotkey
tools/FileHound.Cli  headless scan / search / bench
```

Design docs live in [`docs/superpowers/specs`](docs/superpowers/specs), the implementation plan in [`docs/superpowers/plans`](docs/superpowers/plans), and background research in [`docs/research`](docs/research).

## License

Apache-2.0. See [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) for adapted work (fzf scoring, MIT) and packages.
