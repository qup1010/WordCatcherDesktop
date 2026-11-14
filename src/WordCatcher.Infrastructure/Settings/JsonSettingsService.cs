using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;
using WordCatcher.Infrastructure.Storage;

namespace WordCatcher.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private readonly ILogger<JsonSettingsService>? _logger;
    private readonly object _lock = new();
    private AppSettings _current;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _recoveredFromBackup;

    public event Action<AppSettings>? SettingsChanged;

    public JsonSettingsService(string? customPath = null, ILogger<JsonSettingsService>? logger = null)
    {
        _logger = logger;
        if (string.IsNullOrEmpty(customPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "WordCatcher");
            Directory.CreateDirectory(dir);
            _settingsPath = Path.Combine(dir, "settings.json");
        }
        else
        {
            var dir = Path.GetDirectoryName(customPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _settingsPath = customPath;
        }

        _current = LoadSettingsSync();
    }

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default)
    {
        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var loaded = LoadSettingsSync();
            lock (_lock) { _current = loaded; }
            return loaded;
        }
        finally { _ioGate.Release(); }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await _ioGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFileWriter.WriteTextAsync(_settingsPath, json, ct, !_recoveredFromBackup).ConfigureAwait(false);
            _recoveredFromBackup = false;
            lock (_lock) { _current = settings; }
        }
        finally { _ioGate.Release(); }
        SettingsChanged?.Invoke(settings);
    }

    private AppSettings LoadSettingsSync()
    {
        _recoveredFromBackup = false;
        foreach (var path in new[] { _settingsPath, _settingsPath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded == null) throw new JsonException("设置文件为空。");
                _recoveredFromBackup = path != _settingsPath;
                return loaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger?.LogWarning(ex, "Failed to parse settings file {Path}, trying backup", path);
            }
        }
        var defaults = new AppSettings();
        if (!File.Exists(_settingsPath) && !File.Exists(_settingsPath + ".bak"))
        {
            try
            {
                var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
                AtomicFileWriter.WriteTextAsync(_settingsPath, json).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write default settings");
            }
        }
        return defaults;
    }
}
