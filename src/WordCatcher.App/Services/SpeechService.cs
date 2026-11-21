using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using WordCatcher.Core.Helpers;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.App.Services;

/// <summary>
/// Supports optional online human pronunciation with automatic fallback to Windows SAPI TTS
/// and configurable accent (en-US / en-GB / auto).
/// </summary>
public sealed class SpeechService : ISpeechService, IDisposable
{
    private readonly ISettingsService? _settingsService;
    private readonly HttpClient? _httpClient;
    private readonly TimeSpan _onlineTimeout;
    private readonly Action<string, string>? _sapiFallbackOverride;
    private readonly object _sync = new();
    private object? _voice;
    private MediaPlayer? _mediaPlayer;
    private string? _lastAudioPath;
    private CancellationTokenSource? _onlineCts;
    private long _requestVersion;

    public SpeechService(ISettingsService? settingsService = null, HttpClient? httpClient = null)
        : this(settingsService, httpClient, TimeSpan.FromSeconds(3), null)
    {
    }

    internal SpeechService(
        ISettingsService? settingsService,
        HttpClient? httpClient,
        TimeSpan onlineTimeout,
        Action<string, string>? sapiFallbackOverride)
    {
        _settingsService = settingsService;
        _httpClient = httpClient;
        _onlineTimeout = onlineTimeout > TimeSpan.Zero ? onlineTimeout : TimeSpan.FromSeconds(3);
        _sapiFallbackOverride = sapiFallbackOverride;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var trimmed = text.Trim();
        var settings = _settingsService?.Current.Speech ?? new SpeechSettings();
        var accent = SpeechSettings.NormalizeAccent(settings.Accent);

        if (settings.UseOnlineVoice && _httpClient != null && CanUseOnlineVoice(trimmed))
        {
            StartOnlineSpeechWithFallback(trimmed, accent);
            return;
        }

        SpeakWithSapi(trimmed, accent);
    }

    internal static bool CanUseOnlineVoice(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 80 && TextLanguageDetector.IsEnglishText(text);

    internal static string BuildOnlineVoiceUrl(string text, string accent)
    {
        var type = string.Equals(SpeechSettings.NormalizeAccent(accent), SpeechSettings.AccentGb, StringComparison.OrdinalIgnoreCase)
            ? 1
            : 2;
        return $"https://dict.youdao.com/dictvoice?audio={Uri.EscapeDataString(text)}&type={type}";
    }

    private void StartOnlineSpeechWithFallback(string text, string accent)
    {
        long version;
        CancellationTokenSource requestCts;
        lock (_sync)
        {
            version = ++_requestVersion;
            CancelOnlineCore();
            StopCore();
            requestCts = new CancellationTokenSource();
            _onlineCts = requestCts;
        }

        _ = Task.Run(async () =>
        {
            string? tempFile = null;
            var handedOffToPlayer = false;
            try
            {
                using var timeoutCts = new CancellationTokenSource(_onlineTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token, timeoutCts.Token);

                var url = BuildOnlineVoiceUrl(text, accent);
                using var response = await _httpClient!.GetAsync(url, linkedCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

                var bytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token).ConfigureAwait(false);
                if (bytes.Length < 128)
                    throw new InvalidDataException("Empty audio response.");

                tempFile = Path.Combine(Path.GetTempPath(), $"wc-tts-{Guid.NewGuid():N}.mp3");
                await File.WriteAllBytesAsync(tempFile, bytes, linkedCts.Token).ConfigureAwait(false);

                lock (_sync)
                {
                    if (version != _requestVersion || requestCts.IsCancellationRequested)
                        return;
                }

                handedOffToPlayer = PlayAudioFile(tempFile, text, accent, version, requestCts.Token);
            }
            catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
            {
                // Replaced by a newer speak/stop request.
            }
            catch
            {
                FallbackToSapiIfCurrent(version, text, accent);
            }
            finally
            {
                if (!handedOffToPlayer && tempFile != null)
                    TryDeleteFile(tempFile);
            }
        });
    }

    private bool PlayAudioFile(string path, string fallbackText, string accent, long version, CancellationToken ct)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            FallbackToSapiIfCurrent(version, fallbackText, accent);
            return false;
        }

        dispatcher.BeginInvoke(() =>
        {
            lock (_sync)
            {
                if (version != _requestVersion || ct.IsCancellationRequested)
                {
                    TryDeleteFile(path);
                    return;
                }
            }

            try
            {
                StopMediaPlayerOnUiThread(expectedPlayer: null);
                var player = new MediaPlayer();
                player.MediaEnded += (_, _) => StopMediaPlayerOnUiThread(expectedPlayer: player);
                player.MediaFailed += (_, _) =>
                {
                    StopMediaPlayerOnUiThread(expectedPlayer: player);
                    FallbackToSapiIfCurrent(version, fallbackText, accent);
                };
                player.Open(new Uri(path, UriKind.Absolute));
                player.Play();

                var shouldAbort = false;
                lock (_sync)
                {
                    if (version != _requestVersion || ct.IsCancellationRequested)
                    {
                        shouldAbort = true;
                    }
                    else
                    {
                        _mediaPlayer = player;
                        _lastAudioPath = path;
                    }
                }
                if (shouldAbort)
                {
                    try { player.Stop(); player.Close(); } catch { }
                    TryDeleteFile(path);
                }
            }
            catch
            {
                TryDeleteFile(path);
                FallbackToSapiIfCurrent(version, fallbackText, accent);
            }
        });

        return true;
    }

    private void FallbackToSapiIfCurrent(long expectedVersion, string text, string accent)
    {
        lock (_sync)
        {
            if (expectedVersion != _requestVersion)
                return;
            try
            {
                SpeakWithSapiLocked(text, accent);
            }
            catch
            {
                // Fallback is best-effort on background threads.
            }
        }
    }

    private void SpeakWithSapi(string text, string accent)
    {
        lock (_sync)
        {
            ++_requestVersion;
            CancelOnlineCore();
            SpeakWithSapiLocked(text, accent);
        }
    }

    private void SpeakWithSapiLocked(string text, string accent)
    {
        if (_sapiFallbackOverride != null)
        {
            _sapiFallbackOverride(text, accent);
            return;
        }

        try
        {
            StopCore();
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null)
                throw new PlatformNotSupportedException("当前 Windows 未提供 SAPI 语音服务。");

            _voice = Activator.CreateInstance(type);
            dynamic voice = _voice!;
            voice.Rate = 0;
            TrySelectVoiceAccent(voice, accent);
            voice.Speak(text, 1); // SVSFlagsAsync
        }
        catch
        {
            ReleaseVoice();
            throw;
        }
    }

    private static void TrySelectVoiceAccent(dynamic voice, string accent)
    {
        var targetAttr = accent switch
        {
            SpeechSettings.AccentGb => "Language=809",
            SpeechSettings.AccentUs => "Language=409",
            _ => null
        };
        if (targetAttr == null)
            return;

        try
        {
            dynamic? voices = voice.GetVoices(targetAttr);
            if (voices is not null && (int)voices.Count > 0)
            {
                voice.Voice = voices.Item(0);
            }
        }
        catch
        {
            // Keep default system voice if target accent voice pack is not installed.
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            ++_requestVersion;
            CancelOnlineCore();
            StopCore();
            ReleaseVoice();
        }
    }

    private void CancelOnlineCore()
    {
        if (_onlineCts != null)
        {
            try { _onlineCts.Cancel(); } catch { }
            _onlineCts.Dispose();
            _onlineCts = null;
        }
        StopMediaPlayerAsync();
    }

    private void StopMediaPlayerAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) StopMediaPlayerOnUiThread(expectedPlayer: null);
        else dispatcher.BeginInvoke(() => StopMediaPlayerOnUiThread(expectedPlayer: null));
    }

    private void StopMediaPlayerOnUiThread(MediaPlayer? expectedPlayer)
    {
        MediaPlayer? player;
        string? oldPath;
        lock (_sync)
        {
            if (expectedPlayer != null && !ReferenceEquals(_mediaPlayer, expectedPlayer))
                return;
            player = _mediaPlayer;
            oldPath = _lastAudioPath;
            _mediaPlayer = null;
            _lastAudioPath = null;
        }
        if (player != null)
        {
            try { player.Stop(); player.Close(); } catch { }
        }
        if (oldPath != null) TryDeleteFile(oldPath);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private void StopCore()
    {
        if (_voice != null)
        {
            try { ((dynamic)_voice).Speak(string.Empty, 2); } catch { }
        }
    }

    private void ReleaseVoice()
    {
        if (_voice is not null && Marshal.IsComObject(_voice))
        {
            try { Marshal.FinalReleaseComObject(_voice); } catch { }
        }
        _voice = null;
    }

    public void Dispose() => Stop();
}
