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

/// <summary>
/// What a validator says about the bytes it was given. For <see cref="CarveStatus.NeedMore"/>, <see cref="Size"/> is the
/// length that is already known to be a complete, valid prefix (0 when nothing is), which the carver accepts when it
/// cannot read any further; <see cref="Required"/> is the total the structure claims, when the format states one.
/// </summary>
public readonly record struct CarveResult(CarveStatus Status, long Size, string? Info, long Required = 0)
{
    public static CarveResult Ok(long size, string? info = null) => new(CarveStatus.Ok, size, info);
    public static readonly CarveResult Reject = new(CarveStatus.Reject, 0, null);
    public static readonly CarveResult NeedMore = new(CarveStatus.NeedMore, 0, null);
    /// <summary>The structure runs past the span; <paramref name="validSoFar"/> bytes are a usable file on their own.</summary>
    public static CarveResult NeedMoreAfter(long validSoFar, string? info = null) => new(CarveStatus.NeedMore, validSoFar, info);
    /// <summary>The format states its total size; the carver fetches exactly that (or gives up when it cannot).</summary>
    public static CarveResult NeedTotal(long required) => new(CarveStatus.NeedMore, 0, null, required);
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
