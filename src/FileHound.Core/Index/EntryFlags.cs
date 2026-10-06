namespace FileHound.Core.Index;

/// <summary>Per-entry flags stored in <see cref="VolumeIndex"/>.</summary>
[Flags]
public enum EntryFlags : ushort
{
    None = 0,
    Directory = 1,
    Hidden = 2,
    System = 4,
    ReparsePoint = 8,
    Deleted = 16,
    /// <summary>Size and modified time are populated (always true for walked entries; set later for MFT entries).</summary>
    MetadataKnown = 32,
}

public enum IndexMode : byte
{
    /// <summary>Parallel directory walk + FileSystemWatcher.</summary>
    Standard = 0,
    /// <summary>NTFS MFT enumeration + USN journal (requires elevation).</summary>
    Turbo = 1,
}

public enum FileCategory : byte
{
    Other = 0,
    Folder,
    Document,
    Image,
    Video,
    Audio,
    Archive,
    App,
    Code,
}

public static class EntryFlagsExtensions
{
    /// <summary>Maps Win32 FILE_ATTRIBUTE_* bits to <see cref="EntryFlags"/>.</summary>
    public static EntryFlags FromAttributes(uint attributes)
    {
        var f = EntryFlags.None;
        if ((attributes & 0x10) != 0) f |= EntryFlags.Directory;
        if ((attributes & 0x02) != 0) f |= EntryFlags.Hidden;
        if ((attributes & 0x04) != 0) f |= EntryFlags.System;
        if ((attributes & 0x400) != 0) f |= EntryFlags.ReparsePoint;
        return f;
    }
}
