using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using WordCatcher.Infrastructure.Security;
using Xunit;

namespace WordCatcher.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public sealed class SecurityTests
{
    [Fact]
    public async Task DpapiSecretStore_PreservesKeysPerProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wordcatcher_secrets_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new DpapiSecretService(path);
            await store.SetApiKeyAsync("default", "default-key");
            await store.SetApiKeyAsync("local", "local-key");

            var reloaded = new DpapiSecretService(path);
            Assert.Equal("default-key", await reloaded.GetApiKeyAsync("default"));
            Assert.Equal("local-key", await reloaded.GetApiKeyAsync("local"));

            await reloaded.SetApiKeyAsync("default", string.Empty);
            Assert.Empty(await reloaded.GetApiKeyAsync("default"));
            Assert.Equal("local-key", await reloaded.GetApiKeyAsync("local"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
