using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Text.Json;
using System.Windows.Input;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WordCatcher.App.Services;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Sync;
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
    private readonly UpdateCheckService? _updateCheckService;
    private readonly IAppUpdater? _appUpdater;
    public event Action? UpdateInstallRequested;
    private readonly IWebDavSyncService? _webDavSyncService;

    [ObservableProperty]
    private string _hotkey = AppSettings.DefaultHotkey;

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
    private bool _webDavEnabled;

    [ObservableProperty]
    private string _webDavUrl = string.Empty;

    [ObservableProperty]
    private string _webDavUsername = string.Empty;

    [ObservableProperty]
    private string _webDavPassword = string.Empty;

    [ObservableProperty]
    private string _webDavRemotePath = WebDavSyncService.DefaultRemotePath;

    [ObservableProperty]
    private bool _webDavAutoSync = true;

    [ObservableProperty]
    private bool _closePopupAfterSave = true;

    [ObservableProperty]
    private bool _closePopupOnDeactivate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DraftStatusText))]
    private bool _hasUnsavedChanges;
    public string DraftStatusText => HasUnsavedChanges ? "有未保存的修改" : "设置已保存";
    private string? _savedEditorState;
    private bool _initializing;
    private static readonly HashSet<string> EditableProperties = new()
    {
        nameof(Hotkey), nameof(ExplainLanguage), nameof(DictDownloadUrl), nameof(ApiBaseUrl),
        nameof(ApiModel), nameof(ApiTimeoutSeconds), nameof(ActiveAiProfileId), nameof(AiProfileName),
        nameof(MachineTranslationProvider), nameof(ApiKey), nameof(AnkiEnabled), nameof(AnkiUrl),
        nameof(AnkiDeckName), nameof(AnkiNoteTypeName), nameof(AnkiClozeContext), nameof(AnkiTtsLang),
        nameof(WebDavEnabled), nameof(WebDavUrl), nameof(WebDavUsername), nameof(WebDavPassword),
        nameof(WebDavRemotePath), nameof(WebDavAutoSync),
        nameof(ClosePopupAfterSave), nameof(ClosePopupOnDeactivate)
    };

    private string EditorState() => JsonSerializer.Serialize(new
    {
        Hotkey, DictDownloadUrl, ApiBaseUrl, ApiModel, ApiTimeoutSeconds,
        ActiveAiProfileId, AiProfileName, ApiKey, AnkiEnabled,
        AnkiUrl, AnkiDeckName, AnkiNoteTypeName, AnkiClozeContext, AnkiTtsLang,
        WebDavEnabled, WebDavUrl, WebDavUsername, WebDavPassword, WebDavRemotePath, WebDavAutoSync,
        Profiles = AiProfiles, Keys = _profileApiKeys
    });

    public Task PreferenceSaveTask { get; private set; } = Task.CompletedTask;
    private void RefreshUnsavedChanges()
    {
        var current = _settingsService.Current;
        HasUnsavedChanges = EditorState() != _savedEditorState
            || ExplainLanguage.Trim() != current.ExplainLanguage
            || MachineTranslationProvider != current.Translation.MachineTranslationProvider
            || ClosePopupAfterSave != current.Ui.ClosePopupAfterSave
            || ClosePopupOnDeactivate != current.Ui.ClosePopupOnDeactivate;
    }

    private void SavePreferences()
    {
        var language = ExplainLanguage.Trim();
        if (string.IsNullOrWhiteSpace(language)) { StatusMessage = "请填写目标语言。"; return; }
        var provider = MachineTranslationProvider;
        var closeAfterSave = ClosePopupAfterSave;
        var closeOnDeactivate = ClosePopupOnDeactivate;
        var previous = PreferenceSaveTask;
        PreferenceSaveTask = PersistAsync();
        async Task PersistAsync()
        {
            await previous.ConfigureAwait(true);
            try
            {
                var current = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settingsService.Current))!;
                current.ExplainLanguage = language;
                current.Translation.MachineTranslationProvider = provider;
                current.Ui.ClosePopupAfterSave = closeAfterSave;
                current.Ui.ClosePopupOnDeactivate = closeOnDeactivate;
                await _settingsService.SaveSettingsAsync(current).ConfigureAwait(true);
                StatusMessage = "普通偏好已保存。";
            }
            catch (Exception) { StatusMessage = "普通偏好保存失败，请点击保存配置重试；输入内容已保留。"; }
            finally { RefreshUnsavedChanges(); }
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_initializing && _savedEditorState != null && e.PropertyName != null && EditableProperties.Contains(e.PropertyName))
        {
            RefreshUnsavedChanges();
            if (e.PropertyName is nameof(ExplainLanguage) or nameof(MachineTranslationProvider) or nameof(ClosePopupAfterSave) or nameof(ClosePopupOnDeactivate)) SavePreferences();
        }
    }

    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isTestingAi;
    [ObservableProperty] private bool _isTestingAnki;
    [ObservableProperty] private bool _isTestingWebDav;
    [ObservableProperty] private bool _isSyncingWebDav;
    [ObservableProperty] private string _dictFeedback = string.Empty;
    [ObservableProperty] private bool _isDictProgressIndeterminate;
    private CancellationTokenSource? _dictionaryInstallCancellation;

    [RelayCommand]
    private void CancelDictionaryInstall()
    {
        _dictionaryInstallCancellation?.Cancel();
        DictProgressText = "正在取消…";
    }
    [ObservableProperty] private string _statusMessage = "普通偏好即时保存；连接配置与快捷键需点击保存。";

    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _hasCheckedForUpdates;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private int _updateProgress;
    [ObservableProperty] private string _updateStatus = "尚未检查更新。";
    [ObservableProperty] private string _latestReleaseUrl = UpdateCheckService.RepositoryUrl + "/releases/latest";

    public string AppVersion => UpdateCheckService.CurrentVersion;
    public string ProjectUrl => UpdateCheckService.RepositoryUrl;

    public string AppliedHotkey => _settingsService.Current.Hotkey;
    [ObservableProperty] private bool _isRecordingHotkey;
    [ObservableProperty] private string _hotkeyHint = "点击录入，然后按下你想使用的组合键。";
    private bool _hotkeyWasEnabled;

    [RelayCommand]
    private void RecordHotkey()
    {
        if (IsSaving || IsRecordingHotkey) return;
        _hotkeyWasEnabled = _hotkeyManager.IsEnabled;
        _hotkeyManager.SetEnabled(false);
        IsRecordingHotkey = true;
        HotkeyHint = "正在录入… 按组合键；Esc 取消，Tab 离开。";
    }

    [RelayCommand]
    public void CancelHotkeyRecording()
    {
        if (!IsRecordingHotkey) return;
        IsRecordingHotkey = false;
        _hotkeyManager.SetEnabled(_hotkeyWasEnabled);
        HotkeyHint = "已取消录入，快捷键未更改。";
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        CancelHotkeyRecording();
        Hotkey = AppSettings.DefaultHotkey;
        HotkeyHint = $"已填入默认 {AppSettings.DefaultHotkey}，点击保存后生效。";
    }

    public void CaptureHotkey(Key key, ModifierKeys modifiers)
    {
        if (!IsRecordingHotkey) return;
        if (key == Key.Escape || key == Key.Tab) { CancelHotkeyRecording(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (!TryFormatHotkey(key, modifiers, out var text))
        {
            HotkeyHint = "请使用 Ctrl / Alt / Shift / Win + 字母、数字或 F1–F24。";
            return;
        }
        CancelHotkeyRecording();
        Hotkey = text;
        HotkeyHint = $"已录入 {text}，点击保存后生效；如被占用会保留原快捷键。";
    }

    public static bool TryFormatHotkey(Key key, ModifierKeys modifiers, out string text)
    {
        text = string.Empty;
        string main;
        if (key >= Key.A && key <= Key.Z) main = key.ToString();
        else if (key >= Key.D0 && key <= Key.D9) main = ((int)key - (int)Key.D0).ToString();
        else if (key >= Key.F1 && key <= Key.F24) main = key.ToString();
        else return false;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return false;
        parts.Add(main);
        text = string.Join("+", parts);
        return true;
    }

    private bool _profileEditorReady;
    private string _editingProfileId = string.Empty;
    private readonly Dictionary<string, string> _profileApiKeys = new(StringComparer.Ordinal);
    private string _webDavPasswordFallback = string.Empty;

    public SettingsViewModel(
        ISettingsService settingsService,
        ISecretStore secretStore,
        IOfflineDictionaryService offlineDict,
        IDictionaryInstaller dictInstaller,
        IAnkiClient ankiClient,
        OpenAiTranslationService translationService,
        HotkeyManager hotkeyManager,
        UpdateCheckService? updateCheckService = null,
        IWebDavSyncService? webDavSyncService = null,
        IAppUpdater? appUpdater = null)
    {
        _settingsService = settingsService;
        _secretStore = secretStore;
        _offlineDict = offlineDict;
        _dictInstaller = dictInstaller;
        _ankiClient = ankiClient;
        _translationService = translationService;
        _hotkeyManager = hotkeyManager;
        _updateCheckService = updateCheckService;
        _appUpdater = appUpdater;
        IsUpdateReady = appUpdater?.IsUpdateReady ?? false;
        _webDavSyncService = webDavSyncService;
    }

    public async Task InitializeAsync()
    {
        if (HasUnsavedChanges) return;
        _initializing = true;
        try
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
            MachineTranslationProvider = s.Translation.MachineTranslationProvider;
            _editingProfileId = active.Id;
            _profileEditorReady = true;

            DictDownloadUrl = s.Dictionary.DownloadUrl;

            AnkiEnabled = s.Anki.Enabled;
            AnkiUrl = s.Anki.Url;
            AnkiDeckName = s.Anki.DeckName;
            AnkiNoteTypeName = s.Anki.NoteTypeName;
            AnkiClozeContext = s.Anki.ClozeContext;
            AnkiTtsLang = s.Anki.TtsLang;

            var webDav = s.WebDav ?? new WebDavSettings();
            WebDavEnabled = webDav.Enabled;
            WebDavUrl = webDav.Url;
            WebDavUsername = webDav.Username;
            WebDavRemotePath = string.IsNullOrWhiteSpace(webDav.RemotePath)
                ? WebDavSyncService.DefaultRemotePath
                : webDav.RemotePath;
            WebDavAutoSync = webDav.AutoSync;
            WebDavPassword = _secretStore is IProfileSecretStore profileSecrets
                ? await profileSecrets.GetApiKeyAsync(WebDavSyncService.SecretStoreKey).ConfigureAwait(true)
                : _webDavPasswordFallback;

            ClosePopupAfterSave = s.Ui.ClosePopupAfterSave;
            ClosePopupOnDeactivate = s.Ui.ClosePopupOnDeactivate;

            RefreshDictStatus();
            OnPropertyChanged(nameof(AppliedHotkey));
            _savedEditorState = EditorState();
        }
        finally { _initializing = false; }
        HasUnsavedChanges = false;
    }

    [RelayCommand]
    private async Task DiscardChangesAsync()
    {
        if (IsSaving || IsTestingAi || IsTestingAnki || IsTestingWebDav || IsSyncingWebDav || _initializing) return;
        CancelHotkeyRecording();
        HasUnsavedChanges = false;
        try
        {
            await PreferenceSaveTask.ConfigureAwait(true);
            HasUnsavedChanges = false;
            await InitializeAsync().ConfigureAwait(true);
            StatusMessage = "未保存的修改已放弃，已恢复当前生效的设置。";
        }
        catch (Exception)
        {
            HasUnsavedChanges = true;
            StatusMessage = "恢复设置失败，请稍后重试。";
        }
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
    private void ApplyAiPreset(string? presetKey)
    {
        if (string.IsNullOrWhiteSpace(presetKey)) return;
        var (name, url, model) = presetKey.ToLowerInvariant() switch
        {
            "openai" => ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
            "deepseek" => ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
            "kimi" => ("Kimi", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
            "zhipu" => ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
            "siliconflow" => ("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
            "ollama" => ("本地 Ollama", "http://127.0.0.1:11434/v1", "qwen2.5"),
            _ => ("", "", "")
        };
        if (string.IsNullOrEmpty(url)) return;
        ApiBaseUrl = url;
        ApiModel = model;
        if (AiProfileName == "默认" || AiProfileName.StartsWith("配置 ", StringComparison.Ordinal))
        {
            AiProfileName = name;
        }
        StatusMessage = $"已填入 {name} 预设地址与推荐模型（{model}），填写 API Key 并保存后即可生效。";
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
            DictStatusText = "已安装";
            if (meta != null)
            {
                DictDetailsText = $"{meta.EntryCount:N0} 条词条 · 安装于 {meta.InstalledAtUtc.ToLocalTime():yyyy-MM-dd}";
            }
            else
            {
                DictDetailsText = "可以离线查词。";
            }
        }
        else
        {
            DictStatusText = "未安装离线词典";
            DictDetailsText = "安装后单词优先查询本地词典；未命中或输入句子时使用在线快译，AI 由你主动调用。";
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (IsSaving) return;
        if (IsRecordingHotkey) { StatusMessage = "请先完成或取消快捷键录入。"; return; }
        var error = ValidateEditor();
        if (error != null) { StatusMessage = error; return; }
        IsSaving = true;
        StatusMessage = "正在保存…";
        var oldHotkey = _settingsService.Current.Hotkey;
        var hotkeyChanged = !string.Equals(oldHotkey, Hotkey.Trim(), StringComparison.OrdinalIgnoreCase);
        try
        {
            await PreferenceSaveTask.ConfigureAwait(true);
            if (hotkeyChanged && !_hotkeyManager.Register(Hotkey.Trim()))
            {
                _hotkeyManager.Register(oldHotkey);
                StatusMessage = "快捷键已被占用，未保存设置；请更换组合键。";
                HotkeyHint = $"无法使用 {Hotkey}，当前仍使用 {oldHotkey}。请重新录入。";
                return;
            }
            var s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settingsService.Current))!;
            s.Hotkey = Hotkey.Trim();
            s.ExplainLanguage = ExplainLanguage.Trim();

            SaveEditorToProfile(ActiveAiProfileId);
            s.TranslationProfiles = AiProfiles.Select(CloneProfile).ToList();
            var active = s.TranslationProfiles.FirstOrDefault(p => p.Id == ActiveAiProfileId)
                ?? s.TranslationProfiles.First();
            s.ActiveTranslationProfileId = active.Id;
            // Keep the legacy property in sync for older settings readers and existing files.
            s.Translation = CloneSettings(active);
            s.Translation.MachineTranslationProvider = MachineTranslationProvider;

            s.Dictionary.DownloadUrl = DictDownloadUrl.Trim();

            s.Anki.Enabled = AnkiEnabled;
            s.Anki.Url = AnkiUrl.Trim();
            s.Anki.DeckName = AnkiDeckName.Trim();
            s.Anki.NoteTypeName = AnkiNoteTypeName.Trim();
            s.Anki.ClozeContext = AnkiClozeContext;
            s.Anki.TtsLang = AnkiTtsLang.Trim();

            s.WebDav ??= new WebDavSettings();
            s.WebDav.Enabled = WebDavEnabled;
            s.WebDav.Url = WebDavUrl.Trim();
            s.WebDav.Username = WebDavUsername.Trim();
            s.WebDav.RemotePath = string.IsNullOrWhiteSpace(WebDavRemotePath)
                ? WebDavSyncService.DefaultRemotePath
                : WebDavRemotePath.Trim();
            s.WebDav.AutoSync = WebDavAutoSync;

            s.Ui.ClosePopupAfterSave = ClosePopupAfterSave;
            s.Ui.ClosePopupOnDeactivate = ClosePopupOnDeactivate;

            _profileApiKeys[ActiveAiProfileId] = ApiKey.Trim();
            _webDavPasswordFallback = WebDavPassword;
            if (_secretStore is IProfileSecretStore profileSecrets)
            {
                foreach (var profile in s.TranslationProfiles)
                {
                    _profileApiKeys.TryGetValue(profile.Id, out var key);
                    await profileSecrets.SetApiKeyAsync(profile.Id, key ?? string.Empty).ConfigureAwait(true);
                }
                await profileSecrets.SetApiKeyAsync(WebDavSyncService.SecretStoreKey, WebDavPassword).ConfigureAwait(true);
            }
            else
            {
                await _secretStore.SetApiKeyAsync(ApiKey.Trim()).ConfigureAwait(true);
            }

            await _settingsService.SaveSettingsAsync(s).ConfigureAwait(true);
            OnPropertyChanged(nameof(AppliedHotkey));
            _savedEditorState = EditorState();
            HasUnsavedChanges = false;
            StatusMessage = "设置已保存并生效。";
            HotkeyHint = $"当前快捷键：{s.Hotkey}。";
        }
        catch (Exception)
        {
            if (hotkeyChanged) _hotkeyManager.Register(oldHotkey);
            StatusMessage = "设置未完整保存，请检查文件权限后重试；输入内容已保留。";
        }
        finally { IsSaving = false; }
    }

    private string? ValidateEditor()
    {
        if (!HotkeyManager.TryParseHotkey(Hotkey, out _, out _)) return "快捷键格式无效，例如 Ctrl+Shift+Z、Ctrl+Shift+D 或 Alt+F8。";
        if (string.IsNullOrWhiteSpace(ExplainLanguage)) return "请填写释义语言。";
        if (!IsWebUrl(ApiBaseUrl)) return "AI Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
        if (string.IsNullOrWhiteSpace(ApiModel)) return "请填写 AI 模型名称。";
        if (ApiTimeoutSeconds <= 0) return "请求超时必须大于 0 秒。";
        if (AnkiEnabled && (!IsWebUrl(AnkiUrl) || string.IsNullOrWhiteSpace(AnkiDeckName) || string.IsNullOrWhiteSpace(AnkiNoteTypeName)))
            return "请填写有效的 Anki 地址、牌组名称和笔记类型。";
        if (WebDavEnabled && (!(Uri.TryCreate(WebDavUrl.Trim(), UriKind.Absolute, out var webDavUri) && webDavUri.Scheme == "https") || string.IsNullOrWhiteSpace(WebDavRemotePath)))
            return "启用 WebDAV 同步时，请填写 HTTPS 服务器地址和同步文件路径。";
        return null;
    }

    private static bool IsWebUrl(string text) => Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

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
        if (IsDictInstalling) return;
        if (string.IsNullOrWhiteSpace(DictDownloadUrl) || !Uri.TryCreate(DictDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            DictFeedback = "请填写有效的词典下载地址（HTTP 或 HTTPS）。";
            return;
        }

        IsDictInstalling = true;
        using var installCancellation = new CancellationTokenSource();
        _dictionaryInstallCancellation = installCancellation;
        IsDictProgressIndeterminate = true;
        DictFeedback = string.Empty;
        DictProgressText = "准备下载…";
        DictProgressValue = 0;

        var progress = new Progress<DictionaryInstallProgress>(p =>
        {
            if (!ReferenceEquals(_dictionaryInstallCancellation, installCancellation)) return;
            DictProgressText = p.Message;
            IsDictProgressIndeterminate = !p.TotalBytes.HasValue || p.TotalBytes.Value <= 0;
            if (p.TotalBytes.HasValue && p.TotalBytes.Value > 0)
            {
                DictProgressValue = (double)p.BytesRead / p.TotalBytes.Value * 100.0;
            }
        });

        try
        {
            await _dictInstaller.InstallFromUrlAsync(uri, progress, installCancellation.Token).ConfigureAwait(true);
            RefreshDictStatus();
            DictFeedback = "词典已安装，可以离线查词。";
        }
        catch (OperationCanceledException) when (installCancellation.IsCancellationRequested)
        {
            DictFeedback = "已取消安装。";
        }
        catch (Exception ex) { DictFeedback = $"词典安装失败：{ex.Message}"; }
        finally
        {
            _dictionaryInstallCancellation = null;
            IsDictInstalling = false;
        }
    }

    [RelayCommand]
    private async Task InstallDictFromFileAsync()
    {
        if (IsDictInstalling) return;
        var ofd = new OpenFileDialog
        {
            Filter = "Open Dictionary 归档文件 (*.sqlite.gz;*.sqlite)|*.sqlite.gz;*.sqlite|所有文件 (*.*)|*.*",
            Title = "选择离线词典文件"
        };

        if (ofd.ShowDialog() == true)
        {
            IsDictInstalling = true;
            using var installCancellation = new CancellationTokenSource();
            _dictionaryInstallCancellation = installCancellation;
            IsDictProgressIndeterminate = true;
            DictFeedback = string.Empty;
            DictProgressText = "正在解压并校验…";
            DictProgressValue = 0;

            var progress = new Progress<DictionaryInstallProgress>(p =>
            {
                if (!ReferenceEquals(_dictionaryInstallCancellation, installCancellation)) return;
                DictProgressText = p.Message;
            });

            try
            {
                await _dictInstaller.InstallFromFileAsync(ofd.FileName, progress, installCancellation.Token).ConfigureAwait(true);
                RefreshDictStatus();
                DictFeedback = "词典已安装，可以离线查词。";
            }
            catch (OperationCanceledException) when (installCancellation.IsCancellationRequested)
            {
                DictFeedback = "已取消安装。";
            }
            catch (Exception ex) { DictFeedback = $"词典安装失败：{ex.Message}"; }
            finally
            {
                _dictionaryInstallCancellation = null;
                IsDictInstalling = false;
            }
        }
    }

    [RelayCommand]
    private async Task RemoveDictAsync()
    {
        if (IsDictInstalling) return;
        var confirm = MessageBox.Show(
            "确定要删除当前安装的离线词典吗？（此操作不会影响您已收藏的本地生词库）",
            "删除词典确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            IsDictInstalling = true;
            try
            {
                await _dictInstaller.RemoveAsync().ConfigureAwait(true);
                RefreshDictStatus();
                StatusMessage = "离线词典已移除，收藏的词条不受影响。";
            }
            catch (Exception) { StatusMessage = "词典移除失败，请稍后重试。"; }
            finally { IsDictInstalling = false; }
        }
    }

    [RelayCommand]
    private async Task TestAnkiConnectionAsync()
    {
        if (IsTestingAnki) return;
        if (!IsWebUrl(AnkiUrl)) { StatusMessage = "请填写有效的 Anki 地址。"; return; }
        StatusMessage = "正在测试 Anki 连接…";
        IsTestingAnki = true;
        try
        {
            var version = await _ankiClient.CheckVersionAtAsync(AnkiUrl.Trim()).ConfigureAwait(true);
            StatusMessage = $"AnkiConnect 连接成功，API 版本 {version}。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接 Anki 失败：{ex.Message} 请确认 Anki 与 AnkiConnect 已启动。";
        }
        finally { IsTestingAnki = false; }
    }

    [RelayCommand]
    private async Task TestWebDavConnectionAsync()
    {
        if (IsTestingWebDav || IsSyncingWebDav) return;
        if (_webDavSyncService == null)
        {
            StatusMessage = "WebDAV 同步服务未初始化。";
            return;
        }

        IsTestingWebDav = true;
        StatusMessage = "正在测试 WebDAV 连接…";
        try
        {
            var (exists, fileUrl) = await _webDavSyncService.TestConnectionAsync(
                WebDavUrl,
                WebDavRemotePath,
                WebDavUsername,
                WebDavPassword).ConfigureAwait(true);

            StatusMessage = exists
                ? $"WebDAV 连接成功，已检测到远端同步文件（{fileUrl}）。"
                : $"WebDAV 连接成功，远端目录就绪，首次同步将创建 {fileUrl}。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"WebDAV 连接测试失败：{ex.Message}";
        }
        finally
        {
            IsTestingWebDav = false;
        }
    }

    [RelayCommand]
    private async Task SyncWebDavNowAsync()
    {
        if (IsSyncingWebDav || IsTestingWebDav) return;
        if (_webDavSyncService == null)
        {
            StatusMessage = "WebDAV 同步服务未初始化。";
            return;
        }
        if (HasUnsavedChanges)
        {
            await SaveSettingsAsync().ConfigureAwait(true);
            if (HasUnsavedChanges) return;
        }

        IsSyncingWebDav = true;
        StatusMessage = "正在与 WebDAV 云端同步生词库…";
        try
        {
            var res = await _webDavSyncService.SyncAsync(forcePush: true).ConfigureAwait(true);
            var parts = new List<string>();
            if (res.Added > 0) parts.Add($"新增 {res.Added} 条");
            if (res.Updated > 0) parts.Add($"更新 {res.Updated} 条");
            if (res.Removed > 0) parts.Add($"删除 {res.Removed} 条");
            var summary = parts.Count > 0 ? string.Join("，", parts) : "云端与本地数据已一致";
            StatusMessage = $"WebDAV 同步完成：{summary}（当前共 {res.Total} 个词条）。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"WebDAV 同步失败：{ex.Message}";
        }
        finally
        {
            IsSyncingWebDav = false;
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckForUpdatesAsync(CancellationToken ct)
    {
        if (IsCheckingForUpdates) return;
        HasCheckedForUpdates = true;
        if (_updateCheckService == null)
        {
            UpdateStatus = "更新检查服务不可用。";
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatus = "正在检查 GitHub Release…";
        try
        {
            var result = await _updateCheckService.CheckAsync(ct).ConfigureAwait(true);
            LatestReleaseUrl = result.ReleaseUrl;
            if (!result.IsUpdateAvailable)
            {
                UpdateStatus = $"当前已是最新版本（{result.CurrentVersion}）。";
                return;
            }
            if (_appUpdater?.IsInstalled != true)
            {
                UpdateStatus = $"发现新版本 {result.LatestVersion}。请从下载页面安装 Setup 版本，以后可在应用内更新。";
                return;
            }
            UpdateProgress = 0;
            if (string.IsNullOrWhiteSpace(result.InstallerDownloadUrl))
            {
                UpdateStatus = "最新版本尚未提供安装包，请打开下载页面获取 Setup 版本。";
                return;
            }

            UpdateStatus = $"正在下载 {result.LatestVersion} 安装包…";
            IProgress<int> progress = new Progress<int>(value => UpdateProgress = value);
            IsUpdateReady = await _appUpdater.DownloadLatestAsync(
                new Uri(result.InstallerDownloadUrl), result.LatestVersion, progress.Report, ct).ConfigureAwait(true);
            UpdateStatus = IsUpdateReady
                ? $"{result.LatestVersion} 安装包已下载。保存编辑后，点击“安装更新”打开安装向导。"
                : "新版本安装包尚未就绪，请稍后重新检查或打开下载页面。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            UpdateStatus = "更新下载已取消，可稍后重新检查。";
        }
        catch (InvalidOperationException ex)
        {
            UpdateStatus = ex.Message;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            UpdateStatus = "更新包尚未发布完整，请稍后重试或打开下载页面。";
        }
        catch (Exception)
        {
            UpdateStatus = "检查或下载更新失败，请检查网络连接后重试。";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (!IsUpdateReady || IsCheckingForUpdates) return;
        if (HasUnsavedChanges)
        {
            UpdateStatus = "请先保存或放弃设置修改，再打开安装向导。";
            return;
        }
        UpdateInstallRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenProjectPage()
    {
        OpenUrl(ProjectUrl);
    }

    [RelayCommand]
    private void OpenLatestReleasePage()
    {
        OpenUrl(LatestReleaseUrl);
    }

    private void OpenUrl(string url)
    {
        try
        {
            UpdateCheckService.OpenUrl(url);
        }
        catch (Exception)
        {
            UpdateStatus = "无法打开浏览器，请手动访问项目页面。";
        }
    }

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        if (IsTestingAi) return;
        IsTestingAi = true;
        StatusMessage = "正在测试当前 AI 配置…";
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

            StatusMessage = $"AI 连接成功，测试词：{res.Word}。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI 连接测试失败：{ex.Message}";
        }
        finally { IsTestingAi = false; }
    }
}
