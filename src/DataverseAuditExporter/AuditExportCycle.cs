using Microsoft.Extensions.Logging;

namespace DataverseAuditExporter;

public sealed class AuditExportCycle(IAuditSource source, IEnumerable<IAuditSink> sinks, IExportState state,
    ILogger<AuditExportCycle> logger)
{
    public async Task RunAsync(DateTimeOffset? checkpoint, CancellationToken cancellationToken)
    {
        var highest = checkpoint;
        foreach (var sink in sinks)
            ArgumentNullException.ThrowIfNull(sink);
        if (!sinks.Any())
            throw new InvalidOperationException("At least one audit output is required.");
        await foreach (var page in source.ReadPagesAsync(checkpoint, cancellationToken))
        {
            foreach (var sink in sinks)
            {
                var pending = new List<AuditRecord>();
                var pageIds = new HashSet<Guid>();
                foreach (var audit in page)
                {
                    if (pageIds.Add(audit.Id) && !await state.IsDeliveredAsync(sink.Id, audit.Id, cancellationToken))
                        pending.Add(audit);
                }
                await sink.DeliverAsync(pending, audit => state.MarkDeliveredAsync(sink.Id, audit, cancellationToken), cancellationToken);
                logger.LogInformation("Output {Sink}: {Delivered} delivered, {Skipped} duplicates skipped.", sink.Id, pending.Count, page.Count - pending.Count);
            }
            foreach (var audit in page)
                if (!highest.HasValue || audit.CreatedOn > highest.Value)
                    highest = audit.CreatedOn;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await state.SaveCheckpointAsync(highest, cancellationToken);
        logger.LogInformation("Audit export completed. Checkpoint {Checkpoint}.", highest);
    }
}