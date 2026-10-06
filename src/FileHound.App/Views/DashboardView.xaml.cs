using System.Windows.Controls;
using System.Windows.Input;
using FileHound.App.ViewModels;

namespace FileHound.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    private void HeroBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is DashboardViewModel vm)
        {
            vm.SearchCommand.Execute(HeroBox.Text);
            e.Handled = true;
        }
    }
}
