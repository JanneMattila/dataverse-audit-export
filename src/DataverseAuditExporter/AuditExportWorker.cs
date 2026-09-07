using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataverseAuditExporter;

public sealed record OrganizationExportJob(ExporterOptions Options, TableExportStateStore State, AuditExportCycle Cycle);

public sealed class AuditExportWorker(IReadOnlyList<OrganizationExportJob> organizations, ExporterOptions options,
    TimeProvider time, ILogger<AuditExportWorker> logger) : BackgroundService
{
    public AuditExportWorker(TableExportStateStore state, AuditExportCycle cycle, ExporterOptions options,
        ILogger<AuditExportWorker> logger) : this([new(options, state, cycle)], options, TimeProvider.System, logger) { }

    internal static TimeSpan PollingDelay(int intervalSeconds, TimeSpan elapsed) =>
        TimeSpan.FromSeconds(Math.Max(1, intervalSeconds - elapsed.TotalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var started = time.GetTimestamp();
                foreach (var organization in organizations)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    using var scope = logger.BeginScope("Organization {Organization}", organization.Options.OrganizationUri.Host);
                    try
                    {
                        if (await organization.State.TryAcquireAsync(stoppingToken))
                            await RunOwnedAsync(organization, stoppingToken);
                        else
                            logger.LogInformation("Another instance owns StateId {StateId} for {Organization}; skipping this round.",
                                organization.Options.StateId, organization.Options.OrganizationUri.Host);
                    }
                    catch (StateConfigurationException)
                    {
                        Environment.ExitCode = 1;
                        throw;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        logger.LogWarning("Audit export failed; durable checkpoint retained for {Organization}. {Error}",
                            organization.Options.OrganizationUri.Host, exception.Message);
                    }
                }
                await Task.Delay(PollingDelay(options.IntervalSeconds, time.GetElapsedTime(started)), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task RunOwnedAsync(OrganizationExportJob organization, CancellationToken stoppingToken)
    {
        var state = organization.State;
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        ownership.CancelAfter(TimeSpan.FromSeconds(45));
        var renewal = RenewAsync(state, ownership, renewalStop.Token);
        try
        {
            logger.LogInformation("Acquired export ownership at {Checkpoint}. StartFrom only initializes fresh state.", state.Checkpoint);
            await organization.Cycle.RunAsync(state.Checkpoint, ownership.Token);
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

    private async Task RenewAsync(TableExportStateStore state, CancellationTokenSource ownership, CancellationToken stoppingToken)
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