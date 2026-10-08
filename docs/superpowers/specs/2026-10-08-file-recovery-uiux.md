# FileHound — Recovery Page UI/UX Spec

**Date:** 2026-10-08 · **Extends:** `2026-10-07-filehound-uiux.md` (all tokens, typography, radii, shadows, chip/pill/card styles and the clay mascot rules apply unchanged) · **Behaviour:** `2026-10-08-file-recovery-design.md` (FR-x)

## 1. Principles for this page

1. **Same clay, calmer mood.** Recovery is used in a worried moment. Keep the pastel palette but lean on Sky and Mint; use Rose only for genuine "cannot recover" states and the one destination refusal.
2. **Truth before hope.** Every candidate carries a grade chip. Wording never promises more than the grade: "Excellent — all data still on disk", "Zeroed — the SSD discarded this data".
3. **One flow.** Pick a drive → pick a source tab → pick items → pick a destination → Recover. The destination and the Recover button live in one persistent footer, so the flow reads the same on every tab.
4. **Progress you can leave.** Long scans show progress in the header status chip too, so the user can go back to Search and return.
5. **Mascot moments only where they help:** a digging hound for the empty state, a sniffing hound while scanning, a happy hound on a successful recovery. Nothing animated loops except the existing paw.

## 2. New tokens and assets

- **Grade colors** (chip background / text):
  - Excellent → `Mint` / `#2F5A3E`
  - Good → `Mint` / `#2F5A3E` with a "·" dot
  - Partial → `Butter` / `#7A5A14`
  - Overwritten → `Rose` / `#8A3A47`
  - Zeroed → `Lilac` / `#5A4478`
  - Encrypted → `SurfaceSunken` / `InkSoft`
  - Unknown → `SurfaceSunken` / `InkSoft`
- **Assets** (Higgsfield, same prompts style as §7 of the base spec; processed by `tools/assets/process.ps1`):
  - `hound-dig.png` — the hound digging with a tiny shovel, dirt puffs, happy. Empty state and the Recovery hero.
  - `hound-found.png` — the hound holding up a rescued envelope-shaped file, tail up. Success state.
  - `icon-recycle.png` — pastel mint recycle bin with a cream lid.
  - `icon-timeline.png` — butter clock with a small rewind arrow.
  - `icon-undelete.png` — lilac document with a mint "back" arrow.
  - `icon-shadow.png` — sky folder with a faint ghost copy behind it.
  - `icon-scan.png` — peach magnifying glass over a dotted grid.
  - `icon-shield.png` — mint shield with a cream check (read-only guarantee).
  - `icon-export.png` — lilac tray with an up arrow.

## 3. Navigation

- Sidebar gains **Recovery** between *Drives* and *Settings*, icon `icon-undelete.png`, same `NavItem` style. `AppPage.Recovery` is added; the header title reads "Recovery".
- The header status chip shows recovery progress while a scan runs: `🐾 Scanning free space… 42%` (butter). Clicking it returns to the Recovery page.
- The tray menu gets no new item (YAGNI).

## 4. Page layout

```
┌ Recovery ─────────────────────────────────────────────────────────────────────┐
│ ┌ Drive & source strip (ClayCard, Sky tint) ──────────────────────────────┐   │
│ │ [icon-drive] Drive: ( C: ▾ )   read path: Physical disk ✓   ◉ Read-only │   │
│ │ [Recycle Bin 14] [Recently deleted 312] [Previous versions 1] [Undelete] [Deep scan] │
│ └─────────────────────────────────────────────────────────────────────────┘   │
│ ┌ Tab content (ClayCard) ────────────────────────────────────────────────┐    │
│ │ toolbar: filter box · noise toggle · sort chips · Export ▾              │    │
│ │ results list (virtualized, ResultItem style rows + Grade chip column)   │    │
│ │ selection footer: N selected · total size                               │    │
│ └─────────────────────────────────────────────────────────────────────────┘   │
│ ┌ Recovery footer (ClayCard, Mint tint; sticky) ──────────────────────────┐   │
│ │ Save to: [ E:\Recovered ▾ ]  (different drive required)   [ Recover 3 ] │   │
│ └─────────────────────────────────────────────────────────────────────────┘   │
└───────────────────────────────────────────────────────────────────────────────┘
```

- **Drive & source strip.** Drive dropdown (pill, `ClayTextBox` look) listing ready NTFS drives with letter, label and entry count. Beside it a small caption "read path: Volume / Physical disk / Shadow copy", filled once the session opens, and a `icon-shield` chip **Read-only** with the tooltip "FileHound only reads this drive during recovery. Its own index and logs are paused for it." The five tabs are `Chip` radio buttons; each shows a count once known.
- **Tabs that need admin** (Recently deleted, Previous versions, Undelete, Deep scan) show a lock glyph on the chip when the app is not elevated. Selecting one shows the Turbo card copy inline: "Needs administrator access — Enable Turbo" with the existing `ClayButton`.
- **Recovery footer.** Destination pill opens `OpenFolderDialog`; if the chosen folder is on the source volume the pill turns Rose with "This is the drive you're recovering from — pick another drive" and the Recover button stays disabled. Recover reads "Recover N" / "Restore N" (Recycle Bin and Previous versions in-place). While recovering, the button becomes a `ClayProgress` bar with "3 of 12 · 48 MB/s".

## 5. Tabs

### 5.1 Recycle Bin
- Rows: shell icon · original name (BodyStrong) · original folder (Caption) · size · deleted (relative) · account (Caption, only when elevated and not the current user) · grade chip **Excellent**.
- Items with missing `$R` show grade **Unknown** and detail "data file missing"; orphaned `$R` rows show the detail "no metadata — original name unknown".
- Actions: **Restore** (in place, default), **Recover to…** (copy to destination). Context menu: Restore, Recover to…, Open containing bin folder, Copy original path.
- Empty state: `hound-dig.png` 200 px, "Your Recycle Bin is empty here", caption "Try *Recently deleted* for files that skipped the bin."

### 5.2 Recently deleted
- Toolbar: filter box ("Filter by name or folder"), **Hide noise** toggle (on by default, FR-10), sort chips *Newest*, *Name*, *Size*, **Export ▾** (CSV, DFXML).
- Rows: icon · name · folder · size · deleted (relative, tooltip exact) · grade chip from the gap oracle: **Recoverable (slot intact)** (Mint), **In Recycle Bin** (Sky, with a "→ Recycle Bin tab" link), **Record reused** (Rose, detail "try Deep scan"), **Replaced** (SurfaceSunken, hidden unless noise is shown).
- Directory rows expand to show the captured children count and recover the whole set.
- Banner at the top on first open (dismissable, remembered): "FileHound keeps a log of deletions from the drive's change journal. The journal holds roughly the last {hours} of activity."
- Empty state: "No deletions in the journal yet", caption "The log fills as files are deleted while FileHound is running or from the journal's history."

### 5.3 Previous versions
- A path box at the top ("Type or paste a file or folder path on this drive…") with a *Pick from Search* link that jumps to Search in a picker mode and returns the selected path.
- Below: one row per snapshot version found: snapshot date · size · modified · grade **Excellent**. A directory path shows its entries as a nested list.
- Header right: **Freeze this drive now** (`SoftButton`) opens a confirm dialog: "This creates a snapshot of C:. It writes a small amount of data to the drive, which could overwrite deleted files you haven't recovered yet. Continue?" Default button: Cancel.
- Empty states: not elevated → lock copy; elevated and no snapshots → "No snapshots exist for C:", caption "Windows creates them when System Protection is on. FileHound never turns that on for you."

### 5.4 Undelete
- Toolbar: **Scan** button (becomes *Scanning… 67%* with the sniffing hound at 48 px, then *Rescan*), filter box, type chips (reuse the Search category chips), sort chips *Best grade*, *Newest*, *Name*, *Size*, Export ▾.
- Rows: icon · name with the reconstructed folder (`<unknown folder>` in InkFaint italics when unresolved) · size · modified · deleted (from the log when known) · grade chip with percent for Partial.
- Row tooltip explains the grade in one sentence (FR-14 wording).
- A collapsed **Why some files can't come back** card under the toolbar (expands): SSD TRIM, record reuse, overwritten clusters, encryption — four short lines.
- Selection footer adds "Estimated intact: 92%" for the selection.

### 5.5 Deep scan
- Toolbar: **Scan free space** button → progress bar with "12.4 GB of 410 GB · ETA 6 min", **Pause/Resume**, **Stop**; type chips to filter results; sort *Type*, *Size*, *Location*.
- Rows: type icon · type label (e.g. "JPEG image", "Word document") · size · location (LCN as "block 1,204,332") · a 40 px preview thumbnail for images.
- Selecting a row shows a preview pane on the right (ClayCard, 320 px): image, text snippet, media info, or ZIP entry list; with a "Validated" caption when the parser confirmed the structure.
- Consent card before the first scan (one time, FR-30): shield icon, three bullets, **I understand, scan** (`ClayButton`) / Cancel.
- Empty state: "Nothing scanned yet", caption "Deep scan reads the drive's free space for file signatures. It takes a while on big drives."

## 6. Recovery results and success

- After Recover completes, the results list rows gain a status column: **Recovered ✓** (Mint) with the SHA-256 shortened to 12 chars and a copy button, **Degraded** (Butter, "changed since the scan"), or **Failed** (Rose, with the Win32 message).
- A success toast uses `hound-found.png` at 56 px: "12 files recovered to E:\Recovered\FileHound Recovery 2026-10-08" with **Open folder**.
- The manifest and DFXML/CSV files are listed in the recovery folder; the footer shows an **Open recovery folder** link.

## 7. Interaction details

| Interaction | Behaviour |
|---|---|
| Drive change | Closes the session (progress is lost after a confirm if a scan is running); tabs reset; counts reload. |
| Leaving the page | Scans keep running; the header chip shows progress; write suspension stays until the session closes. |
| Enter on a row | Recover/Restore that row with the current destination. |
| Ctrl+A / Shift+click / Ctrl+click | Multi-select, as in Explorer. |
| Esc | Clears the filter; second Esc clears the selection. |
| Drag-out | Disabled on this page (recovery must go through the destination rule). |
| Keyboard | Tab order: drive → tabs → toolbar → list → footer. All icon buttons have `AutomationProperties.Name`. |

## 8. Copy guidelines

- Say "recover" for copying to another drive, "restore" for putting back in place.
- Never say "100% recoverable"; say "Excellent — all data still on disk".
- Grade tooltips use plain cause-and-effect: "Some of this file's space has been reused by other files. About 40% is still intact."
- The SSD caveat, verbatim: "On SSDs, Windows tells the drive to discard deleted data within seconds. If a file shows *Zeroed*, no copy of it exists on this drive."

## 9. Accessibility

- Grade is never color-only: every chip has text.
- Progress bars expose `AutomationProperties.Name` with the percentage.
- The consent and freeze dialogs are keyboard-reachable with Cancel as the default.
- Contrast: grade text colors above were checked against their chip backgrounds (≥ 4.5:1).
