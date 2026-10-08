using System.Windows.Controls;
using System.Windows.Input;
using FileHound.App.ViewModels.Recovery;

namespace FileHound.App.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RecoveryViewModel vm) vm.Versions.ConfirmFreeze = ConfirmFreeze;
        };
    }

    private RecoveryViewModel Vm => (RecoveryViewModel)DataContext;

    /// <summary>FR-20: the one write this tab can make needs an explicit yes; Cancel is the default button.</summary>
    private bool ConfirmFreeze()
    {
        char letter = Vm.SelectedDrive?.Letter ?? 'C';
        var result = System.Windows.MessageBox.Show(System.Windows.Window.GetWindow(this)!,
            $"This creates a snapshot of {letter}:. It writes a small amount of data to the drive, which could overwrite deleted files you haven't recovered yet.\n\nContinue?",
            $"Freeze {letter}: now?", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel);
        return result == System.Windows.MessageBoxResult.OK;
    }

    private void VersionsPath_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = Vm.Versions.LookupCommand.ExecuteAsync(null); e.Handled = true; }
        else if (e.Key == Key.Escape && sender is TextBox box) { box.Text = ""; e.Handled = true; }
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        switch (e.Key)
        {
            case Key.Space when list.SelectedItems.Count > 0:
                // Space toggles the checkbox of every highlighted row (Explorer-like multi-select + check).
                bool any = list.SelectedItems.OfType<RecoveryItem>().Any(i => !i.IsSelected);
                foreach (var it in list.SelectedItems.OfType<RecoveryItem>()) it.IsSelected = any;
                e.Handled = true;
                break;
            case Key.Enter when Vm.RecoverCommand.CanExecute(null):
                Vm.RecoverCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                foreach (var it in list.Items.OfType<RecoveryItem>()) it.IsSelected = false;
                e.Handled = true;
                break;
        }
    }

    private void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box) return;
        box.Text = "";
        e.Handled = true;
    }
}
