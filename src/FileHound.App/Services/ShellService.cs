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

    /// <summary>
    /// Full path of Explorer. Always launch it by this path: a bare "explorer.exe" is resolved through the CreateProcess
    /// search order (application directory, current directory, then System32), so a planted copy next to FileHound.exe
    /// would run — as administrator when Turbo is on.
    /// </summary>
    public static readonly string ExplorerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Opens a file or folder. When elevated, hands off to the (unelevated) desktop shell so the item doesn't inherit admin rights.</summary>
    public void Open(string path)
    {
        try
        {
            if (IsElevated)
                Process.Start(new ProcessStartInfo(ExplorerPath, Quote(path)) { UseShellExecute = false });
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
        Process.Start(new ProcessStartInfo(ExplorerPath, $"/select,{Quote(path)}") { UseShellExecute = false });
    }

    /// <summary>
    /// Shows the shell Properties sheet. When elevated it is shown by the desktop's Explorer, not in-process: the sheet's
    /// "Open with" / "Change…" buttons launch programs, and those must not inherit admin rights.
    /// </summary>
    public void ShowProperties(string path)
    {
        if (IsElevated)
        {
            try { DesktopShell.ShowProperties(path); return; }
            catch (InvalidOperationException ex)
            {
                Log.Warn($"Properties via desktop shell failed for {path}: {ex.InnerException?.Message ?? ex.Message}");
                Reveal(path);
                throw new InvalidOperationException("Properties isn't available while Turbo is on — shown in Explorer instead (press Alt+Enter there)", ex);
            }
        }
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
