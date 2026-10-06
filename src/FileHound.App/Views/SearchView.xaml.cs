using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileHound.App.ViewModels;

namespace FileHound.App.Views;

public partial class SearchView : UserControl
{
    private Point? _dragStart;
    private ResultItem? _dragItem;

    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) => FocusBox(selectAll: false);
        PreviewKeyDown += View_PreviewKeyDown;
    }

    private SearchViewModel Vm => (SearchViewModel)DataContext;

    public void FocusBox(bool selectAll)
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        if (selectAll) SearchBox.SelectAll();
        else SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+1..9 picks a category chip.
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D9)
        {
            Vm.SelectChip(e.Key - Key.D1);
            e.Handled = true;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Results.Items.Count > 0:
                FocusResult(Results.SelectedIndex < 0 ? 0 : Results.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.Modifiers == ModifierKeys.Control) Vm.RevealCommand.Execute(null);
                else Vm.OpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                if (SearchBox.Text.Length > 0) Vm.QueryText = "";
                else Window.GetWindow(this)?.Hide();
                e.Handled = true;
                break;
        }
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Enter && mods == ModifierKeys.Alt) { Vm.PropertiesCommand.Execute(null); e.Handled = true; }
        else if (key == Key.Enter && mods == ModifierKeys.Control) { Vm.RevealCommand.Execute(null); e.Handled = true; }
        else if (key == Key.Enter) { Vm.OpenCommand.Execute(null); e.Handled = true; }
        else if (key == Key.C && mods == (ModifierKeys.Control | ModifierKeys.Shift)) { Vm.CopyPathCommand.Execute(null); e.Handled = true; }
        else if (key == Key.C && mods == ModifierKeys.Control) { Vm.CopyFileCommand.Execute(null); e.Handled = true; }
        else if (key == Key.Up && Results.SelectedIndex <= 0) { FocusBox(selectAll: false); e.Handled = true; }
        else if (key == Key.Escape) { FocusBox(selectAll: true); e.Handled = true; }
        else if (key is >= Key.A and <= Key.Z && mods == ModifierKeys.None)
        {
            // Typing while in the list continues the query.
            FocusBox(selectAll: false);
        }
    }

    private void FocusResult(int index)
    {
        Results.SelectedIndex = index;
        Results.ScrollIntoView(Results.SelectedItem);
        Results.UpdateLayout();
        if (Results.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemFromSource(e.OriginalSource) is not null) Vm.OpenCommand.Execute(null);
    }

    private void Results_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = ItemFromSource(e.OriginalSource);
        _dragStart = _dragItem is null ? null : e.GetPosition(Results);
    }

    private void Results_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Results);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragStart = null;
        _dragItem = null;
        Vm.TryStartDrag(Results, item);
    }

    private ResultItem? ItemFromSource(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not ListBoxItem) d = System.Windows.Media.VisualTreeHelper.GetParent(d is System.Windows.Documents.Run r ? r.Parent : d);
        return (d as ListBoxItem)?.DataContext as ResultItem;
    }
}
