using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WordCatcher.App.Services;

internal static class WindowBackdrop
{
    private const int SystemBackdropType = 38;
    private const int MainWindowBackdrop = 2;
    private const int ImmersiveDarkModeAttribute = 20;
    private const int WindowCornerPreferenceAttribute = 33;
    private const int WindowCornerRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    public static bool TryApply(Window window, double titleBarHeight, bool isDark = false)
    {
        SetDarkMode(window, isDark);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;
        var source = HwndSource.FromHwnd(handle);
        if (source?.CompositionTarget == null)
            return false;

        try
        {
            var corner = WindowCornerRound;
            _ = DwmSetWindowAttribute(handle, WindowCornerPreferenceAttribute, ref corner, sizeof(int));

            var backdrop = MainWindowBackdrop;
            if (DwmSetWindowAttribute(handle, SystemBackdropType, ref backdrop, sizeof(int)) < 0)
                return false;

            if (System.Windows.Shell.WindowChrome.GetWindowChrome(window) is { } chrome)
                chrome.GlassFrameThickness = new Thickness(-1);

            // Extend DWM Mica across the entire client area so the navigation rail
            // and title bar share a continuous translucent WinUI 3 backdrop.
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0)
                return false;

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    public static void SetDarkMode(Window window, bool isDark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        try
        {
            var darkValue = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, ImmersiveDarkModeAttribute, ref darkValue, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
