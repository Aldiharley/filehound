using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileHound.App.ViewModels;

namespace FileHound.App.Services;

/// <summary>
/// Dev/QA mode (<c>--snapshot &lt;dir&gt; [--query text]</c>): renders every page to PNG in-process with
/// RenderTargetBitmap, then exits. Works even when the desktop can't be screen-captured.
/// </summary>
public static class SnapshotMode
{
    public static async Task RunAsync(Window window, MainViewModel vm, string outDir, string query, TimeSpan warmup)
    {
        Directory.CreateDirectory(outDir);
        await Task.Delay(warmup);
        foreach (var page in new[] { AppPage.Dashboard, AppPage.Search, AppPage.Drives, AppPage.Recovery, AppPage.Settings })
        {
            vm.CurrentPage = page;
            if (page == AppPage.Search)
            {
                vm.Search.QueryText = query;
                await Task.Delay(2500);
            }
            await Task.Delay(1200);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(window, Path.Combine(outDir, $"{page.ToString().ToLowerInvariant()}.png"));
            if (page == AppPage.Recovery)
            {
                vm.Recovery.CurrentTab = ViewModels.Recovery.RecoveryTab.Deleted;
                await Task.Delay(1200);
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Save(window, Path.Combine(outDir, "recovery-deleted.png"));
                vm.Recovery.CurrentTab = ViewModels.Recovery.RecoveryTab.RecycleBin;
            }
        }
        vm.CurrentPage = AppPage.Search;
        vm.Search.QueryText = "";
        await Task.Delay(1200);
        Save(window, Path.Combine(outDir, "search-empty.png"));
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        double dpi = VisualTreeHelper.GetDpi(window).PixelsPerInchX;
        int w = (int)Math.Ceiling(root.ActualWidth * dpi / 96), h = (int)Math.Ceiling(root.ActualHeight * dpi / 96);
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        rtb.Render(bg);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log.Info($"Snapshot saved: {path}");
    }
}
