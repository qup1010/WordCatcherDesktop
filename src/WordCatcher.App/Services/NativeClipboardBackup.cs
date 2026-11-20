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
    internal ClipboardRestoreResult? LastRestoreResult { get; private set; }

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
                {
                    if (!TryGetBitmapSize(original, out var imageBytes) || !TryAddToBudget(ref bytes, imageBytes))
                        throw Unsupported();
                    copy = NativeMethods.CopyImage(original, 0, 0, 0, 0x2000); // LR_CREATEDIBSECTION
                    if (copy != IntPtr.Zero && TryGetBitmapSize(copy, out var clonedBytes) && clonedBytes > imageBytes
                        && !TryAddToBudget(ref bytes, clonedBytes - imageBytes))
                    {
                        NativeMethods.DeleteObject(copy);
                        throw Unsupported();
                    }
                }
                else if (format is 14 or 0x8e)
                {
                    var imageBytes = NativeMethods.GetEnhMetaFileBits(original, 0, IntPtr.Zero);
                    if (imageBytes == 0 || !TryAddToBudget(ref bytes, imageBytes)) throw Unsupported();
                    copy = NativeMethods.CopyEnhMetaFile(original, null);
                }
                else
                {
                    var size = NativeMethods.GlobalSize(original);
                    if (size == 0 || size > MaxBytes || !TryAddToBudget(ref bytes, (long)size)) throw Unsupported();
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
            var afterSequence = NativeMethods.GetClipboardSequenceNumber();
            if (afterSequence != expectedSequence)
            {
                // 延迟渲染期间 Windows 可能递增序号；在同一次剪贴板锁内复核 owner 未变则接受。
                var afterOwner = NativeMethods.GetClipboardOwner();
                NativeMethods.GetWindowThreadProcessId(afterOwner, out var afterOwnerProcess);
                if (afterOwner == IntPtr.Zero || afterOwnerProcess != source.ProcessId) throw Changed();
            }
            string? text = null;
            if (memory != IntPtr.Zero)
            {
                var size = NativeMethods.GlobalSize(memory);
                if (size > 1024 * 1024)
                    return new ClipboardRead(null, afterSequence, CaptureFailure.SelectionTooLong);
                var pointer = NativeMethods.GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                    return new ClipboardRead(null, afterSequence, CaptureFailure.ClipboardBusy);
                try
                {
                    text = Marshal.PtrToStringUni(pointer, Math.Min((int)size / 2, 5001))?.Split('\0')[0];
                }
                finally { NativeMethods.GlobalUnlock(memory); }
            }
            return new ClipboardRead(text, afterSequence);
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
        // 在清空剪贴板前先完成格式注册与标记内存分配，避免任一步失败留下空剪贴板。
        List<(uint Format, IntPtr Handle)>? preparedMarkers = null;
        uint excludeFormat = 0, historyFormat = 0, cloudFormat = 0;
        if (_entries.Count > 0)
        {
            preparedMarkers = [];
            try
            {
                excludeFormat = GetRegisteredFormat("ExcludeClipboardContentFromMonitorProcessing");
                historyFormat = GetRegisteredFormat("CanIncludeInClipboardHistory");
                cloudFormat = GetRegisteredFormat("CanUploadToCloudClipboard");
                preparedMarkers.Add((excludeFormat, AllocDword(1)));
                preparedMarkers.Add((historyFormat, AllocDword(0)));
                preparedMarkers.Add((cloudFormat, AllocDword(0)));
            }
            catch
            {
                FreeHandles(preparedMarkers);
                preparedMarkers = null;
            }
        }

        try { Open(owner.Handle); }
        catch (Exception ex)
        {
            FreeHandles(preparedMarkers);
            LastRestoreResult = new ClipboardRestoreResult(false, "Failed", ex.Message);
            throw;
        }
        try
        {
            // 比较和写入都在同一次剪贴板锁内，阻止检查后用户复制再被覆盖的竞争。
            if (NativeMethods.GetClipboardSequenceNumber() != expectedSequence)
            {
                FreeHandles(preparedMarkers);
                LastRestoreResult = new ClipboardRestoreResult(false, "SkippedClipboardChanged");
                return false;
            }
            if (!NativeMethods.EmptyClipboard())
            {
                FreeHandles(preparedMarkers);
                throw new CaptureException(CaptureFailure.ClipboardBusy);
            }
            if (_entries.Count == 0)
            {
                LastRestoreResult = new ClipboardRestoreResult(true, "RestoredEmpty");
                return true;
            }

            if (preparedMarkers != null)
            {
                for (var i = 0; i < preparedMarkers.Count; i++)
                {
                    var (fmt, handle) = preparedMarkers[i];
                    if (handle == IntPtr.Zero) continue;
                    if (NativeMethods.SetClipboardData(fmt, handle) != IntPtr.Zero)
                        preparedMarkers[i] = (fmt, IntPtr.Zero);
                    else
                        NativeMethods.GlobalFree(handle);
                }
            }

            var restoredEntries = 0;
            var failedEntries = 0;
            foreach (var entry in _entries)
            {
                if (entry.Handle == IntPtr.Zero) continue;
                // 前一次恢复写入的标记不应重复设置，也不应复用其可能的旧值。
                if (excludeFormat != 0 && (entry.Format == excludeFormat || entry.Format == historyFormat || entry.Format == cloudFormat))
                    continue;
                if (NativeMethods.SetClipboardData(entry.Format, entry.Handle) != IntPtr.Zero)
                {
                    entry.Handle = IntPtr.Zero; // 已移交给 Windows，不再释放。
                    restoredEntries++;
                }
                else
                {
                    failedEntries++;
                }
            }
            if (failedEntries > 0)
            {
                var status = restoredEntries > 0 ? "Partial" : "Failed";
                LastRestoreResult = new ClipboardRestoreResult(restoredEntries > 0, status,
                    $"Failed to restore {failedEntries} clipboard format(s).");
                if (restoredEntries == 0)
                    throw new CaptureException(CaptureFailure.ClipboardBusy);
                return true;
            }
            LastRestoreResult = new ClipboardRestoreResult(true, "Restored");
            return true;
        }
        catch (Exception ex)
        {
            FreeHandles(preparedMarkers);
            if (LastRestoreResult?.Status != "Failed")
                LastRestoreResult = new ClipboardRestoreResult(false, "Failed", ex.Message);
            throw;
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    private static void FreeHandles(List<(uint Format, IntPtr Handle)>? handles)
    {
        if (handles == null) return;
        foreach (var (_, handle) in handles)
            if (handle != IntPtr.Zero) NativeMethods.GlobalFree(handle);
        handles.Clear();
    }

    internal static bool TryAddToBudget(ref long currentBytes, long itemBytes)
    {
        if (itemBytes <= 0 || currentBytes < 0 || itemBytes > MaxBytes - currentBytes) return false;
        currentBytes += itemBytes;
        return true;
    }

    private static bool TryGetBitmapSize(IntPtr bitmapHandle, out long bytes)
    {
        bytes = 0;
        if (NativeMethods.GetObject(bitmapHandle, Marshal.SizeOf<NativeMethods.BitmapObject>(), out var bitmap) == 0)
            return false;
        long dibRowBytes = 0;
        if (bitmap.Width > 0 && bitmap.BitsPixel > 0)
        {
            try { dibRowBytes = checked((((long)bitmap.Width * bitmap.BitsPixel + 31) / 32) * 4); }
            catch (OverflowException) { return false; }
        }
        var rowBytes = Math.Max((long)bitmap.WidthBytes, dibRowBytes);
        var height = Math.Abs((long)bitmap.Height);
        if (rowBytes <= 0 || height <= 0) return false;
        try { bytes = checked(rowBytes * height); }
        catch (OverflowException) { return false; }
        return bytes > 0;
    }

    private static uint GetRegisteredFormat(string name)
    {
        var format = NativeMethods.RegisterClipboardFormat(name);
        if (format == 0) throw new CaptureException(CaptureFailure.ClipboardBusy,
            new InvalidOperationException($"Could not register clipboard format '{name}'."));
        return format;
    }

    private static IntPtr AllocDword(uint value)
    {
        var handle = NativeMethods.GlobalAlloc(2, sizeof(uint)); // GMEM_MOVEABLE
        if (handle == IntPtr.Zero) throw new CaptureException(CaptureFailure.ClipboardBusy);
        var ok = false;
        try
        {
            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new CaptureException(CaptureFailure.ClipboardBusy);
            try { Marshal.WriteInt32(pointer, unchecked((int)value)); }
            finally { NativeMethods.GlobalUnlock(handle); }
            ok = true;
            return handle;
        }
        finally { if (!ok) NativeMethods.GlobalFree(handle); }
    }

    internal static void Open(IntPtr owner)
    {
        for (var attempt = 0; attempt < 20; attempt++)
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

internal sealed record ClipboardRestoreResult(bool Restored, string Status, string? Failure = null);
