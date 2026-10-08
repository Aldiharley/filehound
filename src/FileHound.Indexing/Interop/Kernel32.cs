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
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;
    public const int ERROR_MORE_DATA = 234;

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, byte* buffer, int bytesToRead, out int bytesRead, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFilePointerEx(SafeFileHandle file, long distance, out long newPosition, uint moveMethod);

    /// <summary>Reads exactly <paramref name="length"/> bytes at an absolute volume offset (throws on failure).</summary>
    public static void ReadExactly(SafeFileHandle volume, long offset, byte* buffer, int length)
    {
        if (!SetFilePointerEx(volume, offset, out _, 0 /* FILE_BEGIN */))
        {
            int seekErr = Marshal.GetLastPInvokeError();
            throw new IOException($"Seek to {offset} failed: Win32 error {seekErr} ({new System.ComponentModel.Win32Exception(seekErr).Message})");
        }
        int done = 0;
        while (done < length)
        {
            if (!ReadFile(volume, buffer + done, length - done, out int read, 0))
            {
                int err = Marshal.GetLastPInvokeError();
                throw new IOException($"Volume read of {length - done} bytes at {offset + done} failed: Win32 error {err} ({new System.ComponentModel.Win32Exception(err).Message})");
            }
            if (read == 0) throw new IOException($"Unexpected end of volume at {offset + done}");
            done += read;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volumeName, int length);

    /// <summary>The <c>\\?\Volume{GUID}\</c> name of the volume holding <paramref name="path"/>, or null.</summary>
    public static string? VolumeGuidPathOf(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return null;
        if (!root.EndsWith('\\')) root += '\\';
        char* buf = stackalloc char[64];
        return GetVolumeNameForVolumeMountPoint(root, buf, 64) ? new string(buf) : null;
    }

    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_DELETE = 0x4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, byte* info);

    /// <summary>NTFS MFT record number of a file or folder (low 48 bits of its file reference number).</summary>
    public static bool TryGetRecordNumber(string path, out long recordNo)
    {
        recordNo = -1;
        using var h = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) return false;
        byte* info = stackalloc byte[52]; // BY_HANDLE_FILE_INFORMATION
        if (!GetFileInformationByHandle(h, info)) return false;
        ulong frn = ((ulong)*(uint*)(info + 44) << 32) | *(uint*)(info + 48);
        recordNo = (long)(frn & 0x0000_FFFF_FFFF_FFFF);
        return true;
    }

    /// <summary>
    /// Volume serial number of the volume that actually holds <paramref name="path"/>: the handle follows junctions,
    /// symlinks and folder mount points, so this cannot be fooled the way a drive-letter comparison can.
    /// </summary>
    public static bool TryGetVolumeSerial(string path, out uint serial)
    {
        serial = 0;
        using var h = CreateFile(path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) return false;
        byte* info = stackalloc byte[52]; // BY_HANDLE_FILE_INFORMATION
        if (!GetFileInformationByHandle(h, info)) return false;
        serial = *(uint*)(info + 28); // dwVolumeSerialNumber
        return true;
    }

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
