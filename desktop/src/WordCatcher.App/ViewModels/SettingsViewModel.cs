using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Translation;

namespace WordCatcher.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly IOfflineDictionaryService _offlineDict;
    private readonly IDictionaryInstaller _dictInstaller;
    private readonly IAnkiClient _ankiClient;
    private readonly OpenAiTranslationService _translationService;
    private readonly HotkeyManager _hotkeyManager;

    [ObservableProperty]
    private string _hotkey = "Alt+Q";

    [ObservableProperty]
    private bool _dictInstalled;

    [ObservableProperty]
    private string _dictStatusText = "未安装";

    [ObservableProperty]
    private string _dictDetailsText = string.Empty;

    [ObservableProperty]
    private string _dictDownloadUrl = "https://github.com/ahpxex/open-dictionary/releases/latest/download/distribution.sqlite.gz";

    [ObservableProperty]
    private bool _isDictInstalling;

    [ObservableProperty]
    private string _dictProgressText = string.Empty;

    [ObservableProperty]
    private double _dictProgressValue;

    [ObservableProperty]
    private string _apiBaseUrl = "https://api.openai.com/v1";

    [ObservableProperty]
    private string _apiModel = "gpt-4o-mini";

    [ObservableProperty]
    private int _apiTimeoutSeconds = 30;

    [ObservableProperty]
    private ObservableCollection<TranslationProfile> _aiProfiles = new();

    [ObservableProperty]
    private string _activeAiProfileId = "default";

    [ObservableProperty]
    private string _aiProfileName = "默认";

    [ObservableProperty]
    private string _machineTranslationProvider = "microsoft";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _explainLanguage = "简体中文";

    [ObservableProperty]
    private bool _ankiEnabled = true;

    [ObservableProperty]
    private string _ankiUrl = "http://127.0.0.1:8765";

    [ObservableProperty]
    private string _ankiDeckName = "Word Catcher";

    [ObservableProperty]
    private string _ankiNoteTypeName = "Word Catcher";

    [ObservableProperty]
    private bool _ankiClozeContext = true;

    [ObservableProperty]
    private string _ankiTtsLang = "en_US";

    [ObservableProperty]
    private bool _closePopupAfterSave = true;

    private bool _profileEditorReady;
    private string _editingProfileId = string.Empty;
    private readonly Dictionary<string, string> _profileApiKeys = new(StringComparer.Ordinal);

    public SettingsViewModel(
        ISettingsService settingsService,
        ISecretStore secretStore,
        IOfflineDictionaryService offlineDict,
        IDictionaryInstaller dictInstaller,
        IAnkiClient ankiClient,
        OpenAiTranslationService translationService,
        HotkeyManager hotkeyManager)
    {
        _settingsService = settingsService;
        _secretStore = secretStore;
        _offlineDict = offlineDict;
        _dictInstaller = dictInstaller;
        _ankiClient = ankiClient;
        _translationService = translationService;
        _hotkeyManager = hotkeyManager;
    }

    public async Task InitializeAsync()
    {
        var s = _settingsService.Current;
        Hotkey = s.Hotkey;
        ExplainLanguage = s.ExplainLanguage;

        var profiles = EnsureProfiles(s);
        AiProfiles = new ObservableCollection<TranslationProfile>(profiles.Select(CloneProfile));
        var active = AiProfiles.FirstOrDefault(p => p.Id == s.ActiveTranslationProfileId)
            ?? AiProfiles.FirstOrDefault()
            ?? CreateDefaultProfile();
        if (AiProfiles.Count == 0)
            AiProfiles.Add(active);

        _profileApiKeys.Clear();
        foreach (var profile in AiProfiles)
        {
            _profileApiKeys[profile.Id] = await ReadProfileApiKeyAsync(profile.Id).ConfigureAwait(true);
        }

        _profileEditorReady = false;
        ActiveAiProfileId = active.Id;
        LoadProfile(active);
        _editingProfileId = active.Id;
        _profileEditorReady = true;

        DictDownloadUrl = s.Dictionary.DownloadUrl;

        AnkiEnabled = s.Anki.Enabled;
        AnkiUrl = s.Anki.Url;
        AnkiDeckName = s.Anki.DeckName;
        AnkiNoteTypeName = s.Anki.NoteTypeName;
        AnkiClozeContext = s.Anki.ClozeContext;
        AnkiTtsLang = s.Anki.TtsLang;

        ClosePopupAfterSave = s.Ui.ClosePopupAfterSave;

        RefreshDictStatus();
    }

    partial void OnActiveAiProfileIdChanged(string value)
    {
        if (!_profileEditorReady || string.IsNullOrWhiteSpace(value))
            return;

        SaveEditorToProfile(_editingProfileId);
        var profile = AiProfiles.FirstOrDefault(p => p.Id == value);
        if (profile == null)
            return;

        LoadProfile(profile);
        _editingProfileId = profile.Id;
    }

    [RelayCommand]
    private void AddAiProfile()
    {
        SaveEditorToProfile(_editingProfileId);
        var profile = new TranslationProfile
        {
            Id = $"profile-{Guid.NewGuid():N}",
            Name = $"配置 {AiProfiles.Count + 1}",
            BaseUrl = ApiBaseUrl,
            Model = ApiModel,
            TimeoutSeconds = ApiTimeoutSeconds,
            MachineTranslationProvider = MachineTranslationProvider
        };
        AiProfiles.Add(profile);
        ActiveAiProfileId = profile.Id;
    }

    [RelayCommand]
    private void RemoveAiProfile()
    {
        if (AiProfiles.Count <= 1)
        {
            MessageBox.Show("至少需要保留一个 AI 配置。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profile = AiProfiles.FirstOrDefault(p => p.Id == ActiveAiProfileId);
        if (profile == null)
            return;

        var confirm = MessageBox.Show(
            $"确定删除 AI 配置「{profile.Name}」吗？",
            "删除配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        var index = AiProfiles.IndexOf(profile);
        AiProfiles.Remove(profile);
        var next = AiProfiles[Math.Min(index, AiProfiles.Count - 1)];
        ActiveAiProfileId = next.Id;
    }

    public void RefreshDictStatus()
    {
        DictInstalled = _offlineDict.IsInstalled;
        if (DictInstalled)
        {
            var meta = _offlineDict.Metadata;
            DictStatusText = "已安装就绪 (Ready)";
            if (meta != null)
            {
                DictDetailsText = $"词条数: {meta.EntryCount:N0} 条 | 契约版本: {meta.SchemaVersion} | 安装日期: {meta.InstalledAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
            }
            else
            {
                DictDetailsText = "离线词典已挂载。";
            }
        }
        else
        {
            DictStatusText = "未安装离线词典";
            DictDetailsText = "安装后短词优先本地查询；未命中或长句先使用免费机翻，仍不可用时再回退 AI。";
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        var s = _settingsService.Current;
        s.Hotkey = Hotkey.Trim();
        s.ExplainLanguage = ExplainLanguage.Trim();

        SaveEditorToProfile(ActiveAiProfileId);
        s.TranslationProfiles = AiProfiles.Select(CloneProfile).ToList();
        var active = s.TranslationProfiles.FirstOrDefault(p => p.Id == ActiveAiProfileId)
            ?? s.TranslationProfiles.First();
        s.ActiveTranslationProfileId = active.Id;
        // Keep the legacy property in sync for older settings readers and existing files.
        s.Translation = CloneSettings(active);

        s.Dictionary.DownloadUrl = DictDownloadUrl.Trim();

        s.Anki.Enabled = AnkiEnabled;
        s.Anki.Url = AnkiUrl.Trim();
        s.Anki.DeckName = AnkiDeckName.Trim();
        s.Anki.NoteTypeName = AnkiNoteTypeName.Trim();
        s.Anki.ClozeContext = AnkiClozeContext;
        s.Anki.TtsLang = AnkiTtsLang.Trim();

        s.Ui.ClosePopupAfterSave = ClosePopupAfterSave;

        await _settingsService.SaveSettingsAsync(s).ConfigureAwait(true);
        _profileApiKeys[ActiveAiProfileId] = ApiKey.Trim();
        if (_secretStore is IProfileSecretStore profileSecrets)
        {
            foreach (var profile in s.TranslationProfiles)
            {
                _profileApiKeys.TryGetValue(profile.Id, out var key);
                await profileSecrets.SetApiKeyAsync(profile.Id, key ?? string.Empty).ConfigureAwait(true);
            }
        }
        else
        {
            await _secretStore.SetApiKeyAsync(ApiKey.Trim()).ConfigureAwait(true);
        }

        var registered = _hotkeyManager.Register(s.Hotkey);
        if (!registered)
        {
            MessageBox.Show($"快捷键「{s.Hotkey}」可能已被其他应用占用，请尝试更换。", "快捷键注册警告", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show("设置已保存并生效！", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static List<TranslationProfile> EnsureProfiles(AppSettings settings)
    {
        if (settings.TranslationProfiles is { Count: > 0 })
            return settings.TranslationProfiles;

        var legacy = settings.Translation ?? new TranslationSettings();
        return new List<TranslationProfile>
        {
            new()
            {
                Id = "default",
                Name = "默认",
                BaseUrl = legacy.BaseUrl,
                Model = legacy.Model,
                TimeoutSeconds = legacy.TimeoutSeconds,
                MachineTranslationProvider = legacy.MachineTranslationProvider
            }
        };
    }

    private static TranslationProfile CreateDefaultProfile() => new();

    private static TranslationProfile CloneProfile(TranslationProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        BaseUrl = profile.BaseUrl,
        Model = profile.Model,
        TimeoutSeconds = profile.TimeoutSeconds,
        MachineTranslationProvider = profile.MachineTranslationProvider
    };

    private static TranslationSettings CloneSettings(TranslationProfile profile) => new()
    {
        BaseUrl = profile.BaseUrl,
        Model = profile.Model,
        TimeoutSeconds = profile.TimeoutSeconds,
        MachineTranslationProvider = profile.MachineTranslationProvider
    };

    private void LoadProfile(TranslationProfile profile)
    {
        AiProfileName = profile.Name;
        ApiKey = _profileApiKeys.TryGetValue(profile.Id, out var key) ? key : string.Empty;
        ApiBaseUrl = profile.BaseUrl;
        ApiModel = profile.Model;
        ApiTimeoutSeconds = profile.TimeoutSeconds;
        MachineTranslationProvider = string.Equals(profile.MachineTranslationProvider, "google", StringComparison.OrdinalIgnoreCase)
            ? "google"
            : "microsoft";
    }

    private void SaveEditorToProfile(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return;

        var profile = AiProfiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null)
            return;

        profile.BaseUrl = ApiBaseUrl.Trim();
        profile.Model = ApiModel.Trim();
        profile.TimeoutSeconds = ApiTimeoutSeconds > 0 ? ApiTimeoutSeconds : 30;
        profile.MachineTranslationProvider = MachineTranslationProvider.Trim().ToLowerInvariant() is "google"
            ? "google"
            : "microsoft";
        profile.Name = string.IsNullOrWhiteSpace(AiProfileName)
            ? profile.Name
            : AiProfileName.Trim();
        _profileApiKeys[profile.Id] = ApiKey.Trim();
    }

    private async Task<string> ReadProfileApiKeyAsync(string profileId)
    {
        if (_secretStore is IProfileSecretStore profileSecrets)
            return await profileSecrets.GetApiKeyAsync(profileId).ConfigureAwait(true);

        return profileId == ActiveAiProfileId
            ? await _secretStore.GetApiKeyAsync().ConfigureAwait(true)
            : string.Empty;
    }

    [RelayCommand]
    private async Task InstallDictFromUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(DictDownloadUrl) || !Uri.TryCreate(DictDownloadUrl, UriKind.Absolute, out var uri))
        {
            MessageBox.Show("请输入合法的词典下载 URL。", "URL 错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsDictInstalling = true;
        DictProgressText = "准备下载...";
        DictProgressValue = 0;

        var progress = new Progress<DictionaryInstallProgress>(p =>
        {
            DictProgressText = p.Message;
            if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
            {
                DictProgressValue = (double)p.BytesRead / p.TotalBytes.Value * 100.0;
            }
        });

        try
        {
            await _dictInstaller.InstallFromUrlAsync(uri, progress).ConfigureAwait(true);
            RefreshDictStatus();
            MessageBox.Show("Open Dictionary 离线词典安装成功！", "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"词典安装失败: {ex.Message}", "安装错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsDictInstalling = false;
        }
    }

    [RelayCommand]
    private async Task InstallDictFromFileAsync()
    {
        var ofd = new OpenFileDialog
        {
            Filter = "Open Dictionary 归档文件 (*.sqlite.gz;*.sqlite)|*.sqlite.gz;*.sqlite|所有文件 (*.*)|*.*",
            Title = "选择离线词典工件"
        };

        if (ofd.ShowDialog() == true)
        {
            IsDictInstalling = true;
            DictProgressText = "正在解压并校验...";
            DictProgressValue = 0;

            var progress = new Progress<DictionaryInstallProgress>(p =>
            {
                DictProgressText = p.Message;
            });

            try
            {
                await _dictInstaller.InstallFromFileAsync(ofd.FileName, progress).ConfigureAwait(true);
                RefreshDictStatus();
                MessageBox.Show("Open Dictionary 离线词典从本地文件安装成功！", "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"词典安装失败: {ex.Message}", "安装错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsDictInstalling = false;
            }
        }
    }

    [RelayCommand]
    private async Task RemoveDictAsync()
    {
        var confirm = MessageBox.Show(
            "确定要删除当前安装的离线词典吗？（此操作不会影响您已收藏的本地生词库）",
            "删除词典确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            await _dictInstaller.RemoveAsync().ConfigureAwait(true);
            RefreshDictStatus();
            MessageBox.Show("离线词典已移除。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    [RelayCommand]
    private async Task TestAnkiConnectionAsync()
    {
        try
        {
            var version = await _ankiClient.CheckVersionAsync().ConfigureAwait(true);
            MessageBox.Show($"AnkiConnect 连接成功！API 版本号: {version}", "连接正常", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"连接 Anki 失败: {ex.Message}\n请确保 Anki 正在运行且已安装 AnkiConnect 插件。", "连接失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        try
        {
            var sampleCapture = new CaptureResult("devastated", "WordCatcher.exe", "Connection Test", new ScreenPoint(0, 0), DateTimeOffset.UtcNow);
            var res = await _translationService.TranslateWithConfigurationAsync(
                sampleCapture,
                ApiBaseUrl,
                ApiModel,
                ApiTimeoutSeconds,
                ExplainLanguage,
                ApiKey,
                CancellationToken.None).ConfigureAwait(true);

            MessageBox.Show($"AI 翻译接口连通成功！\n测试词: {res.Word}\n释义: {res.Definition}", "测试通过", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"AI 接口连通测试失败:\n{ex.Message}", "测试失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
