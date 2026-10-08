using System.Windows.Controls;
using System.Windows.Input;
using FileHound.App.ViewModels.Recovery;

namespace FileHound.App.Views;

public partial class RecoveryView : UserControl
{
    public RecoveryView() => InitializeComponent();

    private RecoveryViewModel Vm => (RecoveryViewModel)DataContext;

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
