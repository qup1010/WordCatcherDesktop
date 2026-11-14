using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace WordCatcher.App.Services;

public static class ThemeManager
{
    public const string LightMode = "Light";
    public const string DarkMode = "Dark";
    public const string SystemMode = "System";

    private static readonly Uri LightThemeUri = new("/WordCatcher.App;component/Themes/LightTheme.xaml", UriKind.Relative);
    private static readonly Uri DarkThemeUri = new("/WordCatcher.App;component/Themes/DarkTheme.xaml", UriKind.Relative);

    public static bool IsDarkActive { get; private set; }

    public static string NormalizeMode(string? mode) => mode?.Trim() switch
    {
        var m when string.Equals(m, DarkMode, StringComparison.OrdinalIgnoreCase) => DarkMode,
        var m when string.Equals(m, SystemMode, StringComparison.OrdinalIgnoreCase) => SystemMode,
        _ => LightMode
    };

    public static bool ResolveIsDark(string? themeMode) => NormalizeMode(themeMode) switch
    {
        DarkMode => true,
        SystemMode => IsSystemDarkMode(),
        _ => false
    };

    public static void ApplyTheme(string? themeMode)
    {
        var isDark = ResolveIsDark(themeMode);
        IsDarkActive = isDark;

        var accent = isDark
            ? System.Windows.Media.Color.FromRgb(45, 212, 191)
            : System.Windows.Media.Color.FromRgb(15, 118, 110);
        var wpfUiTheme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;

        try
        {
            ApplicationThemeManager.Apply(wpfUiTheme);
            ApplicationAccentColorManager.Apply(accent, wpfUiTheme, systemGlassColor: false, systemAccentColor: false);
        }
        catch (Exception)
        {
            // Unit tests without a full Application host may skip global WPF-UI theme registration.
        }

        var app = Application.Current;
        if (app == null) return;

        ApplyDictionaryTheme(app.Resources, isDark);

        foreach (Window window in app.Windows)
        {
            ApplyDictionaryTheme(window.Resources, isDark);
            WindowBackdrop.SetDarkMode(window, isDark);
            SyncElementSubtree(window, isDark);
        }
    }

    public static void SyncElementResources(FrameworkElement element)
    {
        ApplyDictionaryTheme(element.Resources, IsDarkActive);
    }

    private static void SyncElementSubtree(DependencyObject root, bool isDark)
    {
        if (root is FrameworkElement fe && fe is not Window)
            ApplyDictionaryTheme(fe.Resources, isDark);

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            SyncElementSubtree(VisualTreeHelper.GetChild(root, i), isDark);
        }
    }

    private static void ApplyDictionaryTheme(ResourceDictionary dictionary, bool isDark)
    {
        if (dictionary == null) return;

        var targetUri = isDark ? DarkThemeUri : LightThemeUri;
        for (var i = 0; i < dictionary.MergedDictionaries.Count; i++)
        {
            var merged = dictionary.MergedDictionaries[i];
            if (IsThemeDictionary(merged))
            {
                if (!string.Equals(merged.Source?.OriginalString, targetUri.OriginalString, StringComparison.OrdinalIgnoreCase))
                {
                    dictionary.MergedDictionaries[i] = new ResourceDictionary { Source = targetUri };
                }
            }
            else
            {
                ApplyDictionaryTheme(merged, isDark);
            }
        }
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        if (string.IsNullOrEmpty(source)) return false;
        return source.EndsWith("Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value)
                return value == 0;
        }
        catch (Exception) { }
        return false;
    }
}
