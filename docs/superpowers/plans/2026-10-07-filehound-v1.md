# FileHound v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development (recommended) or executing-plans to implement this plan task by task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build FileHound, a fast whole-disk file and folder finder for Windows 11 in C#, WPF and .NET 10. It has fuzzy search, live updates and a pastel clay UI.

**Architecture:**
- **`FileHound.Core`** (pure .NET): a struct-of-arrays `VolumeIndex`, a query parser, tiered matchers (substring → fzf subsequence → Myers typo), a parallel `SearchEngine` and binary snapshots.
- **`FileHound.Indexing`** (Win32): fills indexes through the MFT (`FSCTL_ENUM_USN_DATA`) when elevated, and through a parallel `FileSystemEnumerable` walker otherwise. It keeps them live with the USN journal or FileSystemWatcher.
- **`FileHound.App`** (WPF MVVM): renders the Dashboard, Search, Drives and Settings pages.

**Tech Stack:** .NET 10 SDK 10.0.401, C# 14, WPF, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, System.IO.Hashing, xUnit.

**Specs:**
- `docs/superpowers/specs/2026-10-07-filehound-design.md` (FR-x, G-x)
- `docs/superpowers/specs/2026-10-07-filehound-architecture.md` (§x)
- `docs/superpowers/specs/2026-10-07-filehound-uiux.md` (UI §x)

## Global Constraints

- Target framework is `net10.0` for Core, and `net10.0-windows` for Indexing, App, Cli and the Indexing tests. `global.json` pins SDK `10.0.401` with `rollForward: latestFeature`.
- `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` for Core and Indexing.
- No GPL, LGPL, MPL or unlicensed code. The fzf-derived scoring is credited in `THIRD-PARTY-NOTICES.md`. The repo license is Apache-2.0 (already present).
- App manifest is `asInvoker`. Elevation happens only when the user clicks Enable Turbo.
- Data folder: `%LOCALAPPDATA%\FileHound\`, containing `settings.json`, `index\*.fhx` and `logs\`.
- Default hotkey **Ctrl+Alt+Space**. Search debounce 40 ms. MaxResults 5000. Typo pass when the Pass 1 total is under 200.
- Perf budget: 2M entries, median ≤ 150 ms (search engine only).
- UI tokens and colors come exactly from UI spec §2. Font: Segoe UI Variable.
- Commit after every task, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File Structure

```
FileHound.sln, global.json, Directory.Build.props, .gitignore, .gitattributes, README.md, THIRD-PARTY-NOTICES.md
src/FileHound.Core/
  Index/EntryFlags.cs, FileCategory.cs, Categorizer.cs, VolumeIndex.cs, VolumeIndexBuilder.cs, PathBuilder.cs, IndexMode.cs
  Query/Query.cs (AST), QueryParser.cs, SizeParser.cs, DateRangeParser.cs, IClock.cs
  Matching/CharClass.cs, FuzzyScorer.cs, TypoMatcher.cs, WildcardMatcher.cs, TermMatcher.cs, MatchTier.cs
  Search/SearchRequest.cs, SearchResult.cs, SearchHit.cs, BoundedHeap.cs, SearchEngine.cs, Highlighter.cs, RankKey.cs
  Persistence/SnapshotSerializer.cs, SnapshotHeader.cs
  Stats/IndexStatistics.cs
src/FileHound.Indexing/
  Interop/Kernel32.cs, UsnStructs.cs
  Drives/DriveDescriptor.cs, DriveDiscovery.cs
  Ntfs/UsnRecordParser.cs, MftScanner.cs, UsnUpdater.cs
  Walk/DirectoryWalker.cs, WatcherUpdater.cs, MetadataFiller.cs
  IndexManager.cs, DriveState.cs, IndexOptions.cs, Elevation.cs
src/FileHound.App/  (see Tasks 16–20)
tests/FileHound.Core.Tests/  tests/FileHound.Indexing.Tests/
tools/FileHound.Cli/Program.cs
```

---

### Task 1: Solution scaffold

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore`, `.gitattributes`, `FileHound.sln`
- Create: `src/FileHound.Core/FileHound.Core.csproj`, `src/FileHound.Indexing/FileHound.Indexing.csproj`
- Create: `tests/FileHound.Core.Tests/*.csproj`, `tests/FileHound.Indexing.Tests/*.csproj`, `tools/FileHound.Cli/*.csproj`

**Produces:** a buildable, empty solution and `dotnet test` running zero tests.

- [ ] **Step 1:** `global.json`:
```json
{ "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }
```
- [ ] **Step 2:** `Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Version>1.0.0</Version>
    <Company>FileHound</Company>
  </PropertyGroup>
</Project>
```
- [ ] **Step 3:** Create the projects with `dotnet new classlib`, `xunit` and `console`. Set the target frameworks as in Global Constraints. Add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to Core and Indexing, and `InternalsVisibleTo` for the test projects. Add the references: Indexing → Core; the tests → their subject; Cli → Indexing.
- [ ] **Step 4:** `dotnet new sln` and `dotnet sln add` for every project. `.gitignore` uses the `dotnet new gitignore` template. `.gitattributes`: `* text=auto eol=crlf`, with `*.png`, `*.ico` and `*.jpg` marked binary.
- [ ] **Step 5:** Run `dotnet build FileHound.sln -c Debug` and expect 0 errors. Run `dotnet test` and expect success.
- [ ] **Step 6:** Commit with `chore: scaffold FileHound solution (.NET 10)`.

### Task 2: Categories and entry flags

**Files:**
- Create: `src/FileHound.Core/Index/EntryFlags.cs`, `FileCategory.cs`, `Categorizer.cs`, `IndexMode.cs`
- Test: `tests/FileHound.Core.Tests/CategorizerTests.cs`

**Produces:**
```csharp
[Flags] public enum EntryFlags : ushort { None=0, Directory=1, Hidden=2, System=4, ReparsePoint=8, Deleted=16, MetadataKnown=32 }
public enum FileCategory : byte { Other=0, Folder, Document, Image, Video, Audio, Archive, App, Code }
public enum IndexMode : byte { Standard=0, Turbo=1 }
public static class Categorizer {
    public static FileCategory FromName(ReadOnlySpan<char> foldName, bool isDirectory);
    public static ReadOnlySpan<char> Extension(ReadOnlySpan<char> foldName); // after last '.', empty if none or leading dot only
    public static IReadOnlyList<string> ExtensionsOf(FileCategory c);
    public static bool TryParseCategory(string token, out FileCategory c); // doc|document|documents|image|images|pic|video|videos|audio|music|archive|archives|zip|app|apps|exe|code|folder
}
```
The extension lists come from research §6, plus Code: `cs c cpp h hpp py js ts tsx jsx java go rs rb php swift kt sql sh ps1 json xml yaml yml toml html css scss vue`. ps1 is App, not Code; App wins for ps1, bat and cmd.

- [ ] **Step 1: Write the failing tests**
```csharp
public class CategorizerTests
{
    [Theory]
    [InlineData("report.pdf", FileCategory.Document)]
    [InlineData("photo.jpeg", FileCategory.Image)]
    [InlineData("movie.mkv", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("backup.7z", FileCategory.Archive)]
    [InlineData("setup.exe", FileCategory.App)]
    [InlineData("program.cs", FileCategory.Code)]
    [InlineData("readme", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    public void FromName_maps_extension(string name, FileCategory expected)
        => Assert.Equal(expected, Categorizer.FromName(name, false));

    [Fact] public void Directories_are_folders() => Assert.Equal(FileCategory.Folder, Categorizer.FromName("src.cs", true));
    [Fact] public void Extension_of_dotfile_is_empty() => Assert.True(Categorizer.Extension(".gitignore").IsEmpty);
    [Fact] public void Extension_takes_last_dot() => Assert.Equal("gz", Categorizer.Extension("a.tar.gz").ToString());
    [Theory] [InlineData("pic", FileCategory.Image)] [InlineData("Documents", FileCategory.Document)] [InlineData("exe", FileCategory.App)]
    public void TryParseCategory_accepts_aliases(string token, FileCategory expected)
    { Assert.True(Categorizer.TryParseCategory(token, out var c)); Assert.Equal(expected, c); }
}
```
- [ ] **Step 2:** Run `dotnet test tests/FileHound.Core.Tests --filter CategorizerTests`. It should FAIL to compile.
- [ ] **Step 3:** Implement it with a `FrozenDictionary<string,FileCategory>` (built with `StringComparer.Ordinal`) and `GetAlternateLookup<ReadOnlySpan<char>>()`. The input is already lowercase.
- [ ] **Step 4:** Run the tests again and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): file categories and entry flags`.

### Task 3: VolumeIndex, builder and path building

**Files:**
- Create: `src/FileHound.Core/Index/VolumeIndex.cs`, `VolumeIndexBuilder.cs`, `PathBuilder.cs`
- Test: `tests/FileHound.Core.Tests/VolumeIndexTests.cs`

**Consumes:** Task 2 types.

**Produces** (architecture §3):
```csharp
public sealed class VolumeIndex {
    public VolumeIndex(string root, IndexMode mode, int capacity = 1024);   // creates root entry 0 named "C:" (root without trailing '\')
    public string Root { get; } public char DriveLetter { get; } public IndexMode Mode { get; set; }
    public uint VolumeSerial { get; set; } public ulong UsnJournalId { get; set; } public long NextUsn { get; set; }
    public int Count { get; } public int LiveCount { get; }
    public ReaderWriterLockSlim Lock { get; }
    public bool IsDirty { get; set; }
    // read accessors (caller holds read lock or is single-threaded)
    public ReadOnlySpan<char> Name(int e); public ReadOnlySpan<char> FoldName(int e);
    public int Parent(int e); public EntryFlags Flags(int e); public FileCategory Category(int e);
    public byte Depth(int e); public long Size(int e); public long ModifiedTicks(int e);
    public bool IsLive(int e); public bool IsDirectory(int e);
    // mutations (take write lock internally unless suffix 'NoLock')
    public int Add(int parent, ReadOnlySpan<char> name, EntryFlags flags, long size, long modifiedTicks, long recordNo = -1);
    public void Rename(int e, int newParent, ReadOnlySpan<char> newName);
    public void Delete(int e);                       // marks e and all live descendants Deleted
    public void SetMetadata(int e, long size, long modifiedTicks);
    public int FindChild(int parent, ReadOnlySpan<char> name);   // case-insensitive; -1 if none; builds child map lazily
    public int FindByPath(string fullPath);                       // -1 if none
    public int FindByRecord(long recordNo);                       // Turbo; -1 if none
    public void SetRecord(int e, long recordNo);
    internal ... raw array access for builder/serializer
}
public static class PathBuilder {
    public static string GetFullPath(VolumeIndex v, int e);            // "C:\" for root, "C:\a\b.txt"
    public static string GetParentPath(VolumeIndex v, int e);          // folder containing e
    public static bool AncestorFoldContains(VolumeIndex v, int e, ReadOnlySpan<char> fold);
}
public sealed class VolumeIndexBuilder {
    public VolumeIndexBuilder(string root, IndexMode mode, int capacityHint = 1 << 16);
    public void AddRecord(long recordNo, long parentRecordNo, ReadOnlySpan<char> name, EntryFlags flags, long size = 0, long modifiedTicks = 0);
    public VolumeIndex Build(long rootRecordNo = 5); // resolves parents; unresolved => Deleted orphan
}
```
Rules:
- The root can be a drive root (`C:\`) or any absolute directory (used by the tests and fake drives). The root entry's name is the root without its trailing `\`, so `"C:"` or `"D:\tmp\x"`. `GetFullPath(root)` returns `Root` as given, and descendants are `rootName + '\' + …`.
- `Add` computes the fold name (`char.ToLowerInvariant`), the category and depth (parent depth + 1, capped at 255), and appends to both arenas. Arrays grow by doubling.
- `Delete` on a directory marks every live entry whose ancestor chain hits `e`.
- The child map key is `(parent, foldHash)`, where foldHash comes from `string.GetHashCode(ReadOnlySpan<char>)` on the fold name. It is maintained by Add, Rename and Delete once it has been built.

- [ ] **Step 1: Write the failing tests**
```csharp
public class VolumeIndexTests
{
    static VolumeIndex Sample(out int docs, out int report)
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard, 4);
        var users = v.Add(0, "Users", EntryFlags.Directory, 0, 0);
        docs = v.Add(users, "Docs", EntryFlags.Directory, 0, 0);
        report = v.Add(docs, "Report Final.PDF", EntryFlags.MetadataKnown, 2048, 638000000000000000);
        return v;
    }
    [Fact] public void Root_path_is_drive_root() { var v = new VolumeIndex(@"E:\", IndexMode.Standard); Assert.Equal(@"E:\", PathBuilder.GetFullPath(v, 0)); }
    [Fact] public void Full_path_joins_ancestors() { var v = Sample(out _, out var r); Assert.Equal(@"C:\Users\Docs\Report Final.PDF", PathBuilder.GetFullPath(v, r)); }
    [Fact] public void Fold_name_is_lowercase() { var v = Sample(out _, out var r); Assert.Equal("report final.pdf", v.FoldName(r).ToString()); Assert.Equal(FileCategory.Document, v.Category(r)); }
    [Fact] public void Depth_counts_from_root() { var v = Sample(out _, out var r); Assert.Equal(3, v.Depth(r)); }
    [Fact] public void Grows_past_capacity() { var v = new VolumeIndex(@"C:\", IndexMode.Standard, 2); for (int i = 0; i < 5000; i++) v.Add(0, $"f{i}.txt", 0, 0, 0); Assert.Equal(5001, v.LiveCount); Assert.Equal("f4999.txt", v.Name(5000).ToString()); }
    [Fact] public void Rename_moves_and_renames() { var v = Sample(out var d, out var r); v.Rename(r, 0, "moved.pdf"); Assert.Equal(@"C:\moved.pdf", PathBuilder.GetFullPath(v, r)); Assert.Equal(1, v.Depth(r)); }
    [Fact] public void Delete_dir_removes_subtree() { var v = Sample(out var d, out var r); v.Delete(d); Assert.False(v.IsLive(d)); Assert.False(v.IsLive(r)); Assert.Equal(2, v.LiveCount); }
    [Fact] public void FindByPath_is_case_insensitive() { var v = Sample(out _, out var r); Assert.Equal(r, v.FindByPath(@"c:\users\DOCS\report final.pdf")); Assert.Equal(-1, v.FindByPath(@"C:\nope")); }
    [Fact] public void FindChild_tracks_renames() { var v = Sample(out var d, out var r); v.FindChild(d, "x"); v.Rename(r, d, "new.pdf"); Assert.Equal(r, v.FindChild(d, "NEW.pdf")); Assert.Equal(-1, v.FindChild(d, "report final.pdf")); }
    [Fact] public void Builder_resolves_parents_out_of_order()
    {
        var b = new VolumeIndexBuilder(@"C:\", IndexMode.Turbo);
        b.AddRecord(100, 50, "child.txt", 0);
        b.AddRecord(50, 5, "parent", EntryFlags.Directory);
        b.AddRecord(200, 999, "orphan.txt", 0);
        var v = b.Build();
        var c = v.FindByRecord(100);
        Assert.Equal(@"C:\parent\child.txt", PathBuilder.GetFullPath(v, c));
        Assert.False(v.IsLive(v.FindByRecord(200)));
    }
    [Fact] public void AncestorFoldContains_checks_chain() { var v = Sample(out _, out var r); Assert.True(PathBuilder.AncestorFoldContains(v, r, "docs")); Assert.False(PathBuilder.AncestorFoldContains(v, r, "windows")); }
}
```
- [ ] **Step 2:** Run `dotnet test --filter VolumeIndexTests` and expect FAIL.
- [ ] **Step 3:** Implement `VolumeIndex`, `VolumeIndexBuilder` (two-phase: collect the records, map record → entry, assign parents, then compute depth in a loop that resolves parents first, using an explicit stack), and `PathBuilder`, which collects ancestors into a `ValueListBuilder`-style stack and writes into a `stackalloc char[512]`, with a `StringBuilder` fallback.
- [ ] **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): struct-of-arrays VolumeIndex, builder, path building`.

### Task 4: Query parser

**Files:**
- Create: `src/FileHound.Core/Query/Query.cs`, `QueryParser.cs`, `SizeParser.cs`, `DateRangeParser.cs`, `IClock.cs`
- Test: `tests/FileHound.Core.Tests/QueryParserTests.cs`

**Produces** (architecture §4):
```csharp
public interface IClock { DateTimeOffset Now { get; } } public sealed class SystemClock : IClock { ... }
public enum TermKind : byte { Plain, Wildcard, Path }
public sealed class Term { public string Text; public TermKind Kind; public ulong CharMask; public bool TypoEligible; /* + Peq tables */ }
public abstract record Filter;
public sealed record ExtFilter(FrozenSet<string> Extensions) : Filter;
public sealed record CategoryFilter(FileCategory[] Categories) : Filter;
public sealed record KindFilter(bool Folders) : Filter;          // true = folders only, false = files only
public sealed record SizeFilter(long Min, long Max) : Filter;     // inclusive min, inclusive max
public sealed record DateFilter(long MinUtcTicks, long MaxUtcTicks) : Filter; // [min, max)
public sealed record DriveFilter(char Letter) : Filter;
public sealed record PathFilter(Term Term) : Filter;
public sealed class Alternative { public Term? Term; public Filter? Filter; }
public sealed class Clause { public List<Alternative> Alternatives; public bool Negated; public bool IsFilterOnly; public bool HasPath; }
public sealed class Query { public List<Clause> Clauses; public List<string> Errors; public bool IsEmpty; public IReadOnlyList<Term> PositiveTerms; }
public static class QueryParser { public static Query Parse(string text, IClock? clock = null); }
public static class SizeParser { public static bool TryParse(string s, out long min, out long max); }
public static class DateRangeParser { public static bool TryParse(string s, IClock clock, out long minUtc, out long maxUtc); }
```

- [ ] **Step 1: Write the failing tests**
```csharp
public class QueryParserTests
{
    sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now => now; }
    static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 15, 0, 0, TimeSpan.Zero));

    [Fact] public void Empty_query() => Assert.True(QueryParser.Parse("   ").IsEmpty);
    [Fact] public void Words_are_and_clauses() { var q = QueryParser.Parse("Budget 2025"); Assert.Equal(2, q.Clauses.Count); Assert.Equal("budget", q.Clauses[0].Alternatives[0].Term!.Text); }
    [Fact] public void Quotes_keep_spaces() => Assert.Equal("my file", QueryParser.Parse("\"My File\"").Clauses[0].Alternatives[0].Term!.Text);
    [Fact] public void Pipe_makes_alternatives() { var q = QueryParser.Parse("a b|c"); Assert.Equal(2, q.Clauses.Count); Assert.Equal(2, q.Clauses[1].Alternatives.Count); }
    [Fact] public void Spaced_pipe_makes_alternatives() { var q = QueryParser.Parse("b | c"); Assert.Single(q.Clauses); Assert.Equal(2, q.Clauses[0].Alternatives.Count); }
    [Fact] public void Bang_negates() => Assert.True(QueryParser.Parse("!tmp").Clauses[0].Negated);
    [Fact] public void Wildcard_term() => Assert.Equal(TermKind.Wildcard, QueryParser.Parse("*.log").Clauses[0].Alternatives[0].Term!.Kind);
    [Fact] public void Backslash_term_is_path() => Assert.Equal(TermKind.Path, QueryParser.Parse(@"src\core").Clauses[0].Alternatives[0].Term!.Kind);
    [Fact] public void Ext_filter_list() { var f = (ExtFilter)QueryParser.Parse("ext:PDF;docx").Clauses[0].Alternatives[0].Filter!; Assert.Contains("pdf", f.Extensions); Assert.Contains("docx", f.Extensions); }
    [Fact] public void Folder_with_term_adds_two_clauses() { var q = QueryParser.Parse("folder:node_modules"); Assert.Equal(2, q.Clauses.Count); Assert.IsType<KindFilter>(q.Clauses[0].Alternatives[0].Filter); }
    [Fact] public void Type_filter() => Assert.Equal(FileCategory.Image, ((CategoryFilter)QueryParser.Parse("type:pic").Clauses[0].Alternatives[0].Filter!).Categories[0]);
    [Theory]
    [InlineData(">10mb", 10L * 1024 * 1024 + 1, long.MaxValue)]
    [InlineData(">=1kb", 1024, long.MaxValue)]
    [InlineData("<2kb", 0, 2047)]
    [InlineData("1mb..1gb", 1048576, 1073741824)]
    [InlineData("empty", 0, 0)]
    [InlineData("gigantic", 134217728, long.MaxValue)]
    [InlineData("500", 500, 500)]
    public void Size_grammar(string s, long min, long max) { Assert.True(SizeParser.TryParse(s, out var a, out var b)); Assert.Equal(min, a); Assert.Equal(max, b); }
    [Fact] public void Bad_size_reports_error() { var q = QueryParser.Parse("size:abc report"); Assert.Single(q.Errors); Assert.Single(q.Clauses); }
    [Fact] public void Dm_today_range() { Assert.True(DateRangeParser.TryParse("today", Clock, out var min, out var max)); Assert.True(max - min == TimeSpan.TicksPerDay); }
    [Fact] public void Dm_year() { Assert.True(DateRangeParser.TryParse("2025", Clock, out var min, out var max)); Assert.Equal(new DateTime(2025,1,1,0,0,0,DateTimeKind.Local).ToUniversalTime().Ticks, min); }
    [Fact] public void Dm_greater_than_date() { Assert.True(DateRangeParser.TryParse(">2026-01-01", Clock, out var min, out var max)); Assert.Equal(long.MaxValue, max); }
    [Fact] public void Drive_filter() => Assert.Equal('E', ((DriveFilter)QueryParser.Parse("drive:e").Clauses[0].Alternatives[0].Filter!).Letter);
    [Fact] public void Filter_only_clause_flag() => Assert.True(QueryParser.Parse("ext:pdf").Clauses[0].IsFilterOnly);
    [Fact] public void Typo_eligibility() { var q = QueryParser.Parse("abc quartelry"); Assert.False(q.PositiveTerms[0].TypoEligible); Assert.True(q.PositiveTerms[1].TypoEligible); }
}
```
- [ ] **Step 2:** Run the tests and expect FAIL.
- [ ] **Step 3:** Implement the tokenizer. It is a char-by-char scan that tracks quotes and produces tokens with `negated` and `alternatives` (split on unquoted `|`; a standalone `|` token merges the previous and next tokens into one clause). For each alternative, `prefix:value` is recognized for `ext, type, kind, file, folder, size, dm, path, drive`. Anything else is a Term. The Term constructor precomputes the fold text, `CharMask`, `TypoEligible` (4 to 64 characters, Plain kind) and the Myers Peq tables (Task 5 consumes them through `TypoMatcher.Prepare`).
- [ ] **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): Everything-style query parser with filters`.

### Task 5: Matchers (wildcard, fzf, Myers, tiers)

**Files:**
- Create: `src/FileHound.Core/Matching/MatchTier.cs`, `CharClass.cs`, `WildcardMatcher.cs`, `FuzzyScorer.cs`, `TypoMatcher.cs`, `TermMatcher.cs`
- Test: `tests/FileHound.Core.Tests/MatcherTests.cs`

**Produces:**
```csharp
public enum MatchTier : byte { Exact=0, Prefix=1, WordStart=2, Substring=3, Subsequence=4, Typo=5, None=255 }
public static class WildcardMatcher { public static bool IsMatch(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern); }
public static class FuzzyScorer {
    public static int ScoreWindow(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ReadOnlySpan<char> pattern, int start, int end, Span<int> positions = default);
    public static bool TryMatchSubsequence(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ReadOnlySpan<char> pattern, out int score, out int start, out int end);
}
public static class TypoMatcher {
    public static int MinSubstringDistance(ReadOnlySpan<char> text, Term term);   // Myers semi-global, term ≤ 64 chars
    public static int MaxDistanceFor(int termLength);                             // 4–6 => 1, ≥7 => 2, else 0
}
public static class TermMatcher {
    public static ulong CharMaskOf(ReadOnlySpan<char> fold);
    public static MatchTier Match(ReadOnlySpan<char> name, ReadOnlySpan<char> fold, ulong nameMask, Term t, bool fuzzy, bool typo, out int score);
    public static bool IsWordBoundary(ReadOnlySpan<char> name, int index);
}
```

- [ ] **Step 1: Write the failing tests**
```csharp
public class MatcherTests
{
    static MatchTier M(string name, string q, bool fuzzy = true, bool typo = false)
    {
        var t = QueryParser.Parse(q).PositiveTerms[0];
        var fold = name.ToLowerInvariant();
        return TermMatcher.Match(name, fold, TermMatcher.CharMaskOf(fold), t, fuzzy, typo, out _);
    }
    [Theory]
    [InlineData("report", "report", MatchTier.Exact)]
    [InlineData("report.pdf", "report", MatchTier.Exact)]          // stem
    [InlineData("reports2025.xlsx", "report", MatchTier.Prefix)]
    [InlineData("final_report.docx", "report", MatchTier.WordStart)]
    [InlineData("FinalReport.docx", "report", MatchTier.WordStart)]  // camel hump
    [InlineData("myreport.docx", "report", MatchTier.Substring)]
    [InlineData("quarterly_report_final.xlsx", "qrtrly", MatchTier.Subsequence)]
    [InlineData("unrelated.txt", "report", MatchTier.None)]
    public void Tiers(string name, string q, MatchTier expected) => Assert.Equal(expected, M(name, q));
    [Fact] public void Subsequence_off_when_not_fuzzy() => Assert.Equal(MatchTier.None, M("quarterly_report.xlsx", "qrtrly", fuzzy: false));
    [Fact] public void Typo_found_only_in_typo_pass()
    {
        Assert.Equal(MatchTier.None, M("quarterly_report.xlsx", "quartelry", typo: false));
        Assert.Equal(MatchTier.Typo, M("quarterly_report.xlsx", "quartelry", typo: true));
    }
    [Fact] public void Noise_guard_rejects_scattered_subsequence() => Assert.Equal(MatchTier.None, M("a_very_long_name_with_random_letters_scattered_everywhere.txt", "anws"));
    [Theory]
    [InlineData("hello.txt", "*.txt", true)] [InlineData("hello.txt", "h?llo.*", true)] [InlineData("hello.txt", "*.doc", false)]
    [InlineData("abc", "a*b*c", true)] [InlineData("abc", "a*b*d", false)] [InlineData("", "*", true)]
    public void Wildcards(string text, string pattern, bool expected) => Assert.Equal(expected, WildcardMatcher.IsMatch(text, pattern));
    [Fact] public void Fzf_prefers_boundary_matches()
    {
        Assert.True(FuzzyScorer.TryMatchSubsequence("foo_bar.txt", "foo_bar.txt", "fb", out var s1, out _, out _));
        Assert.True(FuzzyScorer.TryMatchSubsequence("fxxbxx.txt", "fxxbxx.txt", "fb", out var s2, out _, out _));
        Assert.True(s1 > s2);
    }
    [Fact] public void Myers_matches_reference_dp()
    {
        var rnd = new Random(42);
        for (int i = 0; i < 2000; i++)
        {
            string text = RandomWord(rnd, rnd.Next(0, 30)), pat = RandomWord(rnd, rnd.Next(4, 12));
            var t = QueryParser.Parse(pat).PositiveTerms[0];
            Assert.Equal(ReferenceSemiGlobal(text, pat), TypoMatcher.MinSubstringDistance(text, t));
        }
    }
    static string RandomWord(Random r, int n) { var c = new char[n]; for (int i = 0; i < n; i++) c[i] = (char)('a' + r.Next(4)); return new string(c); }
    static int ReferenceSemiGlobal(string text, string pat)
    {
        var prev = new int[text.Length + 1]; var cur = new int[text.Length + 1];
        for (int i = 1; i <= pat.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= text.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (pat[i - 1] == text[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return text.Length == 0 ? pat.Length : prev.Min();
    }
}
```
- [ ] **Step 2:** Run the tests and expect FAIL.
- [ ] **Step 3:** Implement the matchers per architecture §5.1–5.2:
  - **Myers:** `Pv=~0, Mv=0, score=m`. For each character: `Eq=Peq(c)`, `Xv=Eq|Mv`, `Xh=(((Eq&Pv)+Pv)^Pv)|Eq`, `Ph=Mv|~(Xh|Pv)`, `Mh=Pv&Xh`. Adjust `score` using bit `m-1`, then `Ph<<=1; Mh<<=1` with no carry-in, which makes it semi-global. Then `Pv=Mh|~(Xv|Ph)`, `Mv=Ph&Xv`, and track the minimum score.
  - **Char mask:** bits 0–25 are a–z, bits 26–35 are 0–9, and bit 36 is "other".
- [ ] **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): tiered matchers (wildcard, fzf subsequence, Myers typo)`.

### Task 6: SearchEngine, ranking and highlights

**Files:**
- Create: `src/FileHound.Core/Search/SearchRequest.cs`, `SearchResult.cs`, `SearchHit.cs`, `BoundedHeap.cs`, `RankKey.cs`, `SearchEngine.cs`, `Highlighter.cs`
- Test: `tests/FileHound.Core.Tests/SearchEngineTests.cs`

**Consumes:** Tasks 3–5.

**Produces** (architecture §5.4–5.5):
```csharp
public enum SortMode : byte { Relevance, Name, Size, Modified }
public sealed record SearchRequest(string Text, SortMode Sort = SortMode.Relevance, int MaxResults = 5000, bool Fuzzy = true, bool IncludeHidden = true, FileCategory? Category = null);
public readonly record struct SearchHit(VolumeIndex Volume, int Entry, MatchTier Tier, int Score, long Key);
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCount, TimeSpan Elapsed, IReadOnlyList<string> Errors, bool UsedTypoPass) { public static readonly SearchResult Empty; }
public sealed class SearchEngine { public SearchEngine(IClock? clock = null); public SearchResult Search(IReadOnlyList<VolumeIndex> volumes, SearchRequest request, CancellationToken ct = default); }
public static class Highlighter { public static IReadOnlyList<(int Start, int Length)> Compute(string name, Query query); } // merged, sorted ranges
```

- [ ] **Step 1: Write the failing tests**
```csharp
public class SearchEngineTests
{
    static (VolumeIndex v, SearchEngine e) Fixture()
    {
        var v = new VolumeIndex(@"C:\", IndexMode.Standard);
        var docs = v.Add(0, "Documents", EntryFlags.Directory, 0, 0);
        var proj = v.Add(0, "Projects", EntryFlags.Directory, 0, 0);
        v.Add(docs, "report.pdf", EntryFlags.MetadataKnown, 5_000, Ticks(2026, 10, 6));
        v.Add(docs, "final_report.docx", EntryFlags.MetadataKnown, 50_000, Ticks(2025, 1, 1));
        v.Add(docs, "myreport.txt", EntryFlags.MetadataKnown, 10, Ticks(2024, 1, 1));
        v.Add(proj, "quarterly_report_final.xlsx", EntryFlags.MetadataKnown, 2_000_000_000, Ticks(2026, 9, 1));
        v.Add(proj, "node_modules", EntryFlags.Directory, 0, 0);
        v.Add(docs, "secret.txt", EntryFlags.Hidden | EntryFlags.MetadataKnown, 1, 0);
        return (v, new SearchEngine());
    }
    static long Ticks(int y, int m, int d) => new DateTime(y, m, d, 12, 0, 0, DateTimeKind.Utc).Ticks;
    static string[] Names(SearchResult r) => r.Hits.Select(h => h.Volume.Name(h.Entry).ToString()).ToArray();
    static SearchResult Run(string q, SortMode s = SortMode.Relevance, bool fuzzy = true, bool hidden = true, FileCategory? cat = null)
    { var (v, e) = Fixture(); return e.Search([v], new SearchRequest(q, s, 5000, fuzzy, hidden, cat)); }

    [Fact] public void Ranks_by_tier() => Assert.Equal(new[] { "report.pdf", "final_report.docx", "quarterly_report_final.xlsx", "myreport.txt" }, Names(Run("report")));
    [Fact] public void Total_count_reported() => Assert.Equal(4, Run("report").TotalCount);
    [Fact] public void And_terms() => Assert.Equal(new[] { "quarterly_report_final.xlsx" }, Names(Run("report final xlsx")));
    [Fact] public void Not_term() => Assert.DoesNotContain("myreport.txt", Names(Run("report !txt")));
    [Fact] public void Or_alternatives() => Assert.Equal(2, Run("ext:pdf|ext:docx").TotalCount);
    [Fact] public void Folder_filter() => Assert.Equal(new[] { "node_modules" }, Names(Run("folder:node")));
    [Fact] public void Size_sort_and_filter() => Assert.Equal("quarterly_report_final.xlsx", Names(Run("size:>1gb"))[0]);
    [Fact] public void Hidden_excluded_when_requested() { Assert.Single(Names(Run("secret"))); Assert.Empty(Names(Run("secret", hidden: false))); }
    [Fact] public void Category_chip() => Assert.Equal(new[] { "report.pdf", "final_report.docx", "myreport.txt" }.OrderBy(x => x), Names(Run("report", cat: FileCategory.Document)).OrderBy(x => x));
    [Fact] public void Typo_pass_runs_when_few_hits() { var r = Run("quartelry"); Assert.True(r.UsedTypoPass); Assert.Equal("quarterly_report_final.xlsx", Names(r)[0]); }
    [Fact] public void Path_filter_matches_self_and_descendants() => Assert.Equal(3, Run("path:projects").TotalCount); // Projects + its 2 children
    [Fact] public void Name_sort() => Assert.Equal("final_report.docx", Names(Run("report", SortMode.Name))[0]);
    [Fact] public void Max_results_caps_hits_not_count() { var (v, e) = Fixture(); var r = e.Search([v], new SearchRequest("report", MaxResults: 2)); Assert.Equal(2, r.Hits.Count); Assert.Equal(4, r.TotalCount); }
    [Fact] public void Empty_query_returns_nothing() => Assert.Equal(0, Run("").TotalCount);
    [Fact] public void Cancellation_throws() { var (v, e) = Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel(); Assert.Throws<OperationCanceledException>(() => e.Search([v], new SearchRequest("report"), cts.Token)); }
    [Fact] public void Highlights_substring_and_subsequence()
    {
        Assert.Equal(new[] { (6, 6) }, Highlighter.Compute("final_report.docx", QueryParser.Parse("report")));
        Assert.Equal(6, Highlighter.Compute("quarterly_report_final.xlsx", QueryParser.Parse("qrtrly")).Sum(h => h.Length));
    }
}
```

- [ ] **Step 2:** Run the tests and expect FAIL.
- [ ] **Step 3:** Implement per §5.4–5.5:
  - **Chunks:** 32,768 entries each.
  - **Parallelism:** `Parallel.For` with a per-thread `BoundedHeap<SearchHit>` (a min-heap on the comparer, capacity `MaxResults`) and per-thread counts.
  - **Locking:** take the read lock for each chunk.
  - **Typo pass:** as specified.
  - **Comparers:** Relevance, Size and Modified compare `Key` (higher is better), with the entry id as the tiebreak. Name compares the ordinal fold name.
  - **Filter-only queries:** under Relevance, the key comes from (shorter name, shallower depth).
- [ ] **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): parallel SearchEngine with tiered ranking and highlights`.

### Task 7: Performance test

**Files:**
- Test: `tests/FileHound.Core.Tests/PerformanceTests.cs`

- [ ] **Step 1:** Write the test:
```csharp
public class PerformanceTests
{
    [Fact, Trait("Category", "Perf")]
    public void Two_million_entries_median_under_budget()
    {
        var words = new[] { "report","invoice","photo","holiday","project","backup","final","draft","budget","music","video","setup","notes","scan","family","work","data","test","config","readme" };
        var exts = new[] { "pdf","docx","jpg","png","mp4","mp3","txt","xlsx","zip","exe","cs","json" };
        var rnd = new Random(7);
        var v = new VolumeIndex(@"C:\", IndexMode.Standard, 2_100_000);
        var dirs = new List<int> { 0 };
        for (int i = 0; i < 2_000_000; i++)
        {
            bool dir = i % 10 == 0;
            string name = $"{words[rnd.Next(words.Length)]}_{words[rnd.Next(words.Length)]}{rnd.Next(10000)}" + (dir ? "" : "." + exts[rnd.Next(exts.Length)]);
            int e = v.Add(dirs[rnd.Next(dirs.Count)], name, dir ? EntryFlags.Directory : EntryFlags.MetadataKnown, rnd.Next(1 << 30), DateTime.UtcNow.Ticks - rnd.Next(1 << 30) * 10_000L);
            if (dir) dirs.Add(e);
        }
        var engine = new SearchEngine();
        string[] queries = { "report", "inv", "holiday photo", "fnl", "budjet", "ext:pdf invoice", "size:>500mb", "*.json", "proj back", "dm:year notes", "qzx", "music_video" };
        engine.Search([v], new SearchRequest("warmup"));
        var times = queries.Select(q => { var sw = Stopwatch.StartNew(); engine.Search([v], new SearchRequest(q)); return sw.Elapsed.TotalMilliseconds; }).OrderBy(t => t).ToArray();
        var median = times[times.Length / 2];
        Assert.True(median <= 150, $"median {median:F1} ms; all: {string.Join(", ", times.Select(t => t.ToString("F0")))}");
    }
}
```
- [ ] **Step 2:** Run `dotnet test -c Release --filter Category=Perf` and expect PASS. If it fails, profile and optimize (cheaper mask prefilter, avoid recomputing fold masks with a per-entry `ulong[] _mask` cached at Add time) until it passes.
- [ ] **Step 3:** Commit with `test(core): 2M-entry search performance budget`.

### Task 8: Snapshot persistence and statistics

**Files:**
- Create: `src/FileHound.Core/Persistence/SnapshotSerializer.cs`, `src/FileHound.Core/Stats/IndexStatistics.cs`
- Test: `tests/FileHound.Core.Tests/SnapshotTests.cs`, `StatisticsTests.cs`

**Produces:**
```csharp
public static class SnapshotSerializer {
    public static void Save(VolumeIndex v, string path);          // compacts (skips Deleted), atomic tmp+move, XxHash64 trailer
    public static VolumeIndex? Load(string path);                 // null + deletes file on any corruption
    public static string FileNameFor(char letter, uint serial);   // "C_1A2B3C4D.fhx"
}
public sealed record CategoryCount(FileCategory Category, long Count);
public sealed record LargeFile(VolumeIndex Volume, int Entry, long Size);
public sealed record IndexStatistics(long Files, long Folders, IReadOnlyList<CategoryCount> Categories, IReadOnlyList<LargeFile> Largest) {
    public static IndexStatistics Compute(IReadOnlyList<VolumeIndex> volumes, int largestCount = 5);
}
```
- [ ] **Step 1:** Write the failing tests:
  - A round trip preserves paths, sizes, modified times, flags, `Mode`, `VolumeSerial`, `UsnJournalId` and `NextUsn`, and the record map in Turbo mode.
  - Deleted entries are dropped and the paths of the others are unchanged.
  - Flipping one byte in the middle → `Load` returns null and the file is deleted.
  - A truncated file → null.
  - Bad magic → null.
  - Statistics: counts files and folders (excluding root and deleted entries), categories, and the top-N largest in descending order.
- [ ] **Step 2:** Run the tests and expect FAIL. **Step 3:** Implement per architecture §6. **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(core): binary snapshots and index statistics`.

### Task 9: Indexing: interop, drives, elevation, USN record parser

**Files:**
- Create: `src/FileHound.Indexing/Interop/Kernel32.cs`, `UsnStructs.cs`, `Drives/DriveDescriptor.cs`, `DriveDiscovery.cs`, `Elevation.cs`, `Ntfs/UsnRecordParser.cs`
- Test: `tests/FileHound.Indexing.Tests/UsnRecordParserTests.cs`, `DriveDiscoveryTests.cs`

**Produces:**
```csharp
public sealed record DriveDescriptor(char Letter, string Root, string Format, string Label, long TotalSize, long FreeSpace, uint Serial, bool IsRemovable) { public bool IsNtfs => Format == "NTFS"; }
public static class DriveDiscovery { public static IReadOnlyList<DriveDescriptor> GetDrives(); }
public static class Elevation { public static bool IsElevated { get; } }
public readonly ref struct UsnRecord { public long RecordNo { get; } public long ParentRecordNo { get; } public long Usn { get; } public uint Reason { get; } public uint Attributes { get; } public ReadOnlySpan<char> Name { get; } public int Length { get; } }
public static class UsnRecordParser { public static bool TryRead(ReadOnlySpan<byte> buffer, out UsnRecord record); } // V2 and V3 by offset; RecordNo = FRN & 0xFFFFFFFFFFFF
```
`Kernel32` contains `CreateFileW`, `DeviceIoControl` (with an in/out byte-pointer overload), `GetVolumeInformationW` and `CloseHandle` via `[LibraryImport]` with `SafeFileHandle`, plus the constants `FSCTL_QUERY_USN_JOURNAL=0x900F4`, `FSCTL_ENUM_USN_DATA=0x900B3`, `FSCTL_READ_USN_JOURNAL=0x900BB`, the error codes 38/1179/1181 and the USN reason flags.

- [ ] **Step 1:** Write the failing tests. Build a V2 record by hand in a byte[] (RecordLength 64+name, Major 2, FRN `0x0003000000000123`, parent `0x0005000000000005`, Reason `0x100`, Attributes `0x10`, name "hello.txt" at offset 60) and assert the fields: RecordNo `0x123`, ParentRecordNo 5, name. Do the same for V3 (FRN as 16 bytes, name offset 76). Add a DriveDiscovery smoke test: it returns at least one drive with `Root` ending in `\` and includes `C`.
- [ ] **Step 2:** Run the tests and expect FAIL. **Step 3:** Implement. **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(indexing): Win32 interop, drive discovery, USN record parser`.

### Task 10: DirectoryWalker (Standard mode)

**Files:**
- Create: `src/FileHound.Indexing/Walk/DirectoryWalker.cs`, `src/FileHound.Indexing/ScanProgress.cs`
- Test: `tests/FileHound.Indexing.Tests/DirectoryWalkerTests.cs`

**Produces:**
```csharp
public sealed record ScanProgress(long Entries, long Directories, int Skipped, double Fraction);
public sealed class DirectoryWalker {
    public DirectoryWalker(IReadOnlyCollection<string> excludedPaths, int workers);
    // Walks `rootPath` (a drive root or any directory already present as entry `rootEntry`) adding children into `index`.
    public Task<ScanProgress> WalkAsync(VolumeIndex index, string rootPath, int rootEntry, IProgress<ScanProgress>? progress, CancellationToken ct);
}
```
`Fraction` is an estimate: directories completed divided by directories discovered.

- [ ] **Step 1:** Write the failing tests against a temp tree created in the test: `root/a/b/c.txt`, `root/a/d.md`, `root/hidden.txt` (Hidden attribute), `root/skip/x.txt` (excluded), and `root/link` → a junction to `root/a` created with `Directory.CreateSymbolicLink` (skip the junction assertion when creation throws for lack of privilege). Use `new VolumeIndex(rootPath, IndexMode.Standard)`; Task 3 already supports directory roots. Assert:
  - the expected paths are found with the right sizes;
  - the hidden flag is set;
  - the excluded path is absent;
  - the reparse directory is present but not descended into;
  - progress reported at least once.
- [ ] **Step 2:** Run the tests and expect FAIL. **Step 3:** Implement per architecture §7.4 (a Channel work queue with a pending counter for completion). **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(indexing): parallel DirectoryWalker`.

### Task 11: WatcherUpdater (Standard live updates)

**Files:**
- Create: `src/FileHound.Indexing/Walk/WatcherUpdater.cs`
- Test: `tests/FileHound.Indexing.Tests/WatcherUpdaterTests.cs`

**Produces:**
```csharp
public sealed class WatcherUpdater : IDisposable {
    public WatcherUpdater(VolumeIndex index, string rootPath, DirectoryWalker walker, TimeSpan drainInterval);
    public event EventHandler? Applied;          // after each non-empty batch
    public event EventHandler? Overflowed;       // buffer overflow -> caller rescans
    public void Start();
}
```
- [ ] **Step 1:** Write the failing test. Walk a temp tree. Start the updater with a 200 ms drain. Then:
  - Create `new.txt` and wait until `FindByPath` finds it (2 s timeout).
  - Rename it to `renamed.txt` and wait until the old path is gone and the new one is found.
  - Create the directory `sub` containing `inner.txt` (moved in from outside the tree) and wait until `inner.txt` is found.
  - Delete `sub` and wait until `inner.txt` is no longer live.
- [ ] **Step 2:** Run the test and expect FAIL. **Step 3:** Implement per §7.5. **Step 4:** Run the test and expect PASS.
- [ ] **Step 5:** Commit with `feat(indexing): FileSystemWatcher live updates`.

### Task 12: Turbo mode (MftScanner, UsnUpdater, MetadataFiller)

**Files:**
- Create: `src/FileHound.Indexing/Ntfs/MftScanner.cs`, `UsnUpdater.cs`, `src/FileHound.Indexing/Walk/MetadataFiller.cs`
- Test: `tests/FileHound.Indexing.Tests/TurboTests.cs`

**Produces:**
```csharp
public sealed class MftScanner {
    public static bool TryQueryJournal(char letter, out ulong journalId, out long firstUsn, out long nextUsn);
    public VolumeIndex Scan(DriveDescriptor drive, IReadOnlyCollection<string> excluded, IProgress<ScanProgress>? progress, CancellationToken ct); // throws on failure
}
public sealed class UsnUpdater : IDisposable {
    public UsnUpdater(VolumeIndex index, char letter, TimeSpan pollInterval);
    public event EventHandler? Applied; public event EventHandler? JournalInvalid;
    public void Start();
    // test seam: apply an already-read buffer
    internal int ApplyBuffer(ReadOnlySpan<byte> recordsAfterUsnHeader);
}
public sealed class MetadataFiller { public Task FillAsync(VolumeIndex index, IProgress<double>? progress, CancellationToken ct); }
```
- [ ] **Step 1:** Write the failing tests:
  - `UsnUpdater.ApplyBuffer` with hand-built V2 records: a create `(rec 200, parent 5, "new.txt", FILE_CREATE|CLOSE)`, a rename `(rec 200, parent 5, "ren.txt", RENAME_NEW_NAME|CLOSE)` and a delete `(rec 200, FILE_DELETE|CLOSE)`. Assert the index state after each step. No admin needed.
  - `MetadataFiller` on a temp tree built by `VolumeIndexBuilder` with no metadata: after filling, sizes match `FileInfo.Length` and `MetadataKnown` is set.
  - `MftScanner_scans_C_when_elevated`: return early (marked skipped) when `!Elevation.IsElevated`. Otherwise scan C: and assert more than 10,000 entries and that `FindByPath(@"C:\Windows\explorer.exe") >= 0`.
- [ ] **Step 2:** Run the tests and expect FAIL. **Step 3:** Implement per §7.2–7.3. **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(indexing): MFT scanner, USN journal updater, metadata filler`.

### Task 13: IndexManager orchestration

**Files:**
- Create: `src/FileHound.Indexing/IndexManager.cs`, `DriveState.cs`, `IndexOptions.cs`
- Test: `tests/FileHound.Indexing.Tests/IndexManagerTests.cs`

**Produces** (§7.6):
```csharp
public sealed record IndexOptions(string DataDirectory, IReadOnlyCollection<string> ExcludedPaths, bool PreferTurbo = true, Func<IReadOnlyList<DriveDescriptor>>? DriveSource = null, TimeSpan? SnapshotInterval = null);
public enum DriveStatus { Loading, Scanning, FillingDetails, Ready, Offline, Error }
public sealed record DriveState(DriveDescriptor Drive, IndexMode Mode, DriveStatus Status, long Entries, double Progress, int Skipped, bool MetadataComplete, DateTimeOffset? LastIndexed, string? Error);
public sealed class IndexManager : IAsyncDisposable {
    public IndexManager(IndexOptions options);
    public IReadOnlyList<DriveState> Drives { get; }
    public IReadOnlyList<VolumeIndex> Volumes { get; }
    public event EventHandler? StateChanged; public event EventHandler? IndexChanged;
    public Task StartAsync(CancellationToken ct = default);
    public Task RescanAsync(char letter);
    public Task SaveSnapshotsAsync();
}
```
For tests, `DriveSource` injects a fake drive whose `Root` is a temp directory. This works because VolumeIndex supports directory roots (Task 3).

- [ ] **Step 1:** Write the failing tests:
  - Start with a fake drive pointing at a temp tree → the state reaches `Ready` and a search finds the file.
  - `SaveSnapshotsAsync`, then a new manager over the same data directory → the volume is published from the snapshot (status Ready before the walk completes) and the file is found.
  - A watcher change raises `IndexChanged`.
  - `DisposeAsync` stops cleanly.
- [ ] **Step 2:** Run the tests and expect FAIL. **Step 3:** Implement (Turbo is selected only when `Elevation.IsElevated && drive.IsNtfs && PreferTurbo`). **Step 4:** Run the tests and expect PASS.
- [ ] **Step 5:** Commit with `feat(indexing): IndexManager orchestration with snapshots`.

### Task 14: CLI validation tool

**Files:**
- Create: `tools/FileHound.Cli/Program.cs`

The commands are:
- `filehound-cli scan [letters]`: indexes and prints the entries per drive, the time, the mode and the skipped count.
- `filehound-cli search "<query>" [--top 20]`: indexes, then prints the hits with tier and time.
- `filehound-cli bench "<q1>" "<q2>" …`: indexes once, then runs each query 5 times and prints the median.

- [ ] **Step 1:** Implement on top of `IndexManager` with a temp data directory (`--data <dir>`, defaulting to `%LOCALAPPDATA%\FileHound`).
- [ ] **Step 2:** Run `dotnet run --project tools/FileHound.Cli -c Release -- scan C` and record the timings in the commit message.
- [ ] **Step 3:** Commit with `feat(cli): scan/search/bench validation tool`.

### Task 15: Assets (Higgsfield)

**Files:**
- Create: `src/FileHound.App/Assets/*.png`, `app.ico`, `docs/assets/prompts.md`

- [ ] **Step 1:** `higgsfield auth` status, then `higgsfield model list` to pick an image model, and check the account credits.
- [ ] **Step 2:** Generate the mascot assets per UI spec §7 with one shared style preamble: "cute 3D clay render, soft pastel colors, claymorphism, smooth matte plasticine, soft studio lighting, centered, plain flat pastel {bg} background, high detail, no text". Keep the prompts in `docs/assets/prompts.md`.
- [ ] **Step 3:** Generate the category icons (folder, document, image, video, audio, archive, app, code, drive, paw, clock, magnifier), one per call, on a flat white background.
- [ ] **Step 4:** Post-process with a PowerShell + System.Drawing script (`tools/assets/process.ps1`):
  - resize to the target sizes;
  - flood-fill the near-white background to alpha for the icons (tolerance 12);
  - build `app.ico` as a multi-size PNG-compressed ICO.
- [ ] **Step 5:** Review each PNG visually (Read tool), regenerate any that are off-style, then commit with `feat(app): clay hound mascot and icon assets`.

### Task 16: App shell, theme and navigation

**Files:**
- Create: `src/FileHound.App/FileHound.App.csproj` (WinExe, `UseWPF`, `ApplicationManifest`, `ApplicationIcon`), `app.manifest`, `App.xaml(.cs)`
- Create: `Themes/Colors.xaml`, `Typography.xaml`, `Controls.xaml`
- Create: `Services/Log.cs`, `SettingsService.cs` (+ `AppSettings.cs`), `SingleInstance.cs`, `NativeMethods.cs`
- Create: `ViewModels/MainViewModel.cs`, `Views/MainWindow.xaml(.cs)`, with placeholder pages that are real empty UserControls bound to page VMs
- Test: `tests/FileHound.Core.Tests` does not change. Add `tests/FileHound.App.Tests` only if logic appears that Core does not cover. SettingsService round-trip and malformed-file tests go in a small `FileHound.App.Tests` (net10.0-windows, references App).

**Produces:** `AppSettings` (fields per architecture §8.6), `SettingsService.Load()/Save(AppSettings)`, `MainViewModel.Navigate(Page)` where `enum Page { Dashboard, Search, Drives, Settings }`, and the Clay styles: `ClayCard`, `ClayPill`, `ClayButton`, `ClayGhostButton`, `ClayChip` (ToggleButton/RadioButton), `ClayToggle`, `ClaySearchBox` (TextBox), `ClayListBoxItem`, `ClayContextMenu`, `ClayProgress`, `NavItem` (RadioButton) and `CaptionButton`.

- [ ] **Step 1:** Write the SettingsService tests: defaults when the file is missing, a round trip, and malformed JSON → defaults plus `settings.bad.json` created. Run them and expect FAIL.
- [ ] **Step 2:** Implement SettingsService, Log, SingleInstance (mutex `Local\FileHound.SingleInstance` plus EventWaitHandle `Local\FileHound.Activate`; `--after <pid>` waits for that pid) and the theme dictionaries with the exact tokens.
- [ ] **Step 3:** Build MainWindow:
  - WindowChrome per architecture §8.5, with the DWM round-corner call;
  - sidebar (avatar, greeting, nav, Turbo card), header (title, global search pill, status chip, caption buttons), and a ContentControl with DataTemplates per page VM.
- [ ] **Step 4:** Run the tests and expect PASS. Run the app with `dotnet run --project src/FileHound.App`, take a screenshot (PowerShell `CopyFromScreen` of the window rect), and review it against UI §4.
- [ ] **Step 5:** Commit with `feat(app): clay theme, window chrome, sidebar navigation`.

### Task 17: Search page

**Files:**
- Create: `Services/SearchService.cs`, `ShellService.cs`, `ShellIconProvider.cs`, `ToastService.cs`
- Create: `ViewModels/SearchViewModel.cs`, `ResultItem.cs`, `Controls/HighlightText.cs` (attached property building Runs), `Converters/*.cs`
- Create: `Views/SearchView.xaml(.cs)`

**Consumes:** `SearchEngine`, `Highlighter`, `IndexManager`.

**Produces:**
- `SearchService.QueryAsync(SearchRequest)`, returning `(SearchResult, ResultItem[])` with the 40 ms debounce and cancellation.
- `ResultItem { Name, FolderPath, FullPath, SizeText, ModifiedText, IsDirectory, Highlights, Icon (lazy ImageSource) }`.
- ShellService methods `Open`, `RevealInFolder`, `CopyPath`, `CopyName`, `CopyFile`, `ShowProperties`, and `StartDrag`.

- [ ] **Step 1:** Implement SearchService, plus the ResultItem formatting (`FormatSize`: B/KB/MB/GB/TB with 1 decimal place; `FormatModified`: relative under 7 days, otherwise `yyyy-MM-dd`), with unit tests in FileHound.App.Tests for both formatters. Run them and expect PASS.
- [ ] **Step 2:** Build the SearchView per UI §5.2: large pill, chips, toolbar, results card with header and virtualized ListBox (`VirtualizingPanel.IsVirtualizing=True`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel`, `IsDeferredScrollingEnabled=False`), empty states, details footer, and context menu.
- [ ] **Step 3:** Add the keyboard handling from UI §6, drag-out (disabled when elevated) and toasts.
- [ ] **Step 4:** Run the app, search real drives, check that highlighting, sorting and every action work, and screenshot.
- [ ] **Step 5:** Commit with `feat(app): search page with virtualized results and shell actions`.

### Task 18: Dashboard

**Files:**
- Create: `Controls/DonutChart.cs` (FrameworkElement `OnRender` drawing arcs with a gap), `ViewModels/DashboardViewModel.cs`, `DriveItem.cs`, `Views/DashboardView.xaml(.cs)`

**Consumes:** `IndexStatistics.Compute`, `IndexManager.Drives`, `SettingsService` recent searches.

- [ ] **Step 1:** Implement the DashboardViewModel. It refreshes on `StateChanged` and `IndexChanged`, throttled to once every 2 s, and computes the statistics off the UI thread.
- [ ] **Step 2:** Build the view per UI §5.1: hero, 4 stat cards, drives overview, donut and legend, recent searches, largest files, tip banner.
- [ ] **Step 3:** Run the app, take a screenshot, and compare it with reference image 1 for spacing, colors and shadows. Iterate.
- [ ] **Step 4:** Commit with `feat(app): dashboard with stats, drives, file-type donut`.

### Task 19: Drives page, Settings page, hotkey, tray, startup, Turbo relaunch

**Files:**
- Create: `Services/HotkeyService.cs`, `TrayService.cs`, `StartupService.cs`, `ElevationService.cs`
- Create: `ViewModels/DrivesViewModel.cs`, `SettingsViewModel.cs`, `Views/DrivesView.xaml`, `SettingsView.xaml`

- [ ] **Step 1:** Write `HotkeyGesture.TryParse("Ctrl+Alt+Space", out mods, out vk)` with tests for valid strings, invalid strings and `ToString` round trips. Run them and expect FAIL.
- [ ] **Step 2:** Implement HotkeyService (`RegisterHotKey` + `WM_HOTKEY` hook), TrayService (H.NotifyIcon `TaskbarIcon` with a context menu), StartupService (HKCU `...\Run` value `FileHound` = `"exe" --minimized`) and ElevationService.RelaunchElevated (handles the UAC cancel, error 1223). Run the tests and expect PASS.
- [ ] **Step 3:** Build DrivesView (UI §5.3) and SettingsView (UI §5.4: toggles, hotkey capture box, excluded folders with OpenFolderDialog, About links).
- [ ] **Step 4:** Run the app, then check:
  - the hotkey shows and hides the window;
  - the tray menu works;
  - closing the window hides it to the tray;
  - Rescan works;
  - settings persist across a restart.

  Then screenshot.
- [ ] **Step 5:** Commit with `feat(app): drives & settings pages, global hotkey, tray, startup, turbo relaunch`.

### Task 20: Validation, docs, release

**Files:**
- Create: `README.md` (overwrite the stub), `THIRD-PARTY-NOTICES.md`, `docs/screenshots/*.png`

- [ ] **Step 1:** Run `dotnet build -c Release` (0 warnings in Core and Indexing) and `dotnet test -c Release`. Everything should be green, and the output gets pasted into the summary.
- [ ] **Step 2:** End-to-end in the real app:
  - first-run Standard indexing of all drives;
  - restart → the snapshot loads fast;
  - live create, rename and delete;
  - every query syntax example from FR-9.
- [ ] **Step 3:** Fix any bugs found, using the systematic-debugging skill, each with a regression test.
- [ ] **Step 4:** Write the README:
  - features and screenshots;
  - build and run commands;
  - a query syntax table;
  - Turbo vs Standard;
  - keyboard shortcuts.

  Write THIRD-PARTY-NOTICES covering fzf (MIT), CommunityToolkit.Mvvm, H.NotifyIcon.Wpf and System.IO.Hashing.
- [ ] **Step 5:** Commit with `docs: README, notices, screenshots`. Then run `git push -u origin main`.
