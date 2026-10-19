using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace WordCatcher.App.Services;

public interface IAppUpdater
{
    bool IsInstalled { get; }
    bool IsUpdateReady { get; }
    Task<bool> DownloadLatestAsync(Action<int> progress, CancellationToken ct);
    void PrepareRestart();
}

public sealed class AppUpdater : IAppUpdater
{
    private readonly UpdateManager _manager = new(new GithubSource(
        UpdateCheckService.RepositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;
    public bool IsUpdateReady => IsInstalled && _manager.UpdatePendingRestart != null;

    public async Task<bool> DownloadLatestAsync(Action<int> progress, CancellationToken ct)
    {
        if (!IsInstalled) return false;
        ct.ThrowIfCancellationRequested();
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (update == null) return IsUpdateReady;
        await _manager.DownloadUpdatesAsync(update, progress, ct).ConfigureAwait(false);
        return IsUpdateReady;
    }

    public void PrepareRestart()
    {
        if (!IsUpdateReady) throw new InvalidOperationException("尚未下载可安装的更新。");
        // Let WPF close normally and run OnExit cleanup before replacing binaries.
        _manager.WaitExitThenApplyUpdates(_manager.UpdatePendingRestart, silent: false, restart: true);
    }
}
