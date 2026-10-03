using System.Threading.Tasks;

namespace WordCatcher.Core.Interfaces;

public interface IAnkiSyncQueue
{
    void Enqueue(string syncJobId);
    void TriggerSync();
    Task SyncNowAsync();
}
