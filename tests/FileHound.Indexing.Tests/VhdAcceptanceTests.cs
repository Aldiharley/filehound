using FileHound.Core.Recovery;
using FileHound.Indexing.Interop;
using FileHound.Indexing.Ntfs;
using FileHound.Indexing.Recovery;

namespace FileHound.Indexing.Tests;

/// <summary>End-to-end undelete on a fresh NTFS volume (VHDX via diskpart). Elevated only.</summary>
public class VhdAcceptanceTests
{
    private static string Sha(byte[] b) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(b));

    [Fact, Trait("Category", "Elevated")]
    public void Undelete_round_trip_on_a_fresh_ntfs_volume()
    {
        if (!Elevation.IsElevated) return;
        using var vhd = VirtualDisk.Create(Path.Combine(Path.GetTempPath(), $"fh-{Guid.NewGuid():N}.vhdx"), out string reason);
        if (vhd is null) return; // diskpart unavailable here: nothing to assert (reason is in the test output)
        var root = vhd.Root;
        var rng = new Random(42);
        byte[] big = new byte[3_000_000]; rng.NextBytes(big);      // multi-run candidate
        byte[] small = new byte[600]; rng.NextBytes(small);         // resident candidate
        byte[] jpg = new byte[200_000]; rng.NextBytes(jpg); jpg[0] = 0xFF; jpg[1] = 0xD8; jpg[2] = 0xFF;
        Directory.CreateDirectory(root + "Photos");
        File.WriteAllBytes(root + @"Photos\holiday.jpg", jpg);
        File.WriteAllBytes(root + "big.bin", big);
        File.WriteAllBytes(root + "small.txt", small);
        File.WriteAllBytes(root + "victim.bin", big);
        var expected = new Dictionary<string, string> { ["holiday.jpg"] = Sha(jpg), ["big.bin"] = Sha(big), ["small.txt"] = Sha(small) };
        foreach (var n in new[] { @"Photos\holiday.jpg", "big.bin", "small.txt", "victim.bin" }) File.Delete(root + n);
        File.WriteAllBytes(root + "overwriter.bin", new byte[6_000_000]);   // likely lands on victim's clusters
        Thread.Sleep(3000);                                                  // let the lazy writer flush $MFT and $Bitmap

        var drive = DriveDiscovery.GetDrives().Single(d => d.Letter == vhd.Letter);
        using var reader = VolumeReader.Open(drive);
        var bitmap = ClusterBitmap.Load(reader);
        var index = new MftScanner().Scan(drive, [], null, CancellationToken.None);
        var candidates = new MftUndeleteSource(reader, bitmap, index, null).Scan(null, CancellationToken.None);
        string about = $"read path={reader.PathDescription}; bitmap={bitmap.Source}; candidates={string.Join(", ", candidates.Select(c => $"{c.Name}:{c.Grade}/{c.PercentIntact}%"))}";
        var dest = Directory.CreateTempSubdirectory("fh-vhd-out-").FullName;
        try
        {
            foreach (var (name, sha) in expected)
            {
                var c = Assert.Single(candidates, x => x.Name == name);
                Assert.True(c.Grade is RecoveryGrade.Excellent or RecoveryGrade.Good, $"{name}: {c.Grade} ({c.Detail}) — {about}");
                var (_, got, _, _) = UndeleteWriter.Recover(reader, bitmap, (UndeleteRecord)c.Key, c.Grade, Path.Combine(dest, name), CancellationToken.None);
                Assert.True(sha == got, $"{name}: hash mismatch — {about}");
            }
            Assert.Equal(root + "Photos", Assert.Single(candidates, x => x.Name == "holiday.jpg").OriginalFolder);
            var victim = Assert.Single(candidates, x => x.Name == "victim.bin");
            // A fresh volume may place the overwriter elsewhere; the point is that the grade tells the truth either way.
            if (victim.Grade is RecoveryGrade.Excellent or RecoveryGrade.Good)
            {
                var (_, got, _, _) = UndeleteWriter.Recover(reader, bitmap, (UndeleteRecord)victim.Key, victim.Grade, Path.Combine(dest, "victim.bin"), CancellationToken.None);
                Assert.True(Sha(big) == got, $"victim graded {victim.Grade} but its data changed — {about}");
            }
            else Assert.True(victim.Grade is RecoveryGrade.Partial or RecoveryGrade.Overwritten, about);
        }
        finally { Directory.Delete(dest, true); }
    }
}
