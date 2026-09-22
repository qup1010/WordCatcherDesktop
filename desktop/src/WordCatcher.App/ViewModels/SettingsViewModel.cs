using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
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

    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string _statusMessage = "修改后点击保存，设置即可生效。";

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
        Hotkey = "Alt+Q";
        HotkeyHint = "已填入默认 Alt+Q，点击保存后生效。";
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
        OnPropertyChanged(nameof(AppliedHotkey));
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

            s.Dictionary.DownloadUrl = DictDownloadUrl.Trim();

            s.Anki.Enabled = AnkiEnabled;
            s.Anki.Url = AnkiUrl.Trim();
            s.Anki.DeckName = AnkiDeckName.Trim();
            s.Anki.NoteTypeName = AnkiNoteTypeName.Trim();
            s.Anki.ClozeContext = AnkiClozeContext;
            s.Anki.TtsLang = AnkiTtsLang.Trim();

            s.Ui.ClosePopupAfterSave = ClosePopupAfterSave;

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

            await _settingsService.SaveSettingsAsync(s).ConfigureAwait(true);
            OnPropertyChanged(nameof(AppliedHotkey));
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
        if (!HotkeyManager.TryParseHotkey(Hotkey, out _, out _)) return "快捷键格式无效，例如 Alt+Q、Ctrl+Shift+D 或 Alt+F8。";
        if (string.IsNullOrWhiteSpace(ExplainLanguage)) return "请填写释义语言。";
        if (!IsWebUrl(ApiBaseUrl)) return "AI Base URL 必须是有效的 HTTP 或 HTTPS 地址。";
        if (string.IsNullOrWhiteSpace(ApiModel)) return "请填写 AI 模型名称。";
        if (ApiTimeoutSeconds <= 0) return "请求超时必须大于 0 秒。";
        if (AnkiEnabled && (!IsWebUrl(AnkiUrl) || string.IsNullOrWhiteSpace(AnkiDeckName) || string.IsNullOrWhiteSpace(AnkiNoteTypeName)))
            return "请填写有效的 Anki 地址、牌组名称和笔记类型。";
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
        if (IsDictInstalling) return;
        if (string.IsNullOrWhiteSpace(DictDownloadUrl) || !Uri.TryCreate(DictDownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
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
        if (IsDictInstalling) return;
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
        if (!string.Equals(AnkiUrl.Trim(), _settingsService.Current.Anki.Url, StringComparison.Ordinal))
        {
            StatusMessage = "Anki 地址已修改，请先保存再测试连接。";
            return;
        }
        StatusMessage = "正在测试 Anki 连接…";
        try
        {
            var version = await _ankiClient.CheckVersionAsync().ConfigureAwait(true);
            StatusMessage = $"AnkiConnect 连接成功，API 版本 {version}。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"连接 Anki 失败：{ex.Message} 请确认 Anki 与 AnkiConnect 已启动。";
        }
    }

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
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
    }
}
