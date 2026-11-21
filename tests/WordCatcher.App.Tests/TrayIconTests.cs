using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using WordCatcher.App.Tray;
using WordCatcher.Core.Models;
using Xunit;

namespace WordCatcher.App.Tests;

[Collection("Application resource rendering")]
public class TrayIconTests
{
    [Fact]
    public void BuildTooltipTextReflectsPausedStateOnly()
    {
        Assert.Equal("Word Catcher", TrayIconManager.BuildTooltipText(hotkeysEnabled: true));
        Assert.Equal("Word Catcher（快捷键已暂停）", TrayIconManager.BuildTooltipText(hotkeysEnabled: false));
    }

    [Fact]
    public void ComputeMenuOriginClampsInsideWorkAreaAndOpensAboveTaskbar()
    {
        // Cursor on bottom taskbar near bottom-right corner
        var (x1, y1) = TrayContextMenuWindow.ComputeMenuOrigin(
            cursorX: 1850,
            cursorY: 1055,
            widthPx: 288,
            heightPx: 420,
            shadowMarginPx: 12,
            workLeft: 0,
            workTop: 0,
            workRight: 1920,
            workBottom: 1032);
        var cardLeft1 = x1 + 12;
        var cardTop1 = y1 + 12;
        var cardWidth = 288 - 24;
        var cardHeight = 420 - 24;
        Assert.InRange(cardLeft1, 0, 1920 - cardWidth);
        Assert.InRange(cardTop1, 0, 1032 - cardHeight);

        // Cursor near top edge (top taskbar)
        var (x2, y2) = TrayContextMenuWindow.ComputeMenuOrigin(
            cursorX: 1700,
            cursorY: 20,
            widthPx: 288,
            heightPx: 420,
            shadowMarginPx: 12,
            workLeft: 0,
            workTop: 48,
            workRight: 1920,
            workBottom: 1080);
        Assert.True(y2 + 12 >= 48);
        Assert.True(x2 + 12 + cardWidth <= 1920);
    }

    [Fact]
    public void TrayContextMenuShowsTrimmedItemsTriggersTogglesAndClosesOnEscape()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            TrayContextMenuWindow? window = null;
            try
            {
                var mouseToggledCount = 0;
                var hotkeysToggledCount = 0;
                var openedLibrary = false;

                var actions = new TrayMenuActions(
                    OpenLibrary: () => openedLibrary = true,
                    OpenLookup: () => { },
                    TriggerOcrCapture: () => { },
                    ToggleMouseSelection: () => mouseToggledCount++,
                    ToggleHotkeys: () => hotkeysToggledCount++,
                    OpenSettings: () => { },
                    ExitApp: () => { });

                var snapshot = new TrayMenuSnapshot(
                    MouseSelectionEnabled: true,
                    HotkeysEnabled: true,
                    HotkeyText: "Ctrl+Shift+C",
                    OcrHotkeyText: "Ctrl+Shift+O");

                window = new TrayContextMenuWindow(snapshot, actions);
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.Measure(new Size(400, 800));
                root.Arrange(new Rect(root.DesiredSize));
                root.UpdateLayout();

                Assert.Equal(
                    new[]
                    {
                        "打开生词库",
                        "手动查词",
                        "截屏 OCR 取词",
                        "鼠标划词取词",
                        "启用全局快捷键",
                        "设置",
                        "退出"
                    },
                    window.MenuItemLabelsForTest);

                Assert.DoesNotContain("立即取词", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("浮窗形态", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("极简译文", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("迷你卡片", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("完整卡片", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("立即同步词库", window.MenuItemLabelsForTest);
                Assert.DoesNotContain("重试 Anki 待同步任务", window.MenuItemLabelsForTest);

                // Click 鼠标划词取词 toggle
                var mouseBtn = window.GetButtonByTagForTest("toggle-mouse");
                Assert.NotNull(mouseBtn);
                mouseBtn!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, mouseToggledCount);
                window = null;

                // Click 启用全局快捷键 toggle on a fresh menu
                window = new TrayContextMenuWindow(snapshot, actions);
                var hotkeyBtn = window.GetButtonByTagForTest("toggle-hotkeys");
                Assert.NotNull(hotkeyBtn);
                hotkeyBtn!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(1, hotkeysToggledCount);
                window = null;

                // Verify Esc closes without invoking callbacks
                window = new TrayContextMenuWindow(snapshot, actions);
                var closed = false;
                window.Closed += (_, _) => closed = true;
                var esc = new KeyEventArgs(Keyboard.PrimaryDevice, new StubPresentationSource(), 0, Key.Escape)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                window.RaiseEvent(esc);
                Assert.True(esc.Handled);
                Assert.True(closed);
                Assert.False(openedLibrary);
                window = null;
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.SafeClose(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    private sealed class StubPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }
}
