using System;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task<AppSettings> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettings settings, CancellationToken ct = default);
    event Action<AppSettings>? SettingsChanged;
}

public interface IAtomicSettingsService : ISettingsService
{
    Task UpdateSettingsAsync(Action<AppSettings> mutate, CancellationToken ct = default);
}

public static class SettingsServiceExtensions
{
    public static Task UpdateSettingsAsync(this ISettingsService service, Action<AppSettings> mutate, CancellationToken ct = default)
    {
        if (service is IAtomicSettingsService atomic)
            return atomic.UpdateSettingsAsync(mutate, ct);

        var clone = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(service.Current)) ?? new AppSettings();
        mutate(clone);
        return service.SaveSettingsAsync(clone, ct);
    }
}
