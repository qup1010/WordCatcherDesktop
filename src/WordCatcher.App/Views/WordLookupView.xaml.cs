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
        UpdateResultColumns();
        if (DataContext is WordLookupViewModel)
        {
            FocusQuery();
        }
    }

    public void FocusQuery()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new System.Action(() =>
        {
            QueryBox.Focus();
            QueryBox.SelectAll();
        }));
    }

    private void ResultColumns_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateResultColumns();

    private void ContextPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateResultColumns();

    private void UpdateResultColumns()
    {
        if (ContextPanel == null || DefinitionPanel == null || SecondaryColumn == null) return;
        var hasContext = ContextPanel.Visibility == Visibility.Visible;
        var stacked = ResultColumns.ActualWidth < 680;
        SecondaryColumn.Width = hasContext && !stacked ? new GridLength(0.85, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ContextPanel, stacked ? 0 : 1);
        Grid.SetRow(ContextPanel, stacked ? 1 : 0);
        DefinitionPanel.Margin = new Thickness(0, 0, hasContext && !stacked ? 24 : 0, 0);
        ContextPanel.Margin = new Thickness(0, stacked ? 16 : 0, 0, 0);
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is WordLookupViewModel clearVm)
        {
            clearVm.ClearQueryCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter)
            return;

        if (DataContext is WordLookupViewModel viewModel && viewModel.SearchCommand.CanExecute(null))
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }
}
