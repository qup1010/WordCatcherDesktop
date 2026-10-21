using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WordCatcher.App.Services;

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string? InstallerDownloadUrl,
    bool IsUpdateAvailable);

public sealed class UpdateCheckService
{
    public const string RepositoryUrl = "https://github.com/qup1010/WordCatcherDesktop";
    public const string InstallerAssetName = "WordCatcherDesktop-win-Setup.exe";
    private const string LatestReleaseApiUrl = RepositoryUrl + "/releases/latest";

    private readonly HttpClient _httpClient;

    public UpdateCheckService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public static string CurrentVersion =>
        typeof(UpdateCheckService).Assembly.GetName().Version?.ToString(3) ?? "0.3.0";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        var requestToken = timeoutCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WordCatcherDesktop", CurrentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("GitHub 上还没有已发布的版本。");

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestToken).ConfigureAwait(false);

        var root = document.RootElement;
        var tagName = root.TryGetProperty("tag_name", out var tagElement)
            ? tagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName))
            throw new InvalidOperationException("GitHub Release 缺少版本号。");

        var releaseUrl = root.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString()
            : null;
        releaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? RepositoryUrl + "/releases/latest" : releaseUrl;

        var latestVersion = NormalizeVersion(tagName);
        var installerDownloadUrl = GetInstallerDownloadUrl(root);
        var isUpdateAvailable = IsNewerVersion(CurrentVersion, latestVersion);
        if (isUpdateAvailable && installerDownloadUrl is null)
            throw new InvalidOperationException($"GitHub Release 缺少 {InstallerAssetName} 安装包。");

        return new UpdateCheckResult(
            CurrentVersion,
            latestVersion,
            releaseUrl,
            installerDownloadUrl,
            isUpdateAvailable);
    }

    public static bool IsNewerVersion(string currentVersion, string latestVersion)
    {
        return Version.TryParse(NormalizeVersion(currentVersion), out var current)
            && Version.TryParse(NormalizeVersion(latestVersion), out var latest)
            && latest > current;
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("无效的网页地址。", nameof(url));

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });
    }

    private static string NormalizeVersion(string value)
    {
        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];
        var prereleaseIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        return prereleaseIndex >= 0 ? normalized[..prereleaseIndex] : normalized;
    }

    private static string? GetInstallerDownloadUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement)
                || !string.Equals(nameElement.GetString(), InstallerAssetName, StringComparison.Ordinal))
                continue;

            if (!asset.TryGetProperty("browser_download_url", out var urlElement)
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/qup1010/WordCatcherDesktop/releases/download/", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(uri.AbsolutePath), InstallerAssetName, StringComparison.Ordinal))
                return null;

            return uri.ToString();
        }

        return null;
    }
}
