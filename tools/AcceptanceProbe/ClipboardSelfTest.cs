using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using WordCatcher.App.Interop;
using WordCatcher.App.Services;
using WordCatcher.Core.Models;

// 专用子进程在独立窗口站中测试，绝不改写交互桌面的用户剪贴板。
internal static class ClipboardSelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowStation(string? name, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr settings, uint flags, uint access, IntPtr security);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    internal static void Run()
    {
        // 必须先隔离，失败则停止；不回退到当前交互窗口站。
        var station = CreateWindowStation(null, 0, 0x37f, IntPtr.Zero);
        Check(station != IntPtr.Zero && SetProcessWindowStation(station), "isolated window station");
        var desktop = CreateDesktop("ClipboardTest", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
        Check(desktop != IntPtr.Zero, "create isolated desktop");
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Check(SetThreadDesktop(desktop), "isolated desktop");
                RunChecks();
            }
            catch (Exception ex) { failure = ex; }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        worker.Join();
        if (failure != null) throw new InvalidOperationException("Isolated clipboard test failed.", failure);
    }

    private static void RunChecks()
    {
        using var owner = new HwndSource(new HwndSourceParameters("Clipboard self test")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });

        var dib = new byte[44];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(1).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        new byte[] { 12, 34, 56, 255 }.CopyTo(dib, 40);
        var files = new byte[20].Concat(Encoding.Unicode.GetBytes("C:\\acceptance.txt\0\0")).ToArray();
        BitConverter.GetBytes(20).CopyTo(files, 0);
        BitConverter.GetBytes(1).CopyTo(files, 16);
        var formats = new Dictionary<uint, byte[]>
        {
            [13] = Encoding.Unicode.GetBytes("original\0"),
            [RegisterClipboardFormat("HTML Format")] = Encoding.UTF8.GetBytes("<b>test</b>\0"),
            [RegisterClipboardFormat("Rich Text Format")] = Encoding.ASCII.GetBytes("{\\rtf1 test}\0"),
            [15] = files,
            [8] = dib,
            [RegisterClipboardFormat("WordCatcher.Native.Binary.Test")] = new byte[] { 0, 255, 1, 8, 0, 22 }
        };

        Write(owner.Handle, formats);
        using (var backup = NativeClipboardBackup.Capture())
        {
            Write(owner.Handle, Text("captured"));
            Check(backup.Restore(NativeMethods.GetClipboardSequenceNumber()), "restore mixed formats");
            foreach (var pair in formats) Check(ReadBytes(pair.Key, pair.Value.Length).SequenceEqual(pair.Value), $"format {pair.Key}");
        }
        Console.WriteLine("PASS: Unicode, HTML, RTF, file drop, DIB and custom binary formats");

        Write(owner.Handle, []);
        using (var backup = NativeClipboardBackup.Capture())
        {
            Write(owner.Handle, Text("captured"));
            Check(backup.Restore(NativeMethods.GetClipboardSequenceNumber()), "empty restore");
            Check(NativeMethods.CountClipboardFormats() == 0, "clipboard remains empty");
        }
        Console.WriteLine("PASS: originally empty clipboard");

        Write(owner.Handle, Text("original"));
        using (var backup = NativeClipboardBackup.Capture())
        {
            Write(owner.Handle, Text("captured"));
            var capturedSequence = NativeMethods.GetClipboardSequenceNumber();
            Write(owner.Handle, Text("new copy"));
            Check(!backup.Restore(capturedSequence), "skip newer copy");
            Check(ReadBytes(13, Encoding.Unicode.GetByteCount("new copy\0")).SequenceEqual(Encoding.Unicode.GetBytes("new copy\0")), "preserve newer copy");
        }
        Console.WriteLine("PASS: newer clipboard contents are preserved");

        Write(owner.Handle, Text("captured"));
        var source = new CaptureSource(owner.Handle, (uint)Environment.ProcessId, "probe", "probe", new(0, 0));
        var copied = NativeClipboardBackup.Read(source, NativeMethods.GetClipboardSequenceNumber());
        Check(copied.Text == "captured", "trusted clipboard owner");
        try
        {
            NativeClipboardBackup.Read(source with { ProcessId = 0 }, NativeMethods.GetClipboardSequenceNumber());
            throw new InvalidOperationException("Unrelated owner was accepted.");
        }
        catch (CaptureException ex) when (ex.Failure == CaptureFailure.ClipboardChanged) { }
        var previousSequence = NativeMethods.GetClipboardSequenceNumber();
        Write(owner.Handle, Text("new copy"));
        try
        {
            NativeClipboardBackup.Read(source, previousSequence);
            throw new InvalidOperationException("Changed clipboard sequence was accepted.");
        }
        catch (CaptureException ex) when (ex.Failure == CaptureFailure.ClipboardChanged) { }
        Console.WriteLine("PASS: owner and clipboard sequence validation");

        Write(owner.Handle, new Dictionary<uint, byte[]> { [0x200] = new byte[] { 1, 2, 3 } });
        try
        {
            using var unexpected = NativeClipboardBackup.Capture();
            throw new InvalidOperationException("Unsupported private format was accepted.");
        }
        catch (CaptureException ex) when (ex.Failure == CaptureFailure.UnsupportedClipboard) { }
        Check(ReadBytes(0x200, 3).SequenceEqual(new byte[] { 1, 2, 3 }), "unsupported format unchanged");
        Console.WriteLine("PASS: unsupported format aborts without modifying data");
        Write(owner.Handle, []);
        Console.WriteLine("Clipboard self-test completed in an isolated window station.");
    }

    private static Dictionary<uint, byte[]> Text(string text) => new() { [13] = Encoding.Unicode.GetBytes(text + '\0') };
    private static void Write(IntPtr owner, Dictionary<uint, byte[]> formats)
    {
        NativeClipboardBackup.Open(owner);
        try
        {
            Check(NativeMethods.EmptyClipboard(), "empty clipboard");
            foreach (var pair in formats)
            {
                var handle = NativeMethods.GlobalAlloc(2, (nuint)pair.Value.Length);
                var pointer = NativeMethods.GlobalLock(handle);
                Check(pointer != IntPtr.Zero, "allocate fixture");
                Marshal.Copy(pair.Value, 0, pointer, pair.Value.Length);
                NativeMethods.GlobalUnlock(handle);
                Check(NativeMethods.SetClipboardData(pair.Key, handle) != IntPtr.Zero, "set fixture");
            }
        }
        finally { NativeMethods.CloseClipboard(); }
    }
    private static byte[] ReadBytes(uint format, int length)
    {
        NativeClipboardBackup.Open(IntPtr.Zero);
        try
        {
            var handle = NativeMethods.GetClipboardData(format);
            var pointer = NativeMethods.GlobalLock(handle);
            Check(pointer != IntPtr.Zero, "read restored format");
            try { var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length); return bytes; }
            finally { NativeMethods.GlobalUnlock(handle); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Failed: {name}; error={Marshal.GetLastWin32Error()}");
    }
}
