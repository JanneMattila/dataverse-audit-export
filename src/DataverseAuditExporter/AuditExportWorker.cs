using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataverseAuditExporter;

public sealed class AuditExportWorker(TableExportStateStore state, AuditExportCycle cycle, ExporterOptions options,
    ILogger<AuditExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await state.TryAcquireAsync(stoppingToken))
                        await RunOwnedAsync(stoppingToken);
                    else
                        logger.LogInformation("Another instance owns StateId {StateId}; waiting.", options.StateId);
                }
                catch (StateConfigurationException)
                {
                    Environment.ExitCode = 1;
                    throw;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning("Audit export failed; durable checkpoint retained. {Error}", exception.Message);
                }
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunOwnedAsync(CancellationToken stoppingToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        ownership.CancelAfter(TimeSpan.FromSeconds(45));
        var renewal = RenewAsync(ownership, renewalStop.Token);
        try
        {
            logger.LogInformation("Acquired export ownership at {Checkpoint}. StartFrom only initializes fresh state.", state.Checkpoint);
            while (!ownership.IsCancellationRequested)
            {
                await cycle.RunAsync(state.Checkpoint, ownership.Token);
                await Task.Delay(TimeSpan.FromSeconds(options.IntervalSeconds), ownership.Token);
            }
        }
        finally
        {
            await ownership.CancelAsync();
            await renewalStop.CancelAsync();
            await renewal;
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await state.ReleaseAsync(releaseTimeout.Token); }
            catch (Exception exception) { logger.LogDebug("Ownership release unavailable: {Error}", exception.Message); }
        }
    }

    private async Task RenewAsync(CancellationTokenSource ownership, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                await state.RenewAsync(ownership.Token);
                ownership.Token.ThrowIfCancellationRequested();
                ownership.CancelAfter(TimeSpan.FromSeconds(45));
            }
        }
        catch (OperationCanceledException) { await ownership.CancelAsync(); }
        catch (Exception exception)
        {
            logger.LogWarning("Ownership renewal failed: {Error}", exception.Message);
            await ownership.CancelAsync();
        }
    }
}