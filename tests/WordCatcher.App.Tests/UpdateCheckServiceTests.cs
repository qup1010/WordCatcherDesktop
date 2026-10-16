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
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("WordCatcherDesktop", handler.Request!.Headers.UserAgent.First().Product!.Name);
    }

    private sealed class ReleaseHandler(string latestTag) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"tag_name\":\"{latestTag}\",\"html_url\":\"{UpdateCheckService.RepositoryUrl}/releases/tag/{latestTag}\"}}")
            });
        }
    }
}
