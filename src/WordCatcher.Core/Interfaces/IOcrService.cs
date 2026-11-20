using System.Threading;
using System.Threading.Tasks;

namespace WordCatcher.Core.Interfaces;

public interface IOcrService
{
    Task<string> RecognizePngAsync(byte[] pngBytes, CancellationToken ct = default);
}
