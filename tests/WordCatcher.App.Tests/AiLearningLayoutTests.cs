using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Models;
using WordCatcher.App.Windows;
using WordCatcher.App.Views;
using WordCatcher.App.ViewModels;

namespace WordCatcher.App.Tests;

public sealed class AiLearningLayoutTests
{
    [Fact]
    public void AiResultRendersExpandedExplanationAndEditableFieldsWithVisibleFooter()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var (vm, lookup, _, _) = LookupViewModelTests.Create();
                lookup.Lookup = _ => Task.FromResult(new TranslationResult("stride", "", "n.", "坦然接受，不因批评而慌乱。",
                    "他坦然接受了她的批评。", null, LookupSource.Ai, null, "learning-v1",
                    Analysis: new("word", "take something in stride", "这里是固定表达，不能按 stride 的步幅理解。",
                        "常用于面对批评、挫折或变化。", "She took the setback in stride.", "她从容面对了这次挫折。")));
                vm.StartLookupAsync(new CaptureResult("stride", "reader", "Book", new ScreenPoint(0, 0), DateTimeOffset.UtcNow,
                    "He took her criticism in stride.", 25)).GetAwaiter().GetResult();
                var window = new LookupWindow(vm);
                window.ShowActivated = false;
                window.Left = -10000;
                window.Top = -10000;
                window.Show();
                var root = (FrameworkElement)window.Content;
                root.Measure(new Size(420, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height)); root.UpdateLayout();
                foreach (var expander in Descendants(root).OfType<Expander>()) expander.IsExpanded = true;
                FinishAnimations();
                root.Measure(new Size(420, double.PositiveInfinity));
                root.Arrange(new Rect(0, 0, 420, root.DesiredSize.Height)); root.UpdateLayout();
                Assert.InRange(root.ActualHeight, 200, 680);
                var save = (Button)window.FindName("SaveWordButton");
                Assert.True(save.IsEnabled);
                Assert.True(save.TransformToAncestor(root).Transform(new Point()).Y + save.ActualHeight <= root.ActualHeight);
                Assert.Contains(Descendants(root).OfType<TextBlock>(), t => t.Text == "take something in stride");
                SaveImage(root, 420, (int)Math.Ceiling(root.ActualHeight), "wordcatcher-ai-learning-popup.png");
                var boxes = Descendants(root).OfType<Wpf.Ui.Controls.TextBox>().ToList();
                Assert.True(boxes.Any(t => t.Text == vm.EditDefinition),
                    "编辑区域：" + string.Join(" | ", Descendants(root).OfType<Expander>().Select(e => $"{e.Header}: expanded={e.IsExpanded}, visible={e.IsVisible}"))
                    + "；输入控件：" + string.Join(" | ", Descendants(root).Select(e => e.GetType().FullName).Where(n => n!.Contains("TextBox"))));
                var pageVm = new WordLookupViewModel(vm) { HasSearched = true };
                var page = new WordLookupView { DataContext = pageVm };
                page.Measure(new Size(880, 620)); page.Arrange(new Rect(0, 0, 880, 620)); page.UpdateLayout();
                foreach (var expander in Descendants(page).OfType<Expander>().Where(e => e.Header is string header && header != "查询历史")) expander.IsExpanded = true;
                FinishAnimations();
                page.Measure(new Size(880, 620)); page.Arrange(new Rect(0, 0, 880, 620)); page.UpdateLayout();
                SaveImage(page, 880, 620, "wordcatcher-ai-learning-page.png");
                Assert.Equal("重新生成", vm.AiButtonText);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        Assert.Null(failure);
    }
    private static void SaveImage(FrameworkElement element, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(System.IO.Path.GetTempPath(), name)); encoder.Save(file);
    }
    private static void FinishAnimations()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
