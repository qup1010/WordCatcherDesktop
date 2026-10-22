using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Sync;

public sealed class WebDavSyncService : IWebDavSyncService, IDisposable
{
    public const string SecretStoreKey = "__webdav_password__";
    public const string DefaultRemotePath = "WordCatcher/vocabulary-sync.json";

    private readonly HttpClient _httpClient;
    private readonly IWordRepository _wordRepository;
    private readonly ISettingsService _settingsService;
    private readonly ISecretStore _secretStore;
    private readonly ILogger<WebDavSyncService>? _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;

    private long _localRevision;
    private long _syncedRevision;
    private string? _verifiedTarget;
    private string? _lastSuccessTarget;
    private DateTimeOffset? _lastSuccessTime;
    private readonly string? _statusPath;
    private string? _conflictTarget;
    public bool IsSyncing { get; private set; }
    public bool HasPendingChanges => Interlocked.Read(ref _localRevision) != Interlocked.Read(ref _syncedRevision);
    public bool HasVerifiedSnapshot => _verifiedTarget == TargetIdentity();
    public WebDavFailureKind FailureKind { get; private set; }
    public WebDavConflict? Conflict { get; private set; }
    public event Action? StatusChanged;
    public DateTimeOffset? LastSyncedAtUtc => _lastSuccessTarget == TargetIdentity() ? _lastSuccessTime : null;
    public string LastError { get; private set; } = string.Empty;
    public event Action? SyncCompleted;
    public event Action<string>? SyncFailed;

    public WebDavSyncService(
        HttpClient httpClient,
        IWordRepository wordRepository,
        ISettingsService settingsService,
        ISecretStore secretStore,
        ILogger<WebDavSyncService>? logger = null,
        string? statusPath = null)
    {
        _httpClient = httpClient;
        _wordRepository = wordRepository;
        _settingsService = settingsService;
        _secretStore = secretStore;
        _logger = logger;
        _statusPath = statusPath;
        try
        {
            if (statusPath != null && File.Exists(statusPath))
            {
                var saved = JsonSerializer.Deserialize<SyncJournal>(File.ReadAllText(statusPath));
                _lastSuccessTarget = saved?.Target;
                _lastSuccessTime = saved?.Time;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { _logger?.LogDebug(ex, "Could not read sync status"); }
    }

    private sealed record SyncJournal(string Target, DateTimeOffset Time);
    private string TargetIdentity()
    {
        var settings = _settingsService.Current.WebDav;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new[] { settings.Url.Trim(), settings.RemotePath.Trim(), settings.Username.Trim() }))));
    }
    private void NotifyStatus()
    {
        if (StatusChanged == null) return;
        foreach (Action listener in StatusChanged.GetInvocationList())
            try { listener(); } catch (Exception ex) { _logger?.LogWarning(ex, "Sync status listener failed"); }
    }
    private void RecordSuccess(string target, DateTimeOffset time)
    {
        _lastSuccessTarget = target;
        _lastSuccessTime = time;
        if (_statusPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statusPath)!);
            File.WriteAllText(_statusPath + ".tmp", JsonSerializer.Serialize(new SyncJournal(target, time)));
            File.Move(_statusPath + ".tmp", _statusPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger?.LogWarning(ex, "Could not save sync status"); }
    }
    private static WebDavFailureKind Classify(Exception ex) => ex switch
    {
        SyncConflictException => WebDavFailureKind.Conflict,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => WebDavFailureKind.Authentication,
        HttpRequestException or OperationCanceledException => WebDavFailureKind.Network,
        JsonException => WebDavFailureKind.Configuration,
        _ when ex.Message.Contains("401/403", StringComparison.Ordinal) => WebDavFailureKind.Authentication,
        _ when ex.Message.Contains("其他设备", StringComparison.Ordinal) || ex.Message.Contains("冲突", StringComparison.Ordinal) => WebDavFailureKind.Conflict,
        _ when ex.Message.Contains("HTTPS", StringComparison.Ordinal) || ex.Message.Contains("ETag", StringComparison.Ordinal)
            || ex.Message.Contains("格式", StringComparison.Ordinal) || ex.Message.Contains("请先填写", StringComparison.Ordinal) => WebDavFailureKind.Configuration,
        _ => WebDavFailureKind.Other
    };

    public static (string BaseUrl, string FileUrl, IReadOnlyList<string> DirectoryUrls) ResolveTarget(
        string rawBaseUrl,
        string? rawRemotePath)
    {
        var trimmedBase = (rawBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmedBase))
            throw new InvalidOperationException("请先填写 WebDAV 服务器地址。");

        if (!Uri.TryCreate(trimmedBase, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("WebDAV 服务器地址必须使用 HTTPS，以保护认证信息和同步数据。");
        }

        if (trimmedBase.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = trimmedBase.LastIndexOf('/');
            var originLen = $"{parsed.Scheme}://{parsed.Authority}".Length;
            var parent = lastSlash > originLen ? trimmedBase[..lastSlash] : $"{parsed.Scheme}://{parsed.Authority}";
            var dirs = parent.Length > originLen ? new[] { parent } : Array.Empty<string>();
            return (parent, trimmedBase, dirs);
        }

        var cleanPath = string.IsNullOrWhiteSpace(rawRemotePath)
            ? DefaultRemotePath
            : rawRemotePath.Trim().Trim('/');
        var segments = cleanPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            segments = new[] { "WordCatcher", "vocabulary-sync.json" };

        var encodedSegments = segments.Select(Uri.EscapeDataString).ToArray();
        var directoryUrls = new List<string>();
        var current = trimmedBase;
        for (int i = 0; i < encodedSegments.Length - 1; i++)
        {
            current = $"{current}/{encodedSegments[i]}";
            directoryUrls.Add(current);
        }

        var fileUrl = $"{trimmedBase}/{string.Join("/", encodedSegments)}";
        return (trimmedBase, fileUrl, directoryUrls);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        string username,
        string password)
    {
        var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(password))
        {
            var raw = $"{username.Trim()}:{password}";
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64);
        }
        return req;
    }

    private async Task EnsureDirectoriesAsync(
        IReadOnlyList<string> directoryUrls,
        string username,
        string password,
        CancellationToken ct)
    {
        foreach (var dirUrl in directoryUrls)
        {
            using (var propReq = CreateRequest(new HttpMethod("PROPFIND"), $"{dirUrl}/", username, password))
            {
                propReq.Headers.TryAddWithoutValidation("Depth", "0");
                using var propRes = await _httpClient.SendAsync(propReq, ct).ConfigureAwait(false);
                if (propRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码是否正确。");
                if (propRes.IsSuccessStatusCode || (int)propRes.StatusCode == 207)
                    continue;
            }

            using var mkReq = CreateRequest(new HttpMethod("MKCOL"), $"{dirUrl}/", username, password);
            using var mkRes = await _httpClient.SendAsync(mkReq, ct).ConfigureAwait(false);
            if (mkRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码是否正确。");

            if (!mkRes.IsSuccessStatusCode && mkRes.StatusCode != HttpStatusCode.MethodNotAllowed && (int)mkRes.StatusCode != 207)
            {
                using var retryReq = CreateRequest(new HttpMethod("MKCOL"), dirUrl, username, password);
                using var retryRes = await _httpClient.SendAsync(retryReq, ct).ConfigureAwait(false);
                if (retryRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码是否正确。");
                if (!retryRes.IsSuccessStatusCode && retryRes.StatusCode != HttpStatusCode.MethodNotAllowed)
                    throw new InvalidOperationException($"创建 WebDAV 目录失败（HTTP {(int)retryRes.StatusCode}）。");
            }
        }
    }

    public async Task<(bool Exists, string FileUrl)> TestConnectionAsync(
        string url,
        string remotePath,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var (_, fileUrl, directoryUrls) = ResolveTarget(url, remotePath);

        using var propReq = CreateRequest(new HttpMethod("PROPFIND"), fileUrl, username, password);
        propReq.Headers.TryAddWithoutValidation("Depth", "0");
        using var propRes = await _httpClient.SendAsync(propReq, ct).ConfigureAwait(false);

        if (propRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码。");
        if (propRes.IsSuccessStatusCode || (int)propRes.StatusCode == 207)
            return (true, fileUrl);
        if (propRes.StatusCode == HttpStatusCode.NotFound)
        {
            await EnsureDirectoriesAsync(directoryUrls, username, password, ct).ConfigureAwait(false);
            return (false, fileUrl);
        }

        using var getReq = CreateRequest(HttpMethod.Get, fileUrl, username, password);
        using var getRes = await _httpClient.SendAsync(getReq, ct).ConfigureAwait(false);
        if (getRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码。");
        if (getRes.IsSuccessStatusCode)
            return (true, fileUrl);
        if (getRes.StatusCode == HttpStatusCode.NotFound)
        {
            await EnsureDirectoriesAsync(directoryUrls, username, password, ct).ConfigureAwait(false);
            return (false, fileUrl);
        }

        throw new InvalidOperationException($"WebDAV 服务器返回异常状态码：HTTP {(int)getRes.StatusCode}。");
    }

    public async Task<WebDavSyncResult> SyncAsync(bool forcePush = false, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        var startedRevision = Interlocked.Read(ref _localRevision);
        var target = TargetIdentity();
        IsSyncing = true;
        Conflict = null;
        LastError = string.Empty;
        FailureKind = WebDavFailureKind.None;
        NotifyStatus();
        try
        {
            var configured = _settingsService.Current.WebDav;
            var webdav = new WebDavSettings { Url = configured.Url, RemotePath = configured.RemotePath, Username = configured.Username };
            var (_, fileUrl, directoryUrls) = ResolveTarget(webdav.Url, webdav.RemotePath);
            var password = await ReadPasswordAsync(ct).ConfigureAwait(false);

            int added = 0;
            int updated = 0;
            int removed = 0;
            int total = 0;
            bool shouldPush = forcePush;
            string? remoteEtag = null;
            bool remoteMissing = false;

            using (var getReq = CreateRequest(HttpMethod.Get, fileUrl, webdav.Username, password))
            {
                getReq.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var getRes = await _httpClient.SendAsync(getReq, ct).ConfigureAwait(false);

                if (getRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new InvalidOperationException("WebDAV 认证失败（401/403），请检查用户名或应用密码。");

                if (getRes.IsSuccessStatusCode)
                {
                    remoteEtag = getRes.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
                    var remoteJson = await getRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(remoteJson))
                        throw new InvalidOperationException("远端同步文件为空，已停止同步以保留远端数据。");
                    // Once a remote snapshot is fetched, a later edit must not cancel its merge.
                    (int Added, int Updated, int Removed, int Total, bool NeedsPush) merged;
                    try { merged = await _wordRepository.MergeSyncJsonAsync(remoteJson, CancellationToken.None).ConfigureAwait(false); }
                    catch (SyncConflictException ex)
                    {
                        var local = JsonNode.Parse(await _wordRepository.ExportSyncJsonAsync(CancellationToken.None).ConfigureAwait(false))!["records"]!
                            .AsArray().FirstOrDefault(r => r?["id"]?.GetValue<string>() == ex.RecordId);
                        var remote = JsonNode.Parse(remoteJson)!["records"]!.AsArray().FirstOrDefault(r => r?["id"]?.GetValue<string>() == ex.RecordId);
                        if (local != null && remote != null)
                        {
                            Conflict = new(ex.RecordId, local.ToJsonString(), remote.ToJsonString());
                            _conflictTarget = target;
                        }
                        throw;
                    }
                    added = merged.Added;
                    updated = merged.Updated;
                    removed = merged.Removed;
                    total = merged.Total;
                    shouldPush = shouldPush || merged.NeedsPush || added > 0 || updated > 0 || removed > 0;
                }
                else if (getRes.StatusCode == HttpStatusCode.NotFound)
                {
                    remoteMissing = true;
                    await EnsureDirectoriesAsync(directoryUrls, webdav.Username, password, ct).ConfigureAwait(false);
                    total = (await _wordRepository.GetWordsAsync(limit: -1, ct: ct).ConfigureAwait(false)).Count;
                    shouldPush = true;
                }
                else
                {
                    throw new InvalidOperationException($"拉取远端生词库失败（HTTP {(int)getRes.StatusCode}）。");
                }
            }

            bool pushed = false;
            if (shouldPush)
            {
                if (!remoteMissing && remoteEtag == null)
                    throw new InvalidOperationException("WebDAV 服务器未提供强 ETag，无法安全合并写入，请检查服务器配置。");
                var syncPayload = await _wordRepository.ExportSyncJsonAsync(ct).ConfigureAwait(false);
                using var putReq = CreateRequest(HttpMethod.Put, fileUrl, webdav.Username, password);
                SetWriteCondition(putReq, remoteEtag, remoteMissing);
                putReq.Content = new StringContent(syncPayload, Encoding.UTF8, "application/json");
                using var putRes = await _httpClient.SendAsync(putReq, ct).ConfigureAwait(false);

                if (putRes.StatusCode == HttpStatusCode.PreconditionFailed)
                    throw new InvalidOperationException("远端词库已被其他设备更新，请重新同步以合并最新数据。");

                if (putRes.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                {
                    await EnsureDirectoriesAsync(directoryUrls, webdav.Username, password, ct).ConfigureAwait(false);
                    using var retryReq = CreateRequest(HttpMethod.Put, fileUrl, webdav.Username, password);
                    SetWriteCondition(retryReq, remoteEtag, remoteMissing);
                    retryReq.Content = new StringContent(syncPayload, Encoding.UTF8, "application/json");
                    using var retryRes = await _httpClient.SendAsync(retryReq, ct).ConfigureAwait(false);
                    if (retryRes.StatusCode == HttpStatusCode.PreconditionFailed)
                        throw new InvalidOperationException("远端词库已被其他设备更新，请重新同步以合并最新数据。");
                    if (!retryRes.IsSuccessStatusCode)
                        throw new InvalidOperationException($"上传生词库至 WebDAV 失败（HTTP {(int)retryRes.StatusCode}）。");
                }
                else if (putRes.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException("WebDAV 写入权限不足或认证失败（401/403）。");
                }
                else if (!putRes.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"上传生词库至 WebDAV 失败（HTTP {(int)putRes.StatusCode}）。");
                }
                pushed = true;
            }

            var syncedAt = DateTimeOffset.UtcNow;
            RecordSuccess(target, syncedAt);
            if (target == TargetIdentity())
            {
                _verifiedTarget = target;
                Interlocked.Exchange(ref _syncedRevision, startedRevision);
            }
            LastError = string.Empty;
            SyncCompleted?.Invoke();
            return new WebDavSyncResult(added, updated, removed, total, pushed, syncedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _verifiedTarget = null;
            throw;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            FailureKind = Classify(ex);
            _logger?.LogWarning(ex, "WebDAV synchronization failed");
            SyncFailed?.Invoke(ex.Message);
            throw;
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
            NotifyStatus();
        }
    }

    public void ScheduleAutoSync()
    {
        Interlocked.Increment(ref _localRevision);
        NotifyStatus();
        var webdav = _settingsService.Current.WebDav;
        if (!webdav.Enabled || !webdav.AutoSync || string.IsNullOrWhiteSpace(webdav.Url))
            return;

        CancellationTokenSource cts;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            cts = _debounceCts;
        }

        _ = Task.Run(() => RunScheduledSyncAsync(cts));
    }

    private async Task RunScheduledSyncAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(2500, cts.Token).ConfigureAwait(false);
            await SyncAsync(forcePush: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Background auto WebDAV sync failed");
        }
        finally
        {
            lock (_debounceLock)
            {
                if (ReferenceEquals(_debounceCts, cts))
                    _debounceCts = null;
            }
            cts.Dispose();
        }
    }

    public async Task ResolveConflictAsync(bool keepLocal, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Conflict == null) throw new InvalidOperationException("请先同步并查看最新冲突。");
            if (_conflictTarget != TargetIdentity()) throw new InvalidOperationException("同步目标已更改，请重新同步后处理冲突。");
            IsSyncing = true;
            NotifyStatus();
            await _wordRepository.ResolveSyncConflictAsync(Conflict, keepLocal, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _localRevision);
            Conflict = null;
            LastError = string.Empty;
            FailureKind = WebDavFailureKind.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            FailureKind = Classify(ex);
            throw;
        }
        finally
        {
            IsSyncing = false;
            _syncLock.Release();
            NotifyStatus();
        }
        await SyncAsync(forcePush: true, ct).ConfigureAwait(false);
    }

    private static void SetWriteCondition(HttpRequestMessage request, string? etag, bool missing)
    {
        if (missing) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else if (etag != null) request.Headers.IfMatch.ParseAdd(etag);
    }

    private async Task<string> ReadPasswordAsync(CancellationToken ct)
    {
        if (_secretStore is IProfileSecretStore profileSecrets)
            return await profileSecrets.GetApiKeyAsync(SecretStoreKey, ct).ConfigureAwait(false);
        return string.Empty;
    }

    public void Dispose()
    {
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts = null;
        }
    }
}
