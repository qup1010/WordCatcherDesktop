using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Windows;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

// 离屏渲染真实 WPF 界面，只使用演示数据，不加载个人设置、词库或网络服务。
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/images");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WordCatcher.App;component/Themes/DesignSystem.xaml", UriKind.Relative)
        });
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            Color.FromRgb(15, 118, 110), Wpf.Ui.Appearance.ApplicationTheme.Light,
            systemGlassColor: false, systemAccentColor: false);

        var repo = DispatchProxy.Create<IWordRepository, DemoRepository>();
        var events = new WordCollectionEvents();
        var library = new LibraryViewModel(repo, events);
        var words = new[]
        {
            Word("convenience", "kənˈviːniəns", "n.", "方便；便利；让生活更省事的物品或设施。"),
            Word("resilient", "rɪˈzɪliənt", "adj.", "有韧性的；能从困难中恢复的。"),
            Word("perspective", "pəˈspektɪv", "n.", "视角；看待问题的方式。"),
            Word("a little", "ə ˈlɪtl", "phr.", "一点；少量。"),
            Word("familiar", "fəˈmɪliə", "adj.", "熟悉的；常见的。")
        };
        library.Words = new ObservableCollection<Word>(words);
        library.StatusMessage = "5 个词条 · 最近更新优先";
        library.SelectedWord = words[0];
        var lookup = new LookupViewModel(null!, repo, null!, null!, null!, events)
        {
            Source = WordCatcher.Core.Enums.LookupSource.Ai,
            HasResult = true,
            CanSave = true,
            Word = words[0].DisplayWord,
            SelectedText = words[0].DisplayWord,
            Reading = words[0].Reading,
            PartOfSpeech = words[0].PartOfSpeech,
            Definition = words[0].Definition,
            OriginalSentence = DemoRepository.Sentence,
            ContextTranslation = DemoRepository.Translation,
            MemoryHook = words[0].MemoryHook
        };
        var settingsService = new DemoSettings();
        var settings = new SettingsViewModel(settingsService, null!, null!, null!, null!, null!, null!);
        var sync = new SyncViewModel(repo, null!, settingsService);
        var window = new LibraryWindow(library, sync, settings, new WordLookupViewModel(lookup));
        ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("LibraryTab");
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 700));
        root.Arrange(new Rect(0, 0, 1180, 700));
        root.UpdateLayout();
        for (var pass = 0; pass < 3; pass++)
        {
            root.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            foreach (var expander in Descendants(root).OfType<Expander>()) expander.IsExpanded = true;
            root.UpdateLayout();
        }
        var frame = new System.Windows.Threading.DispatcherFrame();
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
        settle.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        root.Measure(new Size(1180, 700));
        root.Arrange(new Rect(0, 0, 1180, 700));
        root.UpdateLayout();
        Capture(root, 1180, 700, Path.Combine(output, "showcase-library.png"));

        var popup = new LookupWindow(lookup);
        var popupRoot = (FrameworkElement)popup.Content;
        popupRoot.Measure(new Size(420, double.PositiveInfinity));
        popupRoot.Arrange(new Rect(0, 0, 420, popupRoot.DesiredSize.Height));
        popupRoot.UpdateLayout();
        Capture(popupRoot, 420, (int)Math.Ceiling(popupRoot.DesiredSize.Height), Path.Combine(output, "showcase-lookup.png"));
        application.Shutdown();
        Console.WriteLine($"Rendered native demo interfaces into {output}");
    }

    private static Word Word(string text, string reading, string pos, string definition) => new()
    {
        DisplayWord = text,
        NormalizedWord = text,
        Reading = reading,
        PartOfSpeech = pos,
        Definition = definition,
        MemoryHook = text == "convenience" ? "想象一条少绕路的路：路更省事是便利，帮助你省事的东西是便利设施。" : "",
        Language = "en"
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Capture(FrameworkElement root, int width, int height, string path)
    {
        var bitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

public class DemoRepository : DispatchProxy
{
    public const string Sentence = "The convenience of a familiar place can make a difficult day feel a little easier.";
    public const string Translation = "熟悉的地方带来的便利，能让艰难的一天稍微轻松一点。";
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IWordRepository.GetOccurrencesByWordIdAsync))
        {
            IReadOnlyList<Occurrence> occurrences = [new()
            {
                WordId = (string)args![0]!,
                SelectedText = "convenience",
                Sentence = Sentence,
                SelectionOffset = 4,
                ContextTranslation = Translation,
                SourceWindowTitle = "Everyday Reading.pdf",
                SourceProcess = "PDF 阅读器",
                CapturedAtUtc = new DateTimeOffset(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(8))
            }];
            return Task.FromResult(occurrences);
        }
        if (method?.ReturnType == typeof(Task)) return Task.CompletedTask;
        throw new InvalidOperationException($"Unexpected showcase operation: {method?.Name}");
    }
}

internal sealed class DemoSettings : ISettingsService
{
    public AppSettings Current { get; } = new();
    public event Action<AppSettings>? SettingsChanged { add { } remove { } }
    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => throw new InvalidOperationException("Showcase settings are read-only.");
}
