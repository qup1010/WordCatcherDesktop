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

    public static bool TryApply(Window window, double titleBarHeight)
    {
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
            var backdrop = MainWindowBackdrop;
            if (DwmSetWindowAttribute(handle, SystemBackdropType, ref backdrop, sizeof(int)) < 0)
                return false;

            var dpiScale = source.CompositionTarget.TransformToDevice.M22;
            var margins = new Margins { Top = (int)Math.Ceiling(titleBarHeight * dpiScale) };
            if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0)
                return false;

            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }
}
