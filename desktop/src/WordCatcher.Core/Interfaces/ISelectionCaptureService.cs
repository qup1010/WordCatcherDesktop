using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface ISelectionCaptureService
{
    Task<CaptureResult?> CaptureSelectionAsync(CancellationToken ct = default);
}
