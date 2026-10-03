using System.Windows;
using Wpf.Ui.Markup;

namespace WordCatcher.App.Themes;

internal static class WpfUiResourceScope
{
    public static void PreferApplicationResources(FrameworkElement element)
    {
        if (Application.Current is not { } application
            || !application.Resources.MergedDictionaries.Any(IsDesignSystem))
            return;

        // 保留 DesignSystem：其中的原生模板会在显示时延迟解析 StaticResource。
        // InitializeComponent 后移除该字典会使模板的字号等资源失效。
        // 这里只移除直接合并的原生字典，避免重复的全局主题覆盖。
        for (var i = element.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (element.Resources.MergedDictionaries[i] is ThemesDictionary or ControlsDictionary)
                element.Resources.MergedDictionaries.RemoveAt(i);
        }
    }

    private static bool IsDesignSystem(ResourceDictionary dictionary)
        => dictionary.Source?.OriginalString.EndsWith("Themes/DesignSystem.xaml", StringComparison.OrdinalIgnoreCase) == true;
}
