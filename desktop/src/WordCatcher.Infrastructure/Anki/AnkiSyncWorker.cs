using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WordCatcher.Core.Enums;
using WordCatcher.Core.Interfaces;
using WordCatcher.Core.Models;

namespace WordCatcher.Infrastructure.Anki;

public sealed class AnkiSyncWorker : IAnkiSyncQueue, IDisposable
{
    private readonly IWordRepository _wordRepository;
    private readonly IAnkiClient _ankiClient;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<AnkiSyncWorker>? _logger;

    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private Task? _processingTask;
    private Task? _timerTask;
    private int _disposed;
    private bool _startupRecoveryComplete;

    public AnkiSyncWorker(
        IWordRepository wordRepository,
        IAnkiClient ankiClient,
        ISettingsService settingsService,
        ILogger<AnkiSyncWorker>? logger = null)
    {
        _wordRepository = wordRepository;
        _ankiClient = ankiClient;
        _settingsService = settingsService;
        _logger = logger;

        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    public void Start()
    {
        _processingTask = Task.Run(ProcessQueueAsync);
        _timerTask = Task.Run(PeriodicScanLoopAsync);
        TriggerSync();
    }

    public void Enqueue(string syncJobId)
    {
        _channel.Writer.TryWrite(syncJobId);
    }

    public void TriggerSync()
    {
        _channel.Writer.TryWrite("__TRIGGER_SCAN__");
    }

    public async Task SyncNowAsync()
    {
        await ProcessPendingAndRetryableJobsAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var jobId))
                {
                    if (_cts.IsCancellationRequested) return;

                    if (jobId == "__TRIGGER_SCAN__")
                    {
                        await ProcessPendingAndRetryableJobsAsync(_cts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await ProcessSingleJobByIdAsync(jobId, _cts.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Anki sync queue stopped unexpectedly");
        }
    }

    private async Task PeriodicScanLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                if (!_cts.IsCancellationRequested && _settingsService.Current.Anki.Enabled)
                {
                    await ProcessPendingAndRetryableJobsAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error in Anki periodic scan loop");
            }
        }
    }

    private async Task ProcessPendingAndRetryableJobsAsync(CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_startupRecoveryComplete)
            {
                try
                {
                    await _wordRepository.RecoverInterruptedSyncJobsAsync(ct).ConfigureAwait(false);
                    _startupRecoveryComplete = true;
                    _logger?.LogInformation("Recovered sync jobs interrupted by the previous application run");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to recover interrupted Anki sync jobs; recovery will retry on the next scan");
                    return;
                }
            }

            if (!_settingsService.Current.Anki.Enabled)
                return;

            var jobs = await _wordRepository.GetSyncJobsAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;

            foreach (var job in jobs)
            {
                if (ct.IsCancellationRequested) break;

                if (IsDueForAutomaticProcessing(job, now))
                {
                    await ExecuteSyncAsync(job, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task ProcessSingleJobByIdAsync(string jobId, CancellationToken ct)
    {
        if (!_settingsService.Current.Anki.Enabled)
            return;

        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var job = await _wordRepository.GetSyncJobByIdAsync(jobId, ct).ConfigureAwait(false);
            if (job != null && IsDueForAutomaticProcessing(job, DateTimeOffset.UtcNow))
            {
                await ExecuteSyncAsync(job, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static bool IsDueForAutomaticProcessing(SyncJob job, DateTimeOffset now)
    {
        return job.Status == SyncStatus.Pending
            || (job.Status == SyncStatus.Retryable
                && job.NextAttemptAtUtc is { } nextAttemptAt
                && nextAttemptAt <= now);
    }

    private async Task ExecuteSyncAsync(SyncJob job, CancellationToken ct)
    {
        job.Word ??= await _wordRepository.GetWordByIdAsync(job.WordId, ct).ConfigureAwait(false);
        if (job.Word == null)
        {
            job.Status = SyncStatus.Failed;
            job.LastError = "Word record not found";
            await _wordRepository.UpdateSyncJobAsync(job, ct).ConfigureAwait(false);
            return;
        }

        if (job.Occurrence == null)
        {
            job.Occurrence = await _wordRepository.GetOccurrenceByIdAsync(job.OccurrenceId, ct).ConfigureAwait(false);
        }

        if (job.Occurrence == null)
        {
            job.Status = SyncStatus.Failed;
            job.LastError = "Occurrence record not found";
            await _wordRepository.UpdateSyncJobAsync(job, ct).ConfigureAwait(false);
            return;
        }

        job.Status = SyncStatus.Syncing;
        await _wordRepository.UpdateSyncJobAsync(job, ct).ConfigureAwait(false);

        try
        {
            await _ankiClient.AddNoteAsync(job.Word, job.Occurrence, ct).ConfigureAwait(false);
            job.Status = SyncStatus.Synced;
            job.LastError = string.Empty;
            job.NextAttemptAtUtc = null;
            _logger?.LogInformation("Sync job {JobId} for word '{Word}' completed successfully", job.Id, job.Word.DisplayWord);
        }
        catch (AnkiException ex)
        {
            _logger?.LogWarning(ex, "Anki error syncing job {JobId}", job.Id);
            HandleAnkiError(job, ex);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error syncing job {JobId}", job.Id);
            HandleAnkiError(job, ex);
        }

        await _wordRepository.UpdateSyncJobAsync(job, ct).ConfigureAwait(false);
    }

    private static void HandleAnkiError(SyncJob job, Exception ex)
    {
        var msg = ex.Message;
        // If template/model deterministic error -> Failed
        if (msg.Contains("model", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("field", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("字段", StringComparison.Ordinal)
            || msg.Contains("deck", StringComparison.OrdinalIgnoreCase) && !msg.Contains("connect", StringComparison.OrdinalIgnoreCase))
        {
            job.Status = SyncStatus.Failed;
            job.LastError = msg;
            job.NextAttemptAtUtc = null;
            return;
        }

        // Connection refused / timeout / network -> Retryable
        job.Status = SyncStatus.Retryable;
        job.LastError = msg;
        job.Attempts++;

        var now = DateTimeOffset.UtcNow;
        job.NextAttemptAtUtc = job.Attempts switch
        {
            1 => now.AddSeconds(30),
            2 => now.AddMinutes(2),
            3 => now.AddMinutes(10),
            _ => null // Stop automatic retry, keep in Retryable for manual retry
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        _channel.Writer.TryComplete();

        var tasks = new[] { _processingTask, _timerTask };
        foreach (var task in tasks)
        {
            if (task == null) continue;
            try
            {
                task.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
            {
                // Expected when cancellation reaches a worker.
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Anki sync worker did not stop cleanly");
            }
        }

        // Do not dispose the semaphore while a timed-out worker could still be
        // executing its finally block and calling Release().
        if ((_processingTask?.IsCompleted ?? true) && (_timerTask?.IsCompleted ?? true))
        {
            _syncLock.Dispose();
            _cts.Dispose();
        }
    }
}
