using FileHound.Core.Index;

namespace FileHound.Core.Carving;

public enum CarveStatus
{
    /// <summary>A complete file of the reported size starts at the span's first byte.</summary>
    Ok,
    /// <summary>The bytes are not this kind of file.</summary>
    Reject,
    /// <summary>The structure is valid so far but continues past the span; call again with more bytes.</summary>
    NeedMore,
}

/// <summary>What a validator says about the bytes it was given.</summary>
public readonly record struct CarveResult(CarveStatus Status, long Size, string? Info)
{
    public static CarveResult Ok(long size, string? info = null) => new(CarveStatus.Ok, size, info);
    public static readonly CarveResult Reject = new(CarveStatus.Reject, 0, null);
    public static readonly CarveResult NeedMore = new(CarveStatus.NeedMore, 0, null);
    public bool IsOk => Status == CarveStatus.Ok;
}

/// <summary>A pure parser: no I/O, bounded work, never reads past the span.</summary>
public delegate CarveResult CarveValidator(ReadOnlySpan<byte> data);

/// <summary>One recognisable file type: how to spot it and how to measure it.</summary>
public sealed record CarveType(string Id, string Label, string Extension, FileCategory Category, long MaxSize, int MagicOffset, byte[][] Magics, CarveValidator Validate)
{
    /// <summary>True when any of the magics appears at <see cref="MagicOffset"/>.</summary>
    public bool MatchesMagic(ReadOnlySpan<byte> data)
    {
        foreach (var m in Magics)
        {
            if (data.Length >= MagicOffset + m.Length && data.Slice(MagicOffset, m.Length).SequenceEqual(m)) return true;
        }
        return false;
    }
}
