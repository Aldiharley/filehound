namespace FileHound.Indexing.Recovery;

/// <summary>A Volume Shadow Copy as seen from the live system.</summary>
public sealed record ShadowCopy(string Id, string Device, DateTime CreatedUtc, string VolumeName, char? Letter);

/// <summary>Enumerates snapshots. Stage B Task 7 adds WMI enumeration, vssadmin parsing and creation; until then there are none.</summary>
public static class ShadowCopies
{
    /// <summary>Snapshots of the drive, newest first; empty when there are none or the process is not elevated.</summary>
    public static IReadOnlyList<ShadowCopy> List(char letter) => [];
}
