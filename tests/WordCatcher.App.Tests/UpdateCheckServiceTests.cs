using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WordCatcher.App.Services;

namespace WordCatcher.App.Tests;

public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("v0.1.0", "v0.1.0", false)]
    [InlineData("0.2.0", "0.1.9", false)]
    [InlineData("0.1.0", "0.1.0-beta.1", false)]
    public void NewerVersionComparisonHandlesReleaseTags(string current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateCheckService.IsNewerVersion(current, latest));
    }

    [Fact]
    public async Task CheckReadsLatestReleaseAndSendsUserAgent()
    {
        var current = Version.Parse(UpdateCheckService.CurrentVersion);
        var latestVersion = new Version(current.Major, current.Minor, current.Build + 1).ToString();
        var latestTag = $"v{latestVersion}";
        var handler = new ReleaseHandler(latestTag);
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);

        var result = await service.CheckAsync();

        Assert.Equal(typeof(UpdateCheckService).Assembly.GetName().Version!.ToString(3), result.CurrentVersion);
        Assert.Equal(latestVersion, result.LatestVersion);
        Assert.Equal($"{UpdateCheckService.RepositoryUrl}/releases/tag/{latestTag}", result.ReleaseUrl);
        Assert.Equal($"{UpdateCheckService.RepositoryUrl}/releases/download/{latestTag}/{UpdateCheckService.InstallerAssetName}", result.InstallerDownloadUrl);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("https://api.github.com/repos/qup1010/WordCatcherDesktop/releases/latest", handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal("WordCatcherDesktop", handler.Request!.Headers.UserAgent.First().Product!.Name);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task RateLimitShowsAnActionableMessage(HttpStatusCode status)
    {
        using var client = new HttpClient(new ReleaseHandler("v0.3.4", status));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new UpdateCheckService(client).CheckAsync());
        Assert.Contains("稍后重试", error.Message);
        Assert.Contains("下载页面", error.Message);
    }

    private sealed class ReleaseHandler(string latestTag, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    $"{{\"tag_name\":\"{latestTag}\",\"html_url\":\"{UpdateCheckService.RepositoryUrl}/releases/tag/{latestTag}\",\"assets\":[{{\"name\":\"{UpdateCheckService.InstallerAssetName}\",\"browser_download_url\":\"{UpdateCheckService.RepositoryUrl}/releases/download/{latestTag}/{UpdateCheckService.InstallerAssetName}\"}}]}}")
            });
        }
    }
}
