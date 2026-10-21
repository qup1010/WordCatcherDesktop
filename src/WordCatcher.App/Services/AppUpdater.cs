using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;

namespace WordCatcher.App.Services;

public interface IAppUpdater
{
    bool IsInstalled { get; }
    bool IsUpdateReady { get; }
    Task<bool> DownloadLatestAsync(Uri installerUri, string version, Action<int> progress, CancellationToken ct);
    void LaunchInstaller();
}

public sealed class AppUpdater : IAppUpdater
{
    private const string RegistrySubKey = @"Software\WordCatcherDesktop";
    private const string InstallLocationValue = "InstallLocation";
    private const string InstallerFilePrefix = "WordCatcherDesktop-win-Setup-";
    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;

    public AppUpdater(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _downloadDirectory = Path.Combine(Path.GetTempPath(), "WordCatcherDesktop", "Updates");
    }

    public bool IsInstalled => IsCurrentInstallManagedByInstaller();
    public bool IsUpdateReady => GetPendingInstallerPath() is not null;

    public async Task<bool> DownloadLatestAsync(Uri installerUri, string version, Action<int> progress, CancellationToken ct)
    {
        if (!IsInstalled) return false;

        var latestVersion = ParseVersion(version);
        if (latestVersion <= ParseVersion(UpdateCheckService.CurrentVersion)) return false;
        ValidateInstallerUri(installerUri);

        Directory.CreateDirectory(_downloadDirectory);
        var installerPath = GetInstallerPath(latestVersion);
        if (File.Exists(installerPath) && new FileInfo(installerPath).Length > 0)
        {
            progress(100);
            return true;
        }

        ct.ThrowIfCancellationRequested();
        var tempPath = installerPath + ".download";
        try
        {
            using var response = await _httpClient.GetAsync(installerUri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            long downloaded = 0;
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    downloaded += read;
                    if (contentLength is > 0)
                        progress((int)Math.Min(99, downloaded * 100 / contentLength.Value));
                }

                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            if (downloaded == 0 || (contentLength is > 0 && downloaded != contentLength.Value))
                throw new IOException("安装包下载不完整，请重新检查更新。");

            File.Move(tempPath, installerPath, overwrite: true);
            progress(100);
            return IsUpdateReady;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public void LaunchInstaller()
    {
        var installerPath = GetPendingInstallerPath()
            ?? throw new InvalidOperationException("尚未下载可安装的更新。");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        });
        if (process is null) throw new InvalidOperationException("无法启动安装向导。");
    }

    private string? GetPendingInstallerPath()
    {
        if (!IsInstalled || !Directory.Exists(_downloadDirectory)) return null;
        var currentVersion = ParseVersion(UpdateCheckService.CurrentVersion);

        return Directory.EnumerateFiles(_downloadDirectory, InstallerFilePrefix + "*.exe")
            .Select(path => (Path: path, Version: TryParseInstallerVersion(path)))
            .Where(item => item.Version is not null && item.Version.CompareTo(currentVersion) > 0 && new FileInfo(item.Path).Length > 0)
            .OrderByDescending(item => item.Version)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private string GetInstallerPath(Version version) =>
        Path.Combine(_downloadDirectory, $"{InstallerFilePrefix}{version}.exe");

    private static Version? TryParseInstallerVersion(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.StartsWith(InstallerFilePrefix, StringComparison.Ordinal)
            && Version.TryParse(fileName[InstallerFilePrefix.Length..], out var version)
                ? version
                : null;
    }

    private static Version ParseVersion(string version) =>
        Version.TryParse(version.TrimStart('v', 'V'), out var parsed)
            ? parsed
            : throw new InvalidOperationException($"无效的更新版本号：{version}");

    private static void ValidateInstallerUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/qup1010/WordCatcherDesktop/releases/download/", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(uri.AbsolutePath).Equals(UpdateCheckService.InstallerAssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("GitHub Release 中的安装包地址无效。");
        }
    }

    private static bool IsCurrentInstallManagedByInstaller()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySubKey, writable: false);
            var installedDirectory = key?.GetValue(InstallLocationValue) as string;
            if (string.IsNullOrWhiteSpace(installedDirectory)) return false;

            return Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(installedDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
