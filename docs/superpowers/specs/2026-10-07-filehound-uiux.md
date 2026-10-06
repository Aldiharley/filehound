# FileHound — UI/UX Spec

**Date:** 2026-10-07 · **Style source:** the user's reference images, a pastel clay music dashboard and a set of pastel 3D clay icons.

## 1. Design principles

1. **Soft, friendly and calm.** Pastel surfaces, generous rounding and soft shadows make it feel like a cosy app rather than a dense power tool, but the power is one keystroke away.
2. **The search box is the hero.** Every page can reach search, and the hotkey lands the caret in it.
3. **Results are clean.** The rows are flat and quiet; the clay depth lives in the containers around them. This keeps 60 fps scrolling (no per-row effects).
4. **Status at a glance.** Drive and indexing state is always visible as a compact chip in the header.
5. **Delight in moderation.** The hound mascot appears in the sidebar, the hero, and the empty and loading states, and nowhere else.

## 2. Design tokens (`Themes/Colors.xaml`)

| Token | Hex | Use |
|---|---|---|
| `Canvas` | `#FBF4EA` | Window background (cream) |
| `Surface` | `#FFFBF5` | Cards |
| `SurfaceSunken` | `#F5ECDF` | Inputs, list hover, track backgrounds |
| `Sidebar` | `#D5E5DA` | Sidebar panel (sage) |
| `SidebarActive` | `#FFF8EE` | Selected nav item pill |
| `Ink` | `#3F3A36` | Primary text (warm charcoal; 10.9:1 on Surface) |
| `InkSoft` | `#7A716A` | Secondary text (4.7:1 on Surface) |
| `InkFaint` | `#A59C93` | Placeholder and disabled text |
| `Peach` / `PeachDeep` | `#F8CDB8` / `#EE8F6E` | Hero, primary button, Favorites-like accents |
| `Mint` / `MintDeep` | `#CFE6D3` / `#6FAE84` | Folders, success, Turbo |
| `Butter` / `ButterDeep` | `#F9E3A6` / `#E3B248` | Documents, warnings |
| `Sky` / `SkyDeep` | `#CFDDF0` / `#7E9CC9` | Drives, info |
| `Lilac` / `LilacDeep` | `#E3D5F0` / `#9E85C4` | Images, video accents |
| `Rose` / `RoseDeep` | `#F6D0D6` / `#D9798A` | Audio, errors |
| `Shadow` | `#8C6E55` at 14% opacity | Every drop shadow (warm, never grey-black) |
| `Highlight` | `#EE8F6E` text on a `#FCE3D7` run background | Matched characters in results |

**Category color mapping** (chips, donut, icons):
- Folder → Mint
- Document → Butter
- Image → Lilac
- Video → Sky
- Audio → Rose
- Archive → Peach
- App → SkyDeep tint
- Code → Mint tint
- Other → `#E8DFD3`

**Radii:**
- window content 24
- cards 22
- inner tiles 18
- inputs and pills 999 (fully round, via a CornerRadius equal to half the height)
- chips 14
- rows 12

**Shadows (clay):**
- Card: `DropShadowEffect { BlurRadius=28, ShadowDepth=6, Direction=270, Opacity=.14, Color=Shadow }`, plus a 1 px inner top highlight (a `#FFFFFF` border at 60% opacity on the top edge, drawn as a gradient border) to get the puffy clay look.
- Pressed or inset: `SurfaceSunken` background with an inner shade drawn as a top-to-bottom `LinearGradientBrush` (`#000000` at 6% to transparent) over the first 6 px.

**Spacing:** a 4 px base unit. Card padding 20, gaps between cards 16, page margin 24.

## 3. Typography (`Themes/Typography.xaml`)

The family is **Segoe UI Variable** (ships with Windows 11), falling back to Segoe UI.

| Style | Size / weight | Use |
|---|---|---|
| `Display` | 30 / SemiBold | Page title ("Dashboard") |
| `HeroTitle` | 26 / Bold | Greeting |
| `Title` | 17 / SemiBold | Card titles |
| `StatValue` | 30 / Bold | Stat numbers |
| `Body` | 14 / Regular | Default |
| `BodyStrong` | 14 / SemiBold | Result names |
| `Caption` | 12 / Regular, InkSoft | Paths, captions |

Numbers use tabular figures (`Typography.NumeralAlignment="Tabular"`).

## 4. Window layout

```
┌────────────────────────────────────────────────────────────────────────────┐
│ [Sidebar 232px, sage, rounded 28, margin 16]  │  Header: Page title · search pill · status chip · ─ ▢ ✕ │
│   (avatar hound 120px circle)                 │─────────────────────────────────────────────────────────│
│   Hi, {UserName}! 👋                          │                                                          │
│   ● Dashboard   (active = cream pill + shadow)│                Page content (scrolls)                   │
│   ○ Search                                    │                                                          │
│   ○ Drives                                    │                                                          │
│   ○ Settings                                  │                                                          │
│                                               │                                                          │
│   ┌ Turbo card (peach) ┐                      │                                                          │
│   │ ⚡ Turbo indexing   │                      │                                                          │
│   │ 10× faster + live  │                      │                                                          │
│   │ [Enable Turbo]     │                      │                                                          │
│   └────────────────────┘ (if elevated: mint "Turbo is on ✓")                                             │
└────────────────────────────────────────────────────────────────────────────┘
```
- The default window is 1280×840 and the minimum is 1040×700.
- The header doubles as the drag area for the custom title bar.
- The nav icons are small clay icons (24 px PNGs), with tinted vector glyphs as a fallback.

### Header
- **Page title** on the left.
- **Global search pill:** 360 px wide, Surface background, shadow, magnifier icon and the placeholder "Search files and folders…". Typing in it switches to the Search page with the text carried over. It is hidden on the Search page, which has its own large pill.
- **Status chip:**
  - `🐾 Indexing… 42%` (butter) while scanning;
  - `● 2,431,889 items` (mint) when ready;
  - `⚠ 1 drive offline` (rose) when a drive is missing.

  Clicking it opens Drives.
- **Caption buttons:** 36 px round ghost buttons, which turn rose on hover for close.

## 5. Pages

### 5.1 Dashboard (mirrors reference image 1)
1. **Hero card** (peach gradient `#F9D3C0 → #F6C2AE`, radius 26, height 200).
   - **Left:** the hound hero illustration, about 190 px, overflowing the top edge by 12 px to give depth.
   - **Middle:** the "Good morning/afternoon/evening! ☀" greeting, then the line "Let's sniff out your files today.", then a large search pill with a "Search" button styled as a PeachDeep pill with white text.
   - **Right:** a small clay plant/magnifier prop, or nothing if no asset exists.
2. **Stat cards row (4 cards).**
   - **Content:** each card has a pastel background and a 48 px clay icon tile (white rounded square holding the icon). Below that are a Title, a StatValue and a Caption.
   - **Cards:**
     - Files (mint): "1,948,220", caption "+312 today"
     - Folders (peach): count, caption "across N drives"
     - Drives (butter): count, caption "N NTFS · N other"
     - Index (sky): "Turbo" or "Standard", caption "updated 2 min ago"
3. **Row: Drives overview (left, 1.4fr) and File types (right, 1fr).**
   - **Drives overview:** a list of drives. Each row has a drive clay icon, a letter and label, a capacity bar (rounded track, fill colored by usage: mint < 70%, butter < 90%, rose ≥ 90%), "312 GB free of 1 TB", the entry count, and a mode badge (Turbo mint or Standard sky). While scanning, the row instead shows a progress bar with %.
   - **File types:** a donut chart (thickness 34, a 2 px gap between slices) of entry counts by category, with a legend of colored dots, labels and percentages, as in the reference.
4. **Row: Recent searches (left) and Largest files (right).**
   - **Recent searches:** up to 5 rows with a clock icon, the query text and a "↗" round button that runs it. The header has a "Clear" link.
   - **Largest files:** up to 5 rows with a shell icon, name, path caption, size and a round "Open" play-style button (as in the reference "Recently Played" list). Until metadata is complete, the card shows the hound sniffing with "Measuring files…".
5. **Tip banner** (mint, radius 22, height 72): a small star or paw clay icon, the text "Press **Ctrl+Alt+Space** anywhere to summon FileHound ✨", and a "Try it" pill button that focuses the search.

### 5.2 Search
- **Large search pill:**
  - full width, 56 px tall, radius 28, Surface background, shadow;
  - a clay magnifier icon at the left;
  - a clear (×) button and a "Fuzzy" toggle chip at the right.
- **Filter chips row:**
  - The chips are All, Folders, Documents, Images, Video, Audio, Archives, Apps and Code. Each one is a pill with a category-colored dot and its label.
  - Selected chips get a filled pastel background, a deeper text color and a soft shadow.
  - One chip is active at a time and it maps to `SearchRequest.Category`. The keyboard can reach them: Tab or Ctrl+1…9.
- **Toolbar line:**
  - **Left:** "12,408 results · 23 ms". When the result count was capped, it says "showing top 5,000". If the typo pass ran, a "🐾 fuzzy" badge appears.
  - **Right:** a sort dropdown (Relevance, Name, Size, Modified) styled as a pill.
- **Results card:**
  - It is a Surface card filling the rest of the height, with its own internal header row: Name, Folder, Size and Modified, clickable to sort. Its body is a virtualized `ListBox`.
  - **Row:** 52 px tall, 12 px radius.
  - **Row content:**
    - a 24 px shell icon;
    - the name as BodyStrong with highlights;
    - the folder path below the name as Caption, trimmed with an ellipsis at the end (`TextTrimming=CharacterEllipsis`);
    - Size, right-aligned with tabular numerals;
    - Modified, as relative text ("2 h ago") for anything under 7 days and as a date otherwise.
  - **Row states:**
    - Hover: SurfaceSunken.
    - Selected: a Peach 45% fill with a 3 px PeachDeep left accent bar.
- **Context menu:** a Clay-styled `ContextMenu` with a rounded 16 radius and items that have icons.
- **Empty states:**
  - No query: the hound sitting with the text "What are we sniffing for?" and example query chips (`ext:pdf invoice`, `size:>1gb`, `folder:projects`, `dm:week`). Clicking a chip inserts it.
  - No results: the hound shrugging with "No luck — try fewer letters or turn on Fuzzy". A "Search with fuzzy" button appears if fuzzy is off.
  - Indexing still running: a slim butter bar at the top of the results card reading "Still indexing E: (62%) — results may be incomplete".
- **Selection details footer** (36 px): the full path of the selected item, plus round icon buttons for Open, Open folder, Copy path and Properties.

### 5.3 Drives
- A grid of drive cards (2 columns, 1 on narrow windows). Each card has:
  - a clay drive icon, a "C:" Display title, the label, and a format badge (NTFS, exFAT, …);
  - a capacity bar;
  - stats: Entries, Folders, Skipped, and Last indexed;
  - the mode badge and status (Ready, Scanning with progress, Filling details, Offline, or Error with its message);
  - a **Rescan** button (round pill).
- A top banner explains Turbo vs Standard and holds the Enable Turbo button. It is hidden if the app is already elevated.

### 5.4 Settings
Grouped Surface cards with labelled rows. Toggles are custom pill switches: the track is SurfaceSunken when off and Mint when on, and the knob is white with a shadow.
- **General:**
  - "Start with Windows" (toggle)
  - "Start minimized to tray" (toggle)
  - "Close button hides to tray" (toggle)
- **Search:**
  - "Fuzzy matching" (toggle)
  - "Show hidden & system files" (toggle)
  - "Clear recent searches" (button)
- **Hotkey:** a capture box. Click it, press a combination, and it shows "Ctrl + Alt + Space". It shows a status line: ✓ registered, or ⚠ "in use by another app".
- **Excluded folders:** a list with remove (×) buttons, plus an "Add folder…" button that opens `OpenFolderDialog` (.NET 8+ WPF). Changes take effect on the next rescan, and a hint says so.
- **About:** version, "Open logs folder", and "Open index folder".

## 6. Interaction details

| Interaction | Behavior |
|---|---|
| Hotkey | Restore and activate the window, switch to Search, select all text in the search box. Pressing it again while focused hides to tray. |
| Typing | Results update after 40 ms. The previous results stay visible until new ones arrive, so the list doesn't flicker. |
| ↓ from the search box | Moves focus to the first result. ↑ on the first result returns to the box. |
| Enter | In the box, opens the top result. In the list, opens the selected one. |
| Ctrl+Enter | Opens the containing folder. |
| Ctrl+Shift+C | Copies the path. A toast says "Path copied". |
| Esc | Clears the query, or hides the window if the query is already empty. |
| Toasts | A small Surface pill slides up from the bottom center for 2 s (for example "Path copied ✓"). |
| Motion | 150 ms ease-out fades between pages, 120 ms hover color transitions, and a progress shimmer. Nothing loops except the indexing paw. |

## 7. Assets (generated with the Higgsfield CLI, then post-processed)

| Asset | Size(s) | Prompt essence |
|---|---|---|
| `hound-avatar.png` | 512² → displayed at 120 | A cute 3D clay beagle puppy head and shoulders with floppy ears and a tiny magnifying glass, pastel cream background circle, soft studio light |
| `hound-hero.png` | 1024² → 200 | Full body clay puppy holding a big magnifying glass, sniffing happily, pastel peach background |
| `hound-sleep.png` | 768² → 220 | Clay puppy curled asleep on a cushion (empty state) |
| `hound-sniff.png` | 768² → 220 | Clay puppy nose down following paw prints (loading or no results) |
| Category icons | 256² → 48/24 | Clay icons in the reference style for folder, document, image, video, audio, archive, app, code, drive, star/paw, clock, magnifier |
| `app.ico` | 256/64/48/32/16 | The hound head on a peach rounded square |

- **Backgrounds:** all images are generated on a flat pastel background. They sit inside rounded containers of the same tint, so they need no transparency.
- **Icons:** if background removal is available (a Higgsfield model or workflow), the icons use transparent PNGs. Otherwise each icon sits on a white rounded tile (the "icon tile"), which matches the reference.
- **Fallback:** if generation fails, a vector glyph fallback (Segoe Fluent Icons) keeps the UI complete.

## 8. Accessibility
- Every icon-only button has an `AutomationProperties.Name`.
- The focus visual is a 2 px PeachDeep rounded outline with a 2 px offset, applied to all focusable Clay controls.
- Text contrast is at least 4.5:1, as checked in the table in §2. Color is never the only signal: badges carry text as well.
- The window works at 125%, 150% and 200% DPI. All assets are at least 2× their display size.
