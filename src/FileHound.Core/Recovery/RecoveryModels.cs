namespace FileHound.Core.Recovery;

/// <summary>Where a recovery candidate came from (cheapest and most reliable first).</summary>
public enum RecoverySource { RecycleBin, DeletionLog, Undelete, ShadowCopy, Carving }

/// <summary>How much of a candidate's data is still on disk. Never promises more than the evidence supports.</summary>
public enum RecoveryGrade
{
    /// <summary>All data still on disk (or the file is guaranteed intact, e.g. in the Recycle Bin).</summary>
    Excellent,
    /// <summary>All data appears intact but could not be fully verified.</summary>
    Good,
    /// <summary>Some clusters have been reused by other files.</summary>
    Partial,
    /// <summary>Every cluster has been reused.</summary>
    Overwritten,
    /// <summary>The clusters read back as zeros (SSD TRIM discarded the data).</summary>
    Zeroed,
    /// <summary>EFS-encrypted; cannot be recovered.</summary>
    Encrypted,
    Unknown,
}

/// <summary>One thing that can be recovered. <see cref="Key"/> is a source-specific handle used to perform the recovery.</summary>
public sealed record RecoveryCandidate(
    RecoverySource Source,
    string Name,
    string? OriginalFolder,
    long Size,
    DateTime? ModifiedUtc,
    DateTime? DeletedUtc,
    RecoveryGrade Grade,
    int PercentIntact,
    bool IsDirectory,
    string? Detail,
    object Key)
{
    public string OriginalPath => OriginalFolder is null ? Name : Path.Combine(OriginalFolder, Name);
}

/// <summary>Where a stretch of a recovered file came from on the volume (DFXML byte_run). <see cref="ImageOffset"/> is -1 when not from disk.</summary>
public sealed record ByteRun(long FileOffset, long Length, long ImageOffset);

/// <summary>The outcome of recovering one candidate.</summary>
public sealed record RecoveredFile(RecoveryCandidate Candidate, string RecoveredPath, long Bytes, string Sha256Hex, RecoveryGrade FinalGrade, string? Error,
    IReadOnlyList<ByteRun>? Runs = null)
{
    public bool Succeeded => Error is null;
}
