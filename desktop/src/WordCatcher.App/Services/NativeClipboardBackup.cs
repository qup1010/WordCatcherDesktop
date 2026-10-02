using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using WordCatcher.App.Interop;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

// 复制实际原生格式，恢复时不借助会丢失自定义格式的文本/图片重建。
internal sealed class NativeClipboardBackup : IClipboardBackup
{
    private readonly List<Entry> _entries = [];
    public uint Sequence { get; private set; }
    private const int MaxBytes = 64 * 1024 * 1024;

    internal static NativeClipboardBackup Capture()
    {
        var snapshot = new NativeClipboardBackup();
        Open(IntPtr.Zero);
        try
        {
            uint format = 0;
            long bytes = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (snapshot._entries.Count >= 256) throw Unsupported();
                var name = new StringBuilder(256);
                NativeMethods.GetClipboardFormatName(format, name, name.Capacity);
                // 这些格式需要原应用的对象/回调或特殊句柄语义，无法独立完整备份。
                if (format is 3 or 9 or 0x80 or 0x83 || format is >= 0x200 and <= 0x3ff
                    || name.ToString() is "Ole Private Data" or "DataObject" or "Link Source"
                        or "Embed Source" or "Embedded Object" or "ObjectLink" or "OwnerLink" or "FileContents")
                    throw Unsupported();
                var original = NativeMethods.GetClipboardData(format);
                if (original == IntPtr.Zero) throw Unsupported();
                IntPtr copy;
                if (format is 2 or 0x82)
                    copy = NativeMethods.CopyImage(original, 0, 0, 0, 0x2000); // LR_CREATEDIBSECTION
                else if (format is 14 or 0x8e)
                    copy = NativeMethods.CopyEnhMetaFile(original, null);
                else
                {
                    var size = NativeMethods.GlobalSize(original);
                    if (size == 0 || size > MaxBytes || (bytes += (long)size) > MaxBytes) throw Unsupported();
                    copy = CloneMemory(original, (int)size);
                }
                if (copy == IntPtr.Zero) throw Unsupported();
                snapshot._entries.Add(new Entry(format, copy));
            }
            if (NativeMethods.CountClipboardFormats() != snapshot._entries.Count) throw Unsupported();
            snapshot.Sequence = NativeMethods.GetClipboardSequenceNumber();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
        finally { NativeMethods.CloseClipboard(); }
    }

    internal static ClipboardRead Read(CaptureSource source, uint expectedSequence)
    {
        Open(IntPtr.Zero);
        try
        {
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) throw Changed();
            var owner = NativeMethods.GetClipboardOwner();
            NativeMethods.GetWindowThreadProcessId(owner, out var ownerProcess);
            if (owner == IntPtr.Zero || ownerProcess != source.ProcessId) throw Changed();
            var memory = NativeMethods.GetClipboardData(13); // CF_UNICODETEXT（Windows 可按需转换 ANSI）
            string? text = null;
            if (memory != IntPtr.Zero)
            {
                var size = NativeMethods.GlobalSize(memory);
                if (size > 1024 * 1024)
                    return new ClipboardRead(null, NativeMethods.GetClipboardSequenceNumber(), CaptureFailure.SelectionTooLong);
                var pointer = NativeMethods.GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                    return new ClipboardRead(null, NativeMethods.GetClipboardSequenceNumber(), CaptureFailure.ClipboardBusy);
                try
                {
                    text = Marshal.PtrToStringUni(pointer, Math.Min((int)size / 2, 5001))?.Split('\0')[0];
                }
                finally { NativeMethods.GlobalUnlock(memory); }
            }
            return new ClipboardRead(text, NativeMethods.GetClipboardSequenceNumber());
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    internal bool Restore(uint expectedSequence)
    {
        // 必须提供有效 owner，OpenClipboard(NULL) 后 EmptyClipboard 会使 SetClipboardData 失败。
        using var owner = new HwndSource(new HwndSourceParameters("WordCatcher clipboard restore")
        {
            ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0
        });
        Open(owner.Handle);
        try
        {
            // 比较和写入都在同一次剪贴板锁内，阻止检查后用户复制再被覆盖的竞争。
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence) return false;
            if (!NativeMethods.EmptyClipboard()) throw new CaptureException(CaptureFailure.ClipboardBusy);
            foreach (var entry in _entries)
            {
                if (NativeMethods.SetClipboardData(entry.Format, entry.Handle) == IntPtr.Zero)
                    throw new CaptureException(CaptureFailure.ClipboardBusy);
                entry.Handle = IntPtr.Zero; // 已移交给 Windows，不再释放。
            }
            return true; // 空快照也执行 EmptyClipboard，恢复真正的空剪贴板。
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    internal static void Open(IntPtr owner)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner)) return;
            Thread.Sleep(25);
        }
        throw new CaptureException(CaptureFailure.ClipboardBusy);
    }

    private static IntPtr CloneMemory(IntPtr original, int length)
    {
        var source = NativeMethods.GlobalLock(original);
        if (source == IntPtr.Zero) throw Unsupported();
        IntPtr copy = IntPtr.Zero;
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(source, bytes, 0, length);
            copy = NativeMethods.GlobalAlloc(2, (nuint)length); // GMEM_MOVEABLE
            var target = NativeMethods.GlobalLock(copy);
            if (target == IntPtr.Zero) throw Unsupported();
            try { Marshal.Copy(bytes, 0, target, length); }
            finally { NativeMethods.GlobalUnlock(copy); }
            return copy;
        }
        catch { if (copy != IntPtr.Zero) NativeMethods.GlobalFree(copy); throw; }
        finally { NativeMethods.GlobalUnlock(original); }
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
        {
            if (entry.Handle == IntPtr.Zero) continue;
            if (entry.Format is 2 or 0x82) NativeMethods.DeleteObject(entry.Handle);
            else if (entry.Format is 14 or 0x8e) NativeMethods.DeleteEnhMetaFile(entry.Handle);
            else NativeMethods.GlobalFree(entry.Handle);
            entry.Handle = IntPtr.Zero;
        }
    }

    private static CaptureException Unsupported() => new(CaptureFailure.UnsupportedClipboard);
    private static CaptureException Changed() => new(CaptureFailure.ClipboardChanged);
    private sealed class Entry(uint format, IntPtr handle)
    {
        internal uint Format { get; } = format;
        internal IntPtr Handle { get; set; } = handle;
    }
}
