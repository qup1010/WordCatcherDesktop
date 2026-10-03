namespace WordCatcher.Core.Models;

public enum CaptureFailure
{
    NoSelection, CopyTimeout, ClipboardBusy, ClipboardChanged, UnsupportedClipboard,
    SourceChanged, InputBlocked, ModifiersHeld, SelectionTooLong, Unexpected
}

public sealed class CaptureException(CaptureFailure failure, Exception? inner = null)
    : Exception(GetMessage(failure), inner)
{
    public CaptureFailure Failure { get; } = failure;
    private static string GetMessage(CaptureFailure failure) => failure switch
    {
        CaptureFailure.NoSelection => "没有获取到选中文字，请先选中文字再取词。",
        CaptureFailure.CopyTimeout => "目标程序未及时提供复制结果，请重新选中文字后重试。",
        CaptureFailure.ClipboardBusy => "剪贴板正被其他程序占用，请稍后重试。",
        CaptureFailure.ClipboardChanged => "取词期间剪贴板发生变化，本次取词已取消，新的复制内容已保留。",
        CaptureFailure.UnsupportedClipboard => "当前剪贴板含有无法安全备份的格式。请先保存其中的内容，或使用应用内查询。",
        CaptureFailure.SourceChanged => "取词期间切换了窗口，本次取词已取消。请在来源窗口中重新取词。",
        CaptureFailure.InputBlocked => "无法向目标程序发送复制按键，可能存在权限限制。请确认两个程序的权限一致后重试。",
        CaptureFailure.ModifiersHeld => "取词快捷键尚未松开，请松开按键后重试。",
        CaptureFailure.SelectionTooLong => "选中文字超过 5,000 字符，请缩小选区后重试。",
        _ => "取词暂时失败，请重新选中文字后重试。"
    };
}
