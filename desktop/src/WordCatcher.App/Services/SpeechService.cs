using System;
using System.Runtime.InteropServices;

namespace WordCatcher.App.Services;

/// <summary>
/// Uses the Windows SAPI COM voice already present on Windows. This keeps
/// desktop TTS optional and avoids shipping another speech runtime.
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly object _sync = new();
    private object? _voice;

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_sync)
        {
            try
            {
                StopCore();
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type == null)
                    throw new PlatformNotSupportedException("当前 Windows 未提供 SAPI 语音服务。");

                _voice = Activator.CreateInstance(type);
                ((dynamic)_voice!).Speak(text, 1); // SVSFlagsAsync
            }
            catch
            {
                ReleaseVoice();
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
            ReleaseVoice();
        }
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
