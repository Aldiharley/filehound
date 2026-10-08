using FileHound.Core.Recovery;
using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class UndeleteTests
{
    private static (SyntheticVolume Vol, VolumeIndex Index) Setup()
    {
        var vol = new SyntheticVolume(clusters: 512, records: 128);
        var index = new VolumeIndex(@"Q:\", IndexMode.Turbo);
        index.SetRecord(0, 5);
        index.Add(0, "Docs", EntryFlags.Directory, 0, 0, recordNo: 100);
        vol.SetRecord(100, new MftRecordBuilder().Sequence(2).InUse(directory: true).FileName(5, "Docs").Build());
        return (vol, index);
    }

    private static List<RecoveryCandidate> Scan(SyntheticVolume vol, VolumeIndex index, IReadOnlyList<DeletionEntry>? log = null)
    {
        using var r = vol.OpenReader();
        return new MftUndeleteSource(r, ClusterBitmap.Load(r), index, log).Scan(null, CancellationToken.None);
    }

    [Fact]
    public void Deleted_file_with_free_clusters_is_excellent_and_gets_its_folder()
    {
        var (vol, index) = Setup();
        var jpg = new byte[4096];
        jpg[0] = 0xFF; jpg[1] = 0xD8; jpg[2] = 0xFF;
        vol.WriteCluster(200, jpg);
        vol.SetAllocated(200, false);
        vol.SetAllocated(201, false);
        vol.SetRecord(40, new MftRecordBuilder().Sequence(5).FileName(100, "photo.jpg", parentSequence: 2, realSize: 6000)
            .NonResidentData(6000, SyntheticVolume.Runs((200, 2))).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal("photo.jpg", c.Name);
        Assert.Equal(@"Q:\Docs", c.OriginalFolder);
        Assert.Equal(RecoveryGrade.Excellent, c.Grade);
        Assert.Equal(100, c.PercentIntact);
        Assert.Equal(6000, c.Size);
        Assert.Equal(RecoverySource.Undelete, c.Source);
        var key = Assert.IsType<UndeleteRecord>(c.Key);
        Assert.Equal(40, key.RecordNo);
        Assert.Equal(5, key.Sequence);
    }

    [Fact]
    public void Partially_reused_clusters_give_a_percentage()
    {
        var (vol, index) = Setup();
        for (long l = 300; l < 304; l++) vol.SetAllocated(l, l == 301);
        vol.SetRecord(41, new MftRecordBuilder().FileName(100, "doc.bin", parentSequence: 2, realSize: 16384)
            .NonResidentData(16384, SyntheticVolume.Runs((300, 4))).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal(RecoveryGrade.Partial, c.Grade);
        Assert.Equal(75, c.PercentIntact);
    }

    [Fact]
    public void Fully_reused_clusters_are_overwritten()
    {
        var (vol, index) = Setup();
        vol.SetAllocated(305, true);
        vol.SetRecord(43, new MftRecordBuilder().FileName(100, "lost.bin", parentSequence: 2, realSize: 100)
            .NonResidentData(100, SyntheticVolume.Runs((305, 1))).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal(RecoveryGrade.Overwritten, c.Grade);
        Assert.Equal(0, c.PercentIntact);
    }

    [Fact]
    public void Zeroed_first_cluster_means_trimmed()
    {
        var (vol, index) = Setup();
        vol.SetAllocated(310, false);
        vol.SetRecord(42, new MftRecordBuilder().FileName(100, "gone.txt", parentSequence: 2, realSize: 100)
            .NonResidentData(100, SyntheticVolume.Runs((310, 1)), initializedSize: 100).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal(RecoveryGrade.Zeroed, c.Grade);
    }

    [Fact]
    public void Header_mismatch_downgrades_to_good()
    {
        var (vol, index) = Setup();
        var notPng = new byte[4096];
        notPng[0] = 0x41;
        vol.WriteCluster(320, notPng);
        vol.SetAllocated(320, false);
        vol.SetRecord(44, new MftRecordBuilder().FileName(100, "pic.png", parentSequence: 2, realSize: 50)
            .NonResidentData(50, SyntheticVolume.Runs((320, 1))).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal(RecoveryGrade.Good, c.Grade);
        Assert.Contains("header", c.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resident_file_is_excellent()
    {
        var (vol, index) = Setup();
        vol.SetRecord(45, new MftRecordBuilder().FileName(100, "tiny.txt", parentSequence: 2, realSize: 7).ResidentData(7).Build());
        var c = Scan(vol, index).Single();
        Assert.Equal(RecoveryGrade.Excellent, c.Grade);
        Assert.Equal(7, c.Size);
    }

    [Fact]
    public void Deleted_parent_chain_is_followed_when_sequence_matches()
    {
        var (vol, index) = Setup();
        vol.SetRecord(50, new MftRecordBuilder().Sequence(3).Directory().FileName(100, "Old", parentSequence: 2).Build());   // deleted dir, seq 3
        vol.SetRecord(51, new MftRecordBuilder().FileName(50, "note.txt", parentSequence: 3).ResidentData(5).Build());
        vol.SetRecord(52, new MftRecordBuilder().FileName(50, "stale.txt", parentSequence: 9).ResidentData(5).Build()); // parent reused since
        var cs = Scan(vol, index);
        Assert.Equal(@"Q:\Docs\Old", cs.Single(c => c.Name == "note.txt").OriginalFolder);
        Assert.Null(cs.Single(c => c.Name == "stale.txt").OriginalFolder);
        var dir = cs.Single(c => c.Name == "Old");
        Assert.Equal(@"Q:\Docs", dir.OriginalFolder);
        Assert.True(dir.IsDirectory);
    }

    [Fact]
    public void Deletion_log_supplies_folder_and_time_for_the_same_record()
    {
        var (vol, index) = Setup();
        vol.SetRecord(60, new MftRecordBuilder().Sequence(4).FileName(777, "orphan.txt", parentSequence: 1).ResidentData(5).Build());
        var when = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var log = new List<DeletionEntry> { new(60, 4, 777, "orphan.txt", @"Q:\Gone", 5, false, 0, when, DeletionKind.Deleted, 1) };
        var c = Scan(vol, index, log).Single();
        Assert.Equal(@"Q:\Gone", c.OriginalFolder);
        Assert.Equal(when, c.DeletedUtc!.Value.Ticks);
    }

    [Fact]
    public void Encrypted_in_use_extension_and_metafile_records_are_handled()
    {
        var (vol, index) = Setup();
        vol.SetRecord(70, new MftRecordBuilder().FileName(100, "secret.txt", parentSequence: 2)
            .NonResidentData(10, SyntheticVolume.Runs((330, 1)), flags: MftRecord.DataEncrypted).Build());
        vol.SetRecord(71, new MftRecordBuilder().InUse().FileName(100, "live.txt", parentSequence: 2).ResidentData(1).Build());
        vol.SetRecord(72, new MftRecordBuilder().Extension(70).FileName(100, "ext.txt", parentSequence: 2).Build());
        vol.SetRecord(12, new MftRecordBuilder().FileName(5, "$Reserved", parentSequence: 1).Build());
        var cs = Scan(vol, index);
        Assert.Equal(RecoveryGrade.Encrypted, cs.Single().Grade);
    }

    [Fact]
    public void Progress_is_reported()
    {
        var (vol, index) = Setup();
        using var r = vol.OpenReader();
        UndeleteProgress? last = null;
        new MftUndeleteSource(r, ClusterBitmap.Load(r), index, null).Scan(new Progress<UndeleteProgress>(p => last = p), CancellationToken.None);
        // Progress<T> posts asynchronously; poll briefly.
        for (int i = 0; i < 50 && last is null; i++) Thread.Sleep(20);
        Assert.NotNull(last);
        Assert.Equal(128, last!.RecordsTotal);
    }

    [Theory]
    [InlineData(".jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(".png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, true)]
    [InlineData(".pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }, true)]
    [InlineData(".docx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, true)]
    [InlineData(".mp4", new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 }, true)]
    [InlineData(".jpg", new byte[] { 0x00, 0x00, 0x00, 0x00 }, false)]
    [InlineData(".txt", new byte[] { 0x41, 0x42 }, null)]
    [InlineData(".jpg", new byte[] { 0xFF }, null)]
    public void Content_check(string ext, byte[] head, bool? expected) => Assert.Equal(expected, ContentCheck.LooksLike(ext, head));
}
