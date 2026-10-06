using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileHound.App.Services;

/// <summary>
/// Shell icons for result rows (32 px "large" icons, displayed at 24 px for crispness on high DPI). Most files share one icon per extension (cached forever);
/// executables, shortcuts and icon files get per-path icons (LRU-ish bounded cache).
/// </summary>
public sealed class ShellIconProvider
{
    private static readonly HashSet<string> s_perFile = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".ico", ".url", ".appref-ms", ".msc", ".scr", ".cpl" };
    private readonly ConcurrentDictionary<string, ImageSource?> _byExt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private ImageSource? _folder;

    public ImageSource? Get(string path, bool isDirectory)
    {
        if (isDirectory) return _folder ??= Load("folder", NativeMethods.FILE_ATTRIBUTE_DIRECTORY, useAttributes: true);
        string ext = Path.GetExtension(path);
        if (s_perFile.Contains(ext))
        {
            if (_byPath.Count > 1024) _byPath.Clear();
            return _byPath.GetOrAdd(path, p => Load(p, NativeMethods.FILE_ATTRIBUTE_NORMAL, useAttributes: false) ?? GetByExtension(ext));
        }
        return GetByExtension(ext);
    }

    private ImageSource? GetByExtension(string ext) =>
        _byExt.GetOrAdd(ext.Length == 0 ? "." : ext, e => Load("x" + e, NativeMethods.FILE_ATTRIBUTE_NORMAL, useAttributes: true));

    private static ImageSource? Load(string path, uint attributes, bool useAttributes)
    {
        var info = new NativeMethods.SHFILEINFOW();
        uint flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | (useAttributes ? NativeMethods.SHGFI_USEFILEATTRIBUTES : 0);
        nint ok = NativeMethods.SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(), flags);
        if (ok == 0 || info.hIcon == 0) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (COMException) { return null; }
        finally { NativeMethods.DestroyIcon(info.hIcon); }
    }
}
