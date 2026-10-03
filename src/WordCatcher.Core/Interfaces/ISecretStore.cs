using System.Threading;
using System.Threading.Tasks;

namespace WordCatcher.Core.Interfaces;

public interface ISecretStore
{
    Task<string> GetApiKeyAsync(CancellationToken ct = default);
    Task SetApiKeyAsync(string apiKey, CancellationToken ct = default);
}
