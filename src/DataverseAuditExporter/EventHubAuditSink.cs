using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace DataverseAuditExporter;

public sealed class EventHubAuditSink(EventHubProducerClient producer, ExporterOptions options) : IAuditSink
{
    public string Id { get; } = "eventhub-" + ExporterOptions.Hash($"{options.EventHubNamespace}/{options.EventHubName}");

    public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge, CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < records.Count)
        {
            using var batch = await producer.CreateBatchAsync(cancellationToken);
            var delivered = new List<AuditRecord>();
            while (index < records.Count)
            {
                var audit = records[index];
                var data = new EventData(BinaryData.FromString(audit.Json));
                data.Properties["auditid"] = audit.Id.ToString("D");
                data.Properties["organization"] = options.OrganizationUri.GetLeftPart(UriPartial.Authority);
                data.Properties["createdon"] = audit.CreatedOn.ToString("O");
                if (!batch.TryAdd(data))
                {
                    if (batch.Count == 0)
                        throw new InvalidDataException($"Audit {audit.Id} exceeds the Event Hubs batch size limit.");
                    break;
                }
                delivered.Add(audit);
                index++;
            }
            await producer.SendAsync(batch, cancellationToken);
            foreach (var audit in delivered)
                await acknowledge(audit);
        }
    }
}