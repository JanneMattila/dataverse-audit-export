using System.Globalization;
using Azure;
using Azure.Data.Tables;

namespace DataverseAuditExporter;

public sealed class StateConfigurationException(string message) : Exception(message);
public sealed class OwnershipLostException() : Exception("Exporter ownership was lost; the cycle was cancelled.");

public sealed class TableExportStateStore(TableClient table, ExporterOptions options, TimeProvider time) : IExportState
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string owner = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, (DateTimeOffset CreatedOn, HashSet<Guid> AuditIds)> deliveries = [];
    private TableEntity? current;
    public DateTimeOffset? Checkpoint { get; private set; }

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            current = null;
            var response = await table.GetEntityIfExistsAsync<TableEntity>(options.PartitionKey, "checkpoint", cancellationToken: cancellationToken);
            var entity = response.HasValue ? response.Value! : new TableEntity(options.PartitionKey, "checkpoint")
            {
                ["Version"] = 1,
                ["Organization"] = options.OrganizationUri.AbsoluteUri,
                ["Destinations"] = options.DestinationFingerprint,
                ["InitialCheckpoint"] = Format(options.InitialCheckpoint),
                ["Checkpoint"] = ""
            };
            Validate(entity);
            if (response.HasValue && ReadTimestamp(entity, "LeaseExpires") is { } expiry && expiry > time.GetUtcNow())
                return false;
            entity["Owner"] = owner;
            entity["LeaseExpires"] = Format(time.GetUtcNow().AddSeconds(60));
            try
            {
                var write = response.HasValue
                    ? await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken)
                    : await table.AddEntityAsync(entity, cancellationToken);
                entity.ETag = write.Headers.ETag ?? throw new InvalidDataException("Table write did not return an ETag.");
            }
            catch (RequestFailedException exception) when (exception.Status is 409 or 412)
            {
                return false;
            }
            current = entity;
            Checkpoint = ReadTimestamp(entity, "Checkpoint") ?? ReadTimestamp(entity, "InitialCheckpoint");
            return true;
        }
        finally { gate.Release(); }
    }

    public Task RenewAsync(CancellationToken cancellationToken) => UpdateOwnedAsync(null, false, false, cancellationToken);
    public Task SaveCheckpointAsync(DateTimeOffset? checkpoint, CancellationToken cancellationToken) => UpdateOwnedAsync(checkpoint, true, false, cancellationToken);
    public Task ReleaseAsync(CancellationToken cancellationToken) => UpdateOwnedAsync(null, false, true, cancellationToken);

    private async Task UpdateOwnedAsync(DateTimeOffset? checkpoint, bool save, bool release, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = current ?? throw new OwnershipLostException();
            if (entity.GetString("Owner") != owner || ReadTimestamp(entity, "LeaseExpires") <= time.GetUtcNow().AddSeconds(5))
                throw new OwnershipLostException();
            if (save)
            {
                if (Checkpoint.HasValue && (!checkpoint.HasValue || checkpoint < Checkpoint))
                    throw new InvalidOperationException("The checkpoint cannot move backwards.");
                entity["Checkpoint"] = Format(checkpoint);
                entity["LastSuccessfulRunUtc"] = Format(time.GetUtcNow());
            }
            entity["LeaseExpires"] = Format(release ? time.GetUtcNow() : time.GetUtcNow().AddSeconds(60));
            if (release)
                entity["Owner"] = "";
            try
            {
                var response = await table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, cancellationToken);
                entity.ETag = response.Headers.ETag ?? throw new InvalidDataException("Table write did not return an ETag.");
            }
            catch (RequestFailedException exception) when (exception.Status is 404 or 409 or 412)
            {
                current = null;
                throw new OwnershipLostException();
            }
            catch
            {
                current = null;
                throw;
            }
            if (save)
                Checkpoint = checkpoint;
            if (release)
                current = null;
        }
        finally { gate.Release(); }
    }

    public Task<bool> IsDeliveredAsync(string sinkId, Guid auditId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (deliveries)
            return Task.FromResult(deliveries.TryGetValue(sinkId, out var boundary) && boundary.AuditIds.Contains(auditId));
    }

    public Task MarkDeliveredAsync(string sinkId, AuditRecord audit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (deliveries)
        {
            if (!deliveries.TryGetValue(sinkId, out var boundary) || audit.CreatedOn > boundary.CreatedOn)
                deliveries[sinkId] = (audit.CreatedOn, [audit.Id]);
            else if (audit.CreatedOn == boundary.CreatedOn)
                boundary.AuditIds.Add(audit.Id);
        }
        return Task.CompletedTask;
    }

    private void Validate(TableEntity entity)
    {
        if (!entity.TryGetValue("Version", out var version) || version is not int schemaVersion || schemaVersion != 1 ||
            !entity.TryGetValue("Organization", out var organization) || organization is not string organizationText || organizationText != options.OrganizationUri.AbsoluteUri ||
            !entity.TryGetValue("Destinations", out var destinations) || destinations is not string destinationText || destinationText != options.DestinationFingerprint)
            throw new StateConfigurationException("Stored state has an incompatible schema, organization or output configuration. Use a new StateId for a new export.");
        _ = ReadTimestamp(entity, "Checkpoint");
        _ = ReadTimestamp(entity, "InitialCheckpoint");
    }

    private static string Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";

    private static DateTimeOffset? ReadTimestamp(TableEntity entity, string name)
    {
        if (!entity.TryGetValue(name, out var value) || value is "" or null)
            return null;
        if (value is string text && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return timestamp;
        throw new StateConfigurationException($"Stored state contains an invalid {name} timestamp.");
    }
}