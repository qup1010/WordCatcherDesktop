using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WordCatcher.App.Themes;
using WordCatcher.App.ViewModels;

namespace WordCatcher.App.Views;

public partial class WordLookupView : System.Windows.Controls.UserControl
{
    public WordLookupView()
    {
        InitializeComponent();
        WpfUiResourceScope.PreferApplicationResources(this);
    }

    private void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is WordLookupViewModel)
        {
            QueryBox.Focus();
        }
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (DataContext is WordLookupViewModel viewModel && viewModel.SearchCommand.CanExecute(null))
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
