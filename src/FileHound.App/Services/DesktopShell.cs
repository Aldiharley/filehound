using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace FileHound.App.Services;

/// <summary>
/// Reaches the Shell.Application object hosted by the user's own (unelevated) Explorer, so an elevated FileHound can ask
/// the desktop shell to do things at normal integrity instead of doing them itself as administrator.
/// Route: IShellWindows → desktop window → IShellBrowser → active IShellView → folder-view background → Application.
/// </summary>
internal static class DesktopShell
{
    private static readonly Guid s_clsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid s_sidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid s_iidShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid s_iidDispatch = new("00020400-0000-0000-C000-000000000046");
    private const int CSIDL_DESKTOP = 0;
    private const int SWC_DESKTOP = 8;
    private const int SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_BACKGROUND = 0;
    private const int ssfDRIVES = 0x11;

    /// <summary>Shows the shell Properties sheet for <paramref name="path"/> inside Explorer's process.</summary>
    /// <exception cref="InvalidOperationException">Explorer's shell could not be reached or does not know the item.</exception>
    public static void ShowProperties(string path)
    {
        try
        {
            dynamic app = GetApplication();
            string? dir = Path.GetDirectoryName(path);
            dynamic? folder = dir is null ? app.NameSpace(ssfDRIVES) : app.NameSpace(dir);
            dynamic? item = folder?.ParseName(dir is null ? path : Path.GetFileName(path));
            if (item is null) throw new InvalidOperationException($"Explorer doesn't know {path}");
            item.InvokeVerb("properties");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException or NullReferenceException)
        {
            throw new InvalidOperationException("Couldn't reach the desktop shell", ex);
        }
    }

    /// <summary>The IShellDispatch2 (Shell.Application) object living in the desktop's Explorer process.</summary>
    public static object GetApplication()
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(s_clsidShellWindows)!)!;
        object? loc = CSIDL_DESKTOP;
        object? root = null;
        Marshal.ThrowExceptionForHR(windows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH, out object desktop));
        var guidService = s_sidTopLevelBrowser;
        var iidBrowser = s_iidShellBrowser;
        Marshal.ThrowExceptionForHR(((IServiceProvider)desktop).QueryService(ref guidService, ref iidBrowser, out object browser));
        Marshal.ThrowExceptionForHR(((IShellBrowser)browser).QueryActiveShellView(out IShellView view));
        var iidDispatch = s_iidDispatch;
        Marshal.ThrowExceptionForHR(view.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch, out object background));
        dynamic folderView = background;
        return folderView.Application ?? throw new InvalidOperationException("Desktop view has no Application");
    }

    // Only the members FileHound calls have real signatures; the rest are vtable placeholders in declaration order.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, ref object? loc, ref object? locRoot, int swClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, ref object? loc);
        [PreserveSig] int OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [PreserveSig] int FindWindowSW(ref object? loc, ref object? locRoot, int swClass, out int hwnd, int options, [MarshalAs(UnmanagedType.IDispatch)] out object dispatch);
        [PreserveSig] int OnCreated(int cookie, [MarshalAs(UnmanagedType.IUnknown)] object unk);
        [PreserveSig] int ProcessAttachDetach([MarshalAs(UnmanagedType.VariantBool)] bool attach);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        // IShellBrowser
        [PreserveSig] int InsertMenusSB(nint hmenuShared, nint menuWidths);
        [PreserveSig] int SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(nint hmenuShared);
        [PreserveSig] int SetStatusTextSB(nint statusText);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int TranslateAcceleratorSB(nint msg, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
        [PreserveSig] int SendControlMsg(uint id, uint msg, nint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView(out IShellView view);
        [PreserveSig] int OnViewWindowActive(IShellView view);
        [PreserveSig] int SetToolbarItems(nint buttons, uint count, uint flags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out nint hwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        // IShellView
        [PreserveSig] int TranslateAccelerator(nint msg);
        [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)] bool enable);
        [PreserveSig] int UIActivate(uint state);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rect, out nint hwnd);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(nint settings);
        [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint lParam);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(nint pidl, uint flags);
        [PreserveSig] int GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }
}
