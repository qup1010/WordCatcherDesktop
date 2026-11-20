using System.Windows;

namespace WordCatcher.App.Themes;

public sealed class StandaloneDesignSystemDictionary : ResourceDictionary
{
    public StandaloneDesignSystemDictionary()
    {
        if (Application.Current is not { } application || !application.Dispatcher.CheckAccess()
            || !application.Resources.MergedDictionaries.Any(WpfUiResourceScope.IsDesignSystem))
            Source = new Uri("/WordCatcher.App;component/Themes/DesignSystem.xaml", UriKind.Relative);
    }
}
