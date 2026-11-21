using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WordCatcher.App.Services;
using WordCatcher.App.ViewModels;
using WordCatcher.App.Windows;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

// 离屏渲染真实 WPF 界面，只使用演示数据，不加载个人设置、词库或网络服务。
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("docs", "images", "raw"));
        Directory.CreateDirectory(output);

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/WordCatcher.App;component/Themes/DesignSystem.xaml", UriKind.Relative)
        });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        ThemeManager.ApplyTheme(ThemeManager.LightMode);

        var repo = DispatchProxy.Create<IWordRepository, DemoRepository>();
        var events = new WordCollectionEvents();
        var settingsService = new DemoSettings();
        var speechService = new DemoSpeechService();
        var lookupService = DispatchProxy.Create<ILookupService, DemoLookupService>();
        var syncQueue = DispatchProxy.Create<IAnkiSyncQueue, DemoAnkiSyncQueue>();

        var words = new[]
        {
            Word("w1", "resilient", "/rɪˈzɪliənt/", "adj.", "有韧性的；能迅速适应变化并从困难中恢复的。",
                "re-（回）+ sil（跳）→ 压下去还能弹回来，就是有韧性。"),
            Word("w2", "convenience", "/kənˈviːniəns/", "n.", "方便；便利；让生活更省事的物品或设施。"),
            Word("w3", "perspective", "/pəˈspektɪv/", "n.", "视角；看待问题的方式；洞察力。"),
            Word("w4", "semantic", "/sɪˈmæntɪk/", "adj.", "语义的；与意义有关的；体现意图和结构的。"),
            Word("w5", "leverage", "/ˈlevərɪdʒ/", "n. / v.", "杠杆作用；影响力；充分利用。")
        };

        var library = new LibraryViewModel(repo, events);
        library.Words = new ObservableCollection<Word>(words);
        library.MatchingWordCount = words.Length;
        library.StatusMessage = string.Empty;
        library.SelectedWord = words[0];

        var mainLookup = new LookupViewModel(lookupService, repo, syncQueue, settingsService, speechService, events);
        var settings = new SettingsViewModel(settingsService, null!, null!, null!, null!, null!, null!);
        var sync = new SyncViewModel(repo, syncQueue, settingsService);
        var window = new LibraryWindow(library, sync, settings, new WordLookupViewModel(mainLookup));
        ((TabControl)window.FindName("MainTabs")).SelectedItem = window.FindName("LibraryTab");
        var root = (FrameworkElement)window.Content;

        SettleWindow(root, 1180, 700, expandExpanders: true);
        Capture(root, 1180, 700, Path.Combine(output, "library-light.png"), fillCanvas: true);

        // 卡片复习模式（带目标词高亮与答案展开）
        library.StartReviewCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        library.ToggleAnswerCommand.Execute(null);
        library.StatusMessage = string.Empty;
        SettleWindow(root, 1180, 760, expandExpanders: false);
        Capture(root, 1180, 760, Path.Combine(output, "review-light.png"), fillCanvas: true);

        // 1. 完整查词浮窗（浅色 AI 详解）
        var resilientResult = new TranslationResult(
            "resilient",
            "/rɪˈzɪliənt/",
            "adj.",
            "有韧性的；能迅速适应变化并从困难中恢复的。",
            "有韧性的设计能让日常阅读在工作繁忙时依然保持从容。",
            "re-（回）+ sil（跳）→ 压下去还能弹回来，就是有韧性。",
            LookupSource.Ai,
            null,
            null,
            Analysis: new LearningAnalysis(
                "word",
                "resilient",
                "这里形容设计架构在高压或复杂流程下仍能保持稳定与从容。",
                "常与 system、design、team 搭配，强调抗压后的恢复力。",
                "Local caches make the reader resilient when the network drops.",
                "本地缓存让阅读器在断网时依然稳定可用。"));
        var resilientCapture = new CaptureResult(
            "resilient",
            "PDF 阅读器",
            "Design Notes.pdf",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow,
            Sentence: DemoRepository.ResilientSentence,
            SentenceOffset: 2);
        CaptureLookup(
            BuildLookupVm(lookupService, repo, syncQueue, settingsService, speechService, events,
                UiSettings.PopupModeFull, resilientCapture, resilientResult, expandMemoryHook: true),
            420,
            Path.Combine(output, "lookup-full.png"));

        // 2. 极简译文浮窗
        var minimalResult = new TranslationResult(
            "perspective",
            "/pəˈspektɪv/",
            "n.",
            "视角；看待问题的方式；洞察力",
            "",
            null,
            LookupSource.OpenDictionary,
            null,
            null);
        var minimalCapture = new CaptureResult(
            "perspective",
            "Reader",
            "Notes.md",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow);
        CaptureLookup(
            BuildLookupVm(lookupService, repo, syncQueue, settingsService, speechService, events,
                UiSettings.PopupModeMinimal, minimalCapture, minimalResult),
            320,
            Path.Combine(output, "lookup-minimal.png"),
            autoFitWidth: true);

        // 3. 迷你卡片浮窗
        var miniGroups = new List<DictPosGroup>
        {
            new("n.", null, [new DictMeaning("1", "core", "杠杆作用；影响力；筹码", null, [])]),
            new("v.", null, [new DictMeaning("2", "core", "充分利用；借助……发挥优势", null, [])])
        };
        var miniResult = new TranslationResult(
            "leverage",
            "/ˈlevərɪdʒ/",
            "n. / v.",
            "n. 杠杆作用；影响力；v. 充分利用",
            "",
            null,
            LookupSource.OpenDictionary,
            null,
            null,
            PosGroups: miniGroups);
        var miniCapture = new CaptureResult(
            "leverage",
            "Reader",
            "Strategy Memo.docx",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow);
        CaptureLookup(
            BuildLookupVm(lookupService, repo, syncQueue, settingsService, speechService, events,
                UiSettings.PopupModeMini, miniCapture, miniResult),
            352,
            Path.Combine(output, "lookup-mini.png"));

        // 4. 截屏 OCR 与句中选词浮窗
        const string ocrSentence = "Adaptive context retrieval improves precision.";
        var ocrVm = new LookupViewModel(lookupService, repo, syncQueue, settingsService, speechService, events)
        {
            PopupMode = UiSettings.PopupModeFull
        };
        var ocrSentenceResult = new TranslationResult(
            ocrSentence,
            "",
            "",
            "自适应上下文检索能够提升精度。",
            "",
            null,
            LookupSource.MachineTranslation,
            null,
            null);
        ocrVm.StartLookupAsync(
            new CaptureResult(ocrSentence, "ScreenOcr", "截屏 OCR 取词", default, DateTimeOffset.UtcNow, Sentence: ocrSentence),
            historyResult: ocrSentenceResult).GetAwaiter().GetResult();

        var retrievalGroups = new List<DictPosGroup>
        {
            new("n.", null, [new DictMeaning("1", "core", "检索；取回；恢复", null, [])])
        };
        var retrievalResult = new TranslationResult(
            "retrieval",
            "/rɪˈtriːvəl/",
            "n.",
            "检索；取回；恢复",
            "自适应上下文检索能够提升精度。",
            null,
            LookupSource.OpenDictionary,
            null,
            null,
            PosGroups: retrievalGroups);
        ocrVm.StartLookupAsync(
            new CaptureResult("retrieval", "ScreenOcr", "截屏 OCR 取词", default, DateTimeOffset.UtcNow,
                Sentence: ocrSentence,
                SentenceOffset: ocrSentence.IndexOf("retrieval", StringComparison.Ordinal)),
            historyResult: retrievalResult).GetAwaiter().GetResult();
        CaptureLookup(ocrVm, 420, Path.Combine(output, "lookup-ocr.png"));

        // 5. 划词浮标（浅色）
        var pillWindow = new SelectionPillWindow();
        var pillRoot = (FrameworkElement)pillWindow.Content;
        SettleElement(pillRoot, 56, 56);
        Capture(pillRoot, 56, 56, Path.Combine(output, "pill.png"));

        // 6. 深色模式完整查词浮窗
        ThemeManager.ApplyTheme(ThemeManager.DarkMode);
        var semanticResult = new TranslationResult(
            "semantic",
            "/sɪˈmæntɪk/",
            "adj.",
            "语义的；与意义有关的；体现意图和结构的。",
            "语义匹配结合关键词信号，可显著提升长尾检索召回率。",
            "sema-（记号、意义）+ -tic（形容词后缀）→ 表示“意义的”。",
            LookupSource.Ai,
            null,
            null,
            Analysis: new LearningAnalysis(
                "word",
                "semantic",
                "常见于搜索、自然语言处理与前端标签架构语境。",
                "常用搭配：semantic search（语义检索）、semantic HTML。",
                "Semantic search matches user intent beyond exact keywords.",
                "语义检索能在字面关键词之外理解用户意图。"));
        var semanticCapture = new CaptureResult(
            "semantic",
            "Edge",
            "Information Retrieval Notes",
            new ScreenPoint(0, 0),
            DateTimeOffset.UtcNow,
            Sentence: "Semantic matching improves recall across long-tail queries.",
            SentenceOffset: 0);
        CaptureLookup(
            BuildLookupVm(lookupService, repo, syncQueue, settingsService, speechService, events,
                UiSettings.PopupModeFull, semanticCapture, semanticResult, expandMemoryHook: true),
            420,
            Path.Combine(output, "lookup-dark.png"));

        application.Shutdown();
        Console.WriteLine($"Rendered native showcase assets into {output}");
    }

    private static LookupViewModel BuildLookupVm(
        ILookupService lookupService,
        IWordRepository repo,
        IAnkiSyncQueue syncQueue,
        ISettingsService settingsService,
        ISpeechService speechService,
        WordCollectionEvents events,
        string popupMode,
        CaptureResult capture,
        TranslationResult result,
        bool expandMemoryHook = false)
    {
        var vm = new LookupViewModel(lookupService, repo, syncQueue, settingsService, speechService, events)
        {
            PopupMode = popupMode
        };
        vm.StartLookupAsync(capture, historyResult: result).GetAwaiter().GetResult();
        if (expandMemoryHook && !string.IsNullOrWhiteSpace(vm.MemoryHook))
            vm.IsMemoryHookExpanded = true;
        return vm;
    }

    private static void CaptureLookup(LookupViewModel vm, int width, string path, bool autoFitWidth = false)
    {
        var popup = new LookupWindow(vm);
        var popupRoot = (FrameworkElement)popup.Content;
        TextOptions.SetTextFormattingMode(popupRoot, TextFormattingMode.Ideal);
        popupRoot.Measure(new Size(width, double.PositiveInfinity));
        popupRoot.Arrange(new Rect(0, 0, width, Math.Max(60, popupRoot.DesiredSize.Height)));
        popupRoot.UpdateLayout();
        popupRoot.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        popupRoot.Measure(new Size(width, double.PositiveInfinity));
        var targetWidth = autoFitWidth ? Math.Max(140, Math.Ceiling(popupRoot.DesiredSize.Width) + 6) : width;
        var targetHeight = Math.Max(60, Math.Ceiling(popupRoot.DesiredSize.Height));
        SettleElement(popupRoot, targetWidth, targetHeight);
        Capture(popupRoot, (int)targetWidth, (int)targetHeight, path);
    }

    private static void SettleWindow(FrameworkElement root, int width, int height, bool expandExpanders)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        for (var pass = 0; pass < 3; pass++)
        {
            root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            if (expandExpanders)
            {
                foreach (var expander in Descendants(root).OfType<Expander>())
                    expander.IsExpanded = true;
            }
            root.UpdateLayout();
        }
        PumpWait(350);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void SettleElement(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void PumpWait(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
        settle.Start();
        Dispatcher.PushFrame(frame);
    }

    private static Word Word(string id, string text, string reading, string pos, string definition, string memoryHook = "") => new()
    {
        Id = id,
        DisplayWord = text,
        NormalizedWord = text,
        Reading = reading,
        PartOfSpeech = pos,
        Definition = definition,
        MemoryHook = memoryHook,
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

    private static void Capture(FrameworkElement root, int width, int height, string path, bool fillCanvas = false)
    {
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        if (fillCanvas)
        {
            var bitmap = new RenderTargetBitmap(width * 2, height * 2, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new ScaleTransform(2, 2));
                if (root.TryFindResource("CanvasBrush") is Brush canvasBrush)
                    context.DrawRectangle(canvasBrush, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, new Rect(0, 0, width, height));
                context.Pop();
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
            return;
        }

        var directBitmap = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        directBitmap.Render(root);
        var directEncoder = new PngBitmapEncoder();
        directEncoder.Frames.Add(BitmapFrame.Create(directBitmap));
        using var directFile = File.Create(path);
        directEncoder.Save(directFile);
    }
}

public class DemoRepository : DispatchProxy
{
    public const string ResilientSentence = "A resilient design keeps everyday reading calm even when workflows get busy.";
    public const string ResilientTranslation = "有韧性的设计能让日常阅读在工作繁忙时依然保持从容。";

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(IWordRepository.GetOccurrencesByWordIdAsync))
        {
            var wordId = (string)args![0]!;
            IReadOnlyList<Occurrence> occurrences = wordId switch
            {
                "w1" =>
                [
                    new Occurrence
                    {
                        Id = "o1",
                        WordId = "w1",
                        SelectedText = "resilient",
                        Sentence = ResilientSentence,
                        SelectionOffset = 2,
                        ContextTranslation = ResilientTranslation,
                        SourceWindowTitle = "Design Notes.pdf",
                        SourceProcess = "PDF 阅读器",
                        CapturedAtUtc = new DateTimeOffset(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(8)),
                        LearningResult = new TranslationResult(
                            "resilient",
                            "/rɪˈzɪliənt/",
                            "adj.",
                            "有韧性的；能迅速适应变化并从困难中恢复的。",
                            ResilientTranslation,
                            "re-（回）+ sil（跳）→ 压下去还能弹回来，就是有韧性。",
                            LookupSource.Ai,
                            null,
                            null,
                            Analysis: new LearningAnalysis(
                                "word",
                                "resilient",
                                "这里形容设计架构在高压或复杂流程下仍能保持稳定与从容。",
                                "",
                                "",
                                ""))
                    }
                ],
                _ => Array.Empty<Occurrence>()
            };
            return Task.FromResult(occurrences);
        }

        if (method?.Name == nameof(IWordRepository.FindWordAsync))
            return Task.FromResult<Word?>(null);

        if (method?.Name == nameof(IWordRepository.GetWordsAsync) || method?.Name == nameof(IWordRepository.GetFilteredWordsAsync))
            return Task.FromResult<IReadOnlyList<Word>>(Array.Empty<Word>());

        if (method?.Name == nameof(IWordRepository.GetSyncJobsAsync))
            return Task.FromResult<IReadOnlyList<SyncJob>>(Array.Empty<SyncJob>());

        if (method?.ReturnType == typeof(Task))
            return Task.CompletedTask;

        throw new InvalidOperationException($"Unexpected showcase operation: {method?.Name}");
    }
}

public class DemoLookupService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => throw new InvalidOperationException($"Showcase should not invoke live lookup: {method?.Name}");
}

public class DemoAnkiSyncQueue : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.ReturnType == typeof(Task))
            return Task.CompletedTask;
        throw new InvalidOperationException($"Unexpected Anki queue operation: {method?.Name}");
    }
}

internal sealed class DemoSettings : ISettingsService
{
    public AppSettings Current { get; } = new();
    public event Action<AppSettings>? SettingsChanged { add { } remove { } }
    public Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default) => Task.FromResult(Current);
    public Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default) => throw new InvalidOperationException("Showcase settings are read-only.");
}

internal sealed class DemoSpeechService : ISpeechService
{
    public void Speak(string text) { }
    public void Stop() { }
}
