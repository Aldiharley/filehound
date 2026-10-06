using System.Windows.Controls;
using System.Windows.Input;
using FileHound.App.ViewModels;

namespace FileHound.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.IsCapturing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { vm.CancelCapture(); e.Handled = true; return; }
        if (vm.TryCaptureHotkey(Keyboard.Modifiers, key)) e.Handled = true;
        else e.Handled = !HotkeyGestureIsNavigation(key);
    }

    private static bool HotkeyGestureIsNavigation(Key key) => key is Key.Tab;
}
