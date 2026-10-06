using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileHound.App.Services;
using FileHound.App.ViewModels;

namespace FileHound.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.FocusSearchRequested += (_, _) => Dispatcher.InvokeAsync(() => FocusSearch(selectAll: false), DispatcherPriority.Input);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentView)) FadeInPage();
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
                HeaderSearch.Visibility = vm.CurrentPage == AppPage.Search ? Visibility.Collapsed : Visibility.Visible;
        };
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        StateChanged += (_, _) => OnWindowStateChanged();
    }

    /// <summary>Moves keyboard focus into the Search page's box (if that page is showing).</summary>
    public void FocusSearch(bool selectAll)
    {
        if (FindChild<SearchView>(PageHost) is { } view) view.FocusBox(selectAll);
        else Dispatcher.InvokeAsync(() => FindChild<SearchView>(PageHost)?.FocusBox(selectAll), DispatcherPriority.Loaded);
    }

    public void FocusSearchIfOnSearchPage()
    {
        if (_vm.CurrentPage == AppPage.Search) FocusSearch(selectAll: true);
    }

    private void FadeInPage()
    {
        PageHost.Opacity = 0;
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private unsafe void ApplyRoundedCorners()
    {
        int pref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, &pref, sizeof(int));
    }

    private void OnWindowStateChanged()
    {
        // A maximised WindowChrome window overhangs the screen by the resize border; pad it back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    internal static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
