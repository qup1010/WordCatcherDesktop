using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Settings;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string _settingsPath;
    private readonly ILogger<JsonSettingsService>? _logger;
    private readonly object _lock = new();
    private AppSettings _current;

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
        if (!File.Exists(_settingsPath))
        {
            var defaults = new AppSettings();
            await SaveSettingsAsync(defaults, ct).ConfigureAwait(false);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            lock (_lock)
            {
                _current = loaded ?? new AppSettings();
            }
            return _current;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read settings from {Path}, using defaults", _settingsPath);
            var fallback = new AppSettings();
            lock (_lock)
            {
                _current = fallback;
            }
            return fallback;
        }
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_settingsPath, json, ct).ConfigureAwait(false);
        lock (_lock)
        {
            _current = settings;
        }
        SettingsChanged?.Invoke(_current);
    }

    private AppSettings LoadSettingsSync()
    {
        if (!File.Exists(_settingsPath))
        {
            var defaults = new AppSettings();
            try
            {
                var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to write default settings");
            }
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse settings file {Path}, falling back to defaults", _settingsPath);
            return new AppSettings();
        }
    }
}
