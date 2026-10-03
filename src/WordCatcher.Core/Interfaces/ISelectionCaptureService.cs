using System.Threading;
using System.Threading.Tasks;
using WordCatcher.Core.Models;

namespace WordCatcher.Core.Interfaces;

public interface ISelectionCaptureService
{
    // 重复请求返回 null；取消抛出 OperationCanceledException；取词失败抛出 CaptureException。
    Task<CaptureResult?> CaptureSelectionAsync(CancellationToken ct = default);
}
