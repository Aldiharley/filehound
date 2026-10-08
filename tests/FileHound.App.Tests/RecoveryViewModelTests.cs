using FileHound.App.ViewModels.Recovery;
using FileHound.Core.Recovery;
using FileHound.Indexing.Recovery;

namespace FileHound.App.Tests;

public class RecoveryItemTests
{
    private static RecoveryCandidate Cand(RecoveryGrade g, int pct = 100, object? key = null) =>
        new(RecoverySource.Undelete, "f.txt", @"C:\d", 10, null, null, g, pct, false, null, key ?? 0);

    [Theory]
    [InlineData(RecoveryGrade.Excellent, "Excellent", "Excellent")]
    [InlineData(RecoveryGrade.Partial, "Partial", "Partially overwritten (40%)")]
    [InlineData(RecoveryGrade.Overwritten, "Overwritten", "Overwritten")]
    [InlineData(RecoveryGrade.Zeroed, "Zeroed", "Zeroed")]
    [InlineData(RecoveryGrade.Encrypted, "Encrypted", "Encrypted")]
    public void Grade_text_mapping(RecoveryGrade g, string key, string text)
    {
        var (k, t, tip) = RecoveryItem.Describe(Cand(g, 40));
        Assert.Equal(key, k);
        Assert.Equal(text, t);
        Assert.DoesNotContain("100%", tip);
    }

    [Fact]
    public void Zeroed_tooltip_uses_the_ssd_caveat_verbatim() =>
        Assert.Equal("On SSDs, Windows tells the drive to discard deleted data within seconds. If a file shows Zeroed, no copy of it exists on this drive.", RecoveryItem.Describe(Cand(RecoveryGrade.Zeroed)).Tooltip);

    [Theory]
    [InlineData(DeletionKind.Recycled, SlotState.Pending, "Recycled", "In Recycle Bin")]
    [InlineData(DeletionKind.Replaced, SlotState.Pending, "Replaced", "Replaced")]
    [InlineData(DeletionKind.Deleted, SlotState.Free, "Excellent", "Recoverable (slot intact)")]
    [InlineData(DeletionKind.Deleted, SlotState.Reused, "Overwritten", "Record reused")]
    [InlineData(DeletionKind.Deleted, SlotState.Pending, "Unknown", "Checking…")]
    public void Deletion_log_grade_mapping(DeletionKind kind, SlotState slot, string key, string text)
    {
        var entry = new DeletionEntry(1, 1, 5, "f.txt", @"C:\d", 10, false, 0, 0, kind, 1);
        var item = new DeletionLogItem(entry) { Slot = slot };
        var (k, t, _) = RecoveryItem.Describe(new RecoveryCandidate(RecoverySource.DeletionLog, "f.txt", @"C:\d", 10, null, null, RecoveryGrade.Unknown, 0, false, null, item));
        Assert.Equal(key, k);
        Assert.Equal(text, t);
    }

    [Fact]
    public void Unknown_folder_is_flagged()
    {
        var item = new RecoveryItem(new RecoveryCandidate(RecoverySource.Undelete, "x", null, 1, null, null, RecoveryGrade.Good, 100, false, null, 0), DateTime.UtcNow);
        Assert.True(item.IsUnknownFolder);
        Assert.Equal("<unknown folder>", item.Folder);
    }

    [Fact]
    public void Outcome_sets_status_and_short_hash()
    {
        var c = Cand(RecoveryGrade.Excellent);
        var item = new RecoveryItem(c, DateTime.UtcNow);
        item.ApplyOutcome(new RecoveredFile(c, @"E:\r\f.txt", 10, "0123456789abcdef0123", RecoveryGrade.Excellent, null));
        Assert.Equal("Recovered ✓", item.Status);
        Assert.Equal("0123456789ab…", item.Sha256Short);
        item.ApplyOutcome(new RecoveredFile(c, "", 0, "", RecoveryGrade.Excellent, "disk full"));
        Assert.Equal("Failed", item.StatusKey);
        Assert.Contains("disk full", item.Status);
    }
}

public class UndeleteTabViewModelTests
{
    private static RecoveryCandidate C(string name, RecoveryGrade g, int pct, long size) =>
        new(RecoverySource.Undelete, name, @"C:\d", size, null, null, g, pct, false, null, new object());

    [Fact]
    public async Task Scan_fills_items_sorted_by_best_grade_and_reports_intact_estimate()
    {
        var vm = new UndeleteTabViewModel((_, _, _) => Task.FromResult<IReadOnlyList<RecoveryCandidate>>(
            [C("b", RecoveryGrade.Partial, 40, 1000), C("a", RecoveryGrade.Excellent, 100, 3000)]));
        await vm.LoadAsync(null);
        Assert.Equal("Scan", vm.ScanButtonText);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.HasScanned);
        Assert.Equal("Rescan", vm.ScanButtonText);
        Assert.Equal(2, vm.Count);
        Assert.Equal("a", vm.Items[0].Name);
        Assert.Equal("", vm.EstimatedIntactText);
        vm.Items[0].IsSelected = true;
        vm.Items[1].IsSelected = true;
        Assert.Equal("Estimated intact: 85%", vm.EstimatedIntactText);   // (3000*100 + 1000*40) / 4000
        vm.Sort = UndeleteSort.Name;
        Assert.Equal("a", vm.Items[0].Name);
        vm.Filter = "b";
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Scan_error_is_shown_not_thrown()
    {
        var vm = new UndeleteTabViewModel((_, _, _) => throw new NotSupportedException("No readable path to C:"));
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Contains("No readable path", vm.Error);
        Assert.False(vm.IsScanning);
        Assert.False(vm.HasScanned);
    }
}

public class PreviousVersionsTabViewModelTests
{
    [Fact]
    public async Task Lookup_lists_versions_and_freeze_needs_confirmation()
    {
        var snap = new ShadowCopy("{1}", @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6", new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), "", 'C');
        bool frozen = false;
        var vm = new PreviousVersionsTabViewModel(
            versions: (_, p) => [new ShadowVersion(snap, p, snap.Device + p[2..], 10, DateTime.UtcNow, false)],
            freeze: _ => { frozen = true; return "{new}"; },
            snapshots: _ => [snap])
        { Path = @"C:\d\f.txt" };
        await vm.LookupCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.Items);
        Assert.Equal("Excellent", item.GradeKey);
        Assert.Equal("f.txt", item.Name);
        Assert.Equal(@"C:\d", item.Folder);
        Assert.IsType<ShadowVersion>(item.Candidate.Key);

        vm.ConfirmFreeze = () => false;
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.False(frozen);
        vm.ConfirmFreeze = () => true;
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.True(frozen);
        Assert.Contains("1 snapshot", vm.SnapshotsText);
    }

    [Fact]
    public async Task Bad_path_is_reported()
    {
        var vm = new PreviousVersionsTabViewModel(versions: (_, _) => throw new ArgumentException("The path must be on drive C:.")) { Path = @"D:\x" };
        await vm.LookupCommand.ExecuteAsync(null);
        Assert.Contains("must be on drive", vm.Error);
    }
}

public class DeletedTabNoiseTests
{
    private static DeletionEntry E(string name, string parent = @"C:\Users\me\Documents", bool dir = false) =>
        new(1, 1, 5, name, parent, 1, dir, 0, 0, DeletionKind.Deleted, 1);

    [Theory]
    [InlineData("~$report.docx", @"C:\Users\me\Documents", true)]
    [InlineData("a.tmp", @"C:\Users\me\Documents", true)]
    [InlineData("x.bin", @"C:\Users\me\AppData\Local\Temp\abc", true)]
    [InlineData("data_0", @"C:\Users\me\AppData\Local\Google\Chrome\User Data\Default\Cache", true)]
    [InlineData("report.docx", @"C:\Users\me\Documents", false)]
    [InlineData("holiday.jpg", @"C:\Users\me\Pictures\Temporary", false)]
    public void Noise_filter(string name, string parent, bool noise) => Assert.Equal(noise, DeletedTabViewModel.IsNoise(E(name, parent)));

    [Fact]
    public void Directories_are_never_noise() => Assert.False(DeletedTabViewModel.IsNoise(E("Temp", @"C:\Users\me\AppData\Local", dir: true)));

    [Fact]
    public void Candidate_from_entry_keeps_times_and_unknown_folder()
    {
        var e = new DeletionEntry(7, 2, 5, "f.txt", "", 42, false, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks, DeletionKind.Deleted, 9);
        var c = DeletedTabViewModel.ToCandidate(e);
        Assert.Null(c.OriginalFolder);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), c.DeletedUtc);
        Assert.Equal(42, c.Size);
        Assert.Same(e, ((DeletionLogItem)c.Key).Entry);
    }
}
