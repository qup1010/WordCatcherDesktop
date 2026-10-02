using System.Runtime.InteropServices;
using System.Text;

namespace WordCatcher.App.Interop;

public static partial class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern int CountClipboardFormats();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClipboardFormatName(uint format, StringBuilder name, int count);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern nuint GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr CopyImage(IntPtr handle, uint type, int width, int height, uint flags);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CopyEnhMetaFile(IntPtr handle, string? file);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeleteEnhMetaFile(IntPtr handle);
}
