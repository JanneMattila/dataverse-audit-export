using System.Text.Json;

namespace DataverseAuditExporter;

public sealed record AuditRecord(Guid Id, DateTimeOffset CreatedOn, string Json)
{
    public static AuditRecord Parse(JsonElement value, string organization)
    {
        var record = Parse(value);
        var payload = JsonSerializer.SerializeToNode(value)!.AsObject();
        payload["organization"] = organization;
        return record with { Json = payload.ToJsonString() };
    }

    public static AuditRecord Parse(JsonElement value)
    {
        if (!value.TryGetProperty("auditid", out var id) || !Guid.TryParse(id.GetString(), out var auditId) ||
            !value.TryGetProperty("createdon", out var createdOn) || !createdOn.TryGetDateTimeOffset(out var timestamp))
            throw new InvalidDataException("Dataverse returned an invalid auditid or createdon.");
        return new(auditId, timestamp.ToUniversalTime(), value.GetRawText());
    }
}

public interface IAuditSource
{
    IAsyncEnumerable<IReadOnlyList<AuditRecord>> ReadPagesAsync(DateTimeOffset? checkpoint, CancellationToken cancellationToken);
}

public interface IAuditSink
{
    string Id { get; }
    Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge, CancellationToken cancellationToken);
}

public interface IExportState
{
    Task<bool> IsDeliveredAsync(string sinkId, Guid auditId, CancellationToken cancellationToken);
    Task MarkDeliveredAsync(string sinkId, AuditRecord audit, CancellationToken cancellationToken);
    Task SaveCheckpointAsync(DateTimeOffset? checkpoint, CancellationToken cancellationToken);
}