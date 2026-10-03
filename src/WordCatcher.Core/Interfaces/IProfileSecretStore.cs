using System.Threading;
using System.Threading.Tasks;

namespace WordCatcher.Core.Interfaces;

/// <summary>
/// Optional extension of the legacy secret store for per-AI-profile keys.
/// Implementations must keep the values encrypted at rest.
/// </summary>
public interface IProfileSecretStore : ISecretStore
{
    Task<string> GetApiKeyAsync(string profileId, CancellationToken ct = default);
    Task SetApiKeyAsync(string profileId, string apiKey, CancellationToken ct = default);
}
