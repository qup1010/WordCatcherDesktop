using System;
using System.Collections.Generic;
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
    private static readonly List<WeakReference<FrameworkElement>> RegisteredElements = new();

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

        var app = Application.Current;
        var canAccessApp = app == null || app.CheckAccess();
        if (canAccessApp)
        {
            try
            {
                ApplicationThemeManager.Apply(wpfUiTheme);
                ApplicationAccentColorManager.Apply(accent, wpfUiTheme, systemGlassColor: false, systemAccentColor: false);
            }
            catch (Exception)
            {
                // Unit tests without a full Application host may skip global WPF-UI theme registration.
            }
        }

        if (app != null && canAccessApp)
        {
            ApplyDictionaryTheme(app.Resources, isDark, ensureTopLevelTheme: true);
        }

        var targets = new HashSet<FrameworkElement>();
        if (app != null && canAccessApp)
        {
            foreach (Window window in app.Windows)
            {
                targets.Add(window);
            }
        }

        for (var i = RegisteredElements.Count - 1; i >= 0; i--)
        {
            if (RegisteredElements[i].TryGetTarget(out var element))
            {
                targets.Add(element);
            }
            else
            {
                RegisteredElements.RemoveAt(i);
            }
        }

        foreach (var target in targets)
        {
            if (!target.CheckAccess())
                continue;

            ApplyDictionaryTheme(target.Resources, isDark, ensureTopLevelTheme: true);
            if (target is Window window)
            {
                WindowBackdrop.SetDarkMode(window, isDark);
            }
            SyncElementSubtree(target, isDark);
        }
    }

    public static void SyncElementResources(FrameworkElement element)
    {
        RegisterElement(element);
        ApplyDictionaryTheme(element.Resources, IsDarkActive, ensureTopLevelTheme: true);
    }

    private static void RegisterElement(FrameworkElement element)
    {
        for (var i = RegisteredElements.Count - 1; i >= 0; i--)
        {
            if (!RegisteredElements[i].TryGetTarget(out var existing))
            {
                RegisteredElements.RemoveAt(i);
            }
            else if (ReferenceEquals(existing, element))
            {
                return;
            }
        }

        RegisteredElements.Add(new WeakReference<FrameworkElement>(element));
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

    private static void ApplyDictionaryTheme(ResourceDictionary dictionary, bool isDark, bool ensureTopLevelTheme = false)
    {
        if (dictionary == null) return;

        var targetTheme = isDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        var targetUri = isDark ? DarkThemeUri : LightThemeUri;
        var hasDirectThemeDictionary = false;
        for (var i = 0; i < dictionary.MergedDictionaries.Count; i++)
        {
            var merged = dictionary.MergedDictionaries[i];
            if (merged is Wpf.Ui.Markup.ThemesDictionary themesDict)
            {
                var themeFile = isDark ? "Dark.xaml" : "Light.xaml";
                if (themesDict.Source?.OriginalString?.EndsWith(themeFile, StringComparison.OrdinalIgnoreCase) != true)
                {
                    themesDict.Theme = targetTheme;
                }
            }
            else if (IsThemeDictionary(merged))
            {
                hasDirectThemeDictionary = true;
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

        if (ensureTopLevelTheme && !hasDirectThemeDictionary)
        {
            dictionary.MergedDictionaries.Add(new ResourceDictionary { Source = targetUri });
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
