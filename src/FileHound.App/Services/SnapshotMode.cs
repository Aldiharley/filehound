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
                var rec = vm.Recovery;
                async Task Settle(int ms = 800)
                {
                    await Task.Delay(ms);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                async Task WaitUntil(Func<bool> done, int timeoutMs = 60_000)
                {
                    for (int i = 0; i < timeoutMs / 200 && !done(); i++) await Task.Delay(200);
                }
                // Recycle Bin and the deletion log load on their own; re-save the first capture once they have.
                await WaitUntil(() => !rec.RecycleBin.IsLoading && !rec.Deleted.IsLoading);
                await Settle();
                Save(window, Path.Combine(outDir, "recovery.png"));
                rec.CurrentTab = ViewModels.Recovery.RecoveryTab.Deleted;
                await Settle(1200);
                Save(window, Path.Combine(outDir, "recovery-deleted.png"));

                // Undelete: run the scan when Turbo allows it, so the capture shows graded rows.
                rec.CurrentTab = ViewModels.Recovery.RecoveryTab.Undelete;
                if (rec.Undelete.IsAvailable)
                {
                    _ = rec.Undelete.ScanCommand.ExecuteAsync(null);
                    await WaitUntil(() => rec.Undelete.HasScanned || rec.Undelete.Error is not null);
                    if (rec.Undelete.Items.Count > 0) rec.Undelete.Items[0].IsSelected = true;
                }
                await Settle();
                Save(window, Path.Combine(outDir, "recovery-undelete.png"));

                rec.CurrentTab = ViewModels.Recovery.RecoveryTab.PreviousVersions;
                rec.Versions.Path = rec.SelectedDrive is { } d ? $@"{d.Letter}:\Documents" : @"C:\Windows\System32\drivers\etc\hosts";
                await Settle();
                Save(window, Path.Combine(outDir, "recovery-versions.png"));

                // Deep scan: the first Scan shows the consent card; after consent, the scan runs and the first image is previewed.
                rec.CurrentTab = ViewModels.Recovery.RecoveryTab.DeepScan;
                if (rec.DeepScan.IsAvailable)
                {
                    await rec.DeepScan.ScanCommand.ExecuteAsync(null);
                    await Settle();
                    Save(window, Path.Combine(outDir, "recovery-deepscan-consent.png"));
                    if (rec.DeepScan.NeedsConsent) _ = rec.DeepScan.ConsentCommand.ExecuteAsync(null);
                    await WaitUntil(() => rec.DeepScan.HasScanned || rec.DeepScan.Error is not null, 300_000);
                    var image = rec.DeepScan.Items.FirstOrDefault(i => i.Candidate.Key is FileHound.Indexing.Recovery.CarvedFile f && f.Type.Category == FileHound.Core.Index.FileCategory.Image);
                    if (image is not null) { rec.DeepScan.PreviewItem = image; image.IsSelected = true; }
                    await Settle(1500);
                    Save(window, Path.Combine(outDir, "recovery-deepscan.png"));
                }
                else
                {
                    await Settle();
                    Save(window, Path.Combine(outDir, "recovery-deepscan.png"));
                }
                rec.CurrentTab = ViewModels.Recovery.RecoveryTab.RecycleBin;
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
