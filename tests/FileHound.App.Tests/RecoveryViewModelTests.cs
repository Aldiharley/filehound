using FileHound.App.ViewModels.Recovery;
using FileHound.Core.Recovery;

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
