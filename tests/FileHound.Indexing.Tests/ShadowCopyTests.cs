using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

public class ShadowCopyTests
{
    private const string Sample = """
        vssadmin 1.1 - Volume Shadow Copy Service administrative command-line tool

        Contents of shadow copy set ID: {1}
           Contained 1 shadow copies at creation time: 10/5/2026 9:12:33 PM
              Shadow Copy ID: {aaaa-1}
                 Original Volume: (C:)\\?\Volume{v1}\
                 Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6
                 Originating Machine: pc
                 Attributes: Persistent, Client-accessible
        Contents of shadow copy set ID: {2}
           Contained 1 shadow copies at creation time: 10/7/2026 8:00:00 AM
              Shadow Copy ID: {bbbb-2}
                 Original Volume: (E:)\\?\Volume{v2}\
                 Shadow Copy Volume: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy9
        """;

    [Fact]
    public void Parses_vssadmin_output()
    {
        var all = ShadowCopies.ParseVssadmin(Sample, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(2, all.Count);
        var c = all.Single(s => s.Letter == 'C');
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy6", c.Device);
        Assert.Equal("{aaaa-1}", c.Id);
        Assert.Equal(@"\\?\Volume{v1}\", c.VolumeName);
        Assert.Equal(new DateTime(2026, 10, 5, 21, 12, 33), c.CreatedUtc.ToLocalTime());
        Assert.Equal('E', all[1].Letter);
    }

    [Fact]
    public void Empty_output_parses_to_nothing() => Assert.Empty(ShadowCopies.ParseVssadmin(""));

    [Fact]
    public void Restore_name_never_overwrites() =>
        Assert.Equal(@"C:\d\report (from 2026-10-08 1530).docx", ShadowCopySource.RestoreName(@"C:\d\report.docx", new DateTime(2026, 10, 8, 15, 30, 0)));

    [Fact]
    public void Versions_rejects_a_path_on_another_drive() =>
        Assert.Throws<ArgumentException>(() => ShadowCopySource.Versions('C', @"D:\x.txt"));

    [Fact]
    public void Save_and_restore_copy_without_overwriting()
    {
        using var tree = new TempTree();
        var snapshot = new ShadowCopy("{1}", tree.Dir("snap"), new DateTime(2026, 10, 8, 15, 30, 0, DateTimeKind.Utc), "", 'C');
        var original = tree.Path(@"live\report.txt");
        tree.File(@"live\report.txt", 3);
        tree.File(@"snap\live\report.txt");
        File.WriteAllText(tree.Path(@"snap\live\report.txt"), "older");
        var v = new ShadowVersion(snapshot, original, tree.Path(@"snap\live\report.txt"), 5, DateTime.UtcNow, false);

        string saved = ShadowCopySource.SaveTo(v, tree.Dir("out"));
        Assert.Equal("older", File.ReadAllText(saved));
        Assert.EndsWith("report.txt", saved);

        string restored = ShadowCopySource.RestoreInPlace(v);
        Assert.Equal("older", File.ReadAllText(restored));
        Assert.Equal(3, new FileInfo(original).Length);          // the live file is untouched
        Assert.Contains("(from ", restored);
        string restoredAgain = ShadowCopySource.RestoreInPlace(v);
        Assert.NotEqual(restored, restoredAgain);
    }

    [Fact, Trait("Category", "Elevated")]
    public void Reads_a_previous_version_of_hosts_when_a_snapshot_exists()
    {
        if (!Elevation.IsElevated) return;
        var versions = ShadowCopySource.Versions('C', @"C:\Windows\System32\drivers\etc\hosts");
        if (ShadowCopies.List('C').Count == 0) { Assert.Empty(versions); return; }
        Assert.NotEmpty(versions);
        Assert.True(versions[0].Size > 0);
    }
}
