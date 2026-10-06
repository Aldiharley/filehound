using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileHound.Indexing.Interop;

internal static unsafe partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;

    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_INVALID_FUNCTION = 1;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize,
        void* outBuffer, int outBufferSize, out int bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string rootPathName, char* volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, char* fileSystemNameBuffer, int fileSystemNameSize);

    /// <summary>Opens a volume handle such as <c>\\.\C:</c> (requires elevation).</summary>
    public static SafeFileHandle OpenVolume(char letter) =>
        CreateFile($@"\\.\{char.ToUpperInvariant(letter)}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
}

[StructLayout(LayoutKind.Sequential)]
internal struct UsnJournalDataV1
{
    public ulong UsnJournalID;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
    public ushort MinSupportedMajorVersion;
    public ushort MaxSupportedMajorVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftEnumDataV1
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
    public ushort MinMajorVersion;
    public ushort MaxMajorVersion;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ReadUsnJournalDataV1
{
    public long StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalID;
    public ushort MinMajorVersion;
    public ushort MaxMajorVersion;
}

/// <summary>USN_REASON_* flags.</summary>
[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    Close = 0x80000000,
    DataChanges = DataOverwrite | DataExtend | DataTruncation,
}
