using System.Windows;
using Wpf.Ui.Markup;

namespace WordCatcher.App.Themes;

internal static class WpfUiResourceScope
{
    public static void PreferApplicationResources(FrameworkElement element)
    {
        if (Application.Current is null)
            return;

        // Standalone layout tests keep local dictionaries; the app uses its single global theme.
        for (var i = element.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (element.Resources.MergedDictionaries[i] is ThemesDictionary or ControlsDictionary)
                element.Resources.MergedDictionaries.RemoveAt(i);
        }
    }
}
