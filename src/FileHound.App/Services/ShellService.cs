using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace FileHound.App.Services;

/// <summary>Opens, reveals, copies and shows properties for files via the Windows shell.</summary>
public sealed class ShellService
{
    public bool IsElevated { get; } = Elevation.IsElevated;

    /// <summary>Opens a file or folder. When elevated, hands off to the (unelevated) desktop shell so the item doesn't inherit admin rights.</summary>
    public void Open(string path)
    {
        try
        {
            if (IsElevated)
                Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Open failed for {path}: {ex.Message}");
            throw new InvalidOperationException($"Couldn't open {Path.GetFileName(path)}", ex);
        }
    }

    /// <summary>Opens Explorer on the containing folder with the item selected.</summary>
    public unsafe void Reveal(string path)
    {
        nint pidl = NativeMethods.ILCreateFromPathW(path);
        if (pidl != 0)
        {
            try
            {
                if (NativeMethods.SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
            }
            finally { NativeMethods.ILFree(pidl); }
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,{Quote(path)}") { UseShellExecute = false });
    }

    public void ShowProperties(string path)
    {
        var info = new NativeMethods.SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFOW>(),
            fMask = NativeMethods.SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        if (!NativeMethods.ShellExecuteExW(ref info)) Log.Warn($"Properties failed for {path}: {Marshal.GetLastPInvokeError()}");
    }

    public static void CopyText(string text) => Retry(() => Clipboard.SetText(text));

    public static void CopyFile(string path) => Retry(() => Clipboard.SetFileDropList(new StringCollection { path }));

    /// <summary>Starts an OLE drag of the file. Disabled while elevated (UIPI blocks drops into normal-integrity apps).</summary>
    public bool TryStartDrag(DependencyObject source, string path)
    {
        if (IsElevated || !(File.Exists(path) || Directory.Exists(path))) return false;
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move);
        return true;
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private static void Retry(Action a)
    {
        // The clipboard can be briefly locked by other apps.
        for (int i = 0; i < 5; i++)
        {
            try { a(); return; }
            catch (COMException) { Thread.Sleep(30); }
        }
    }
}
