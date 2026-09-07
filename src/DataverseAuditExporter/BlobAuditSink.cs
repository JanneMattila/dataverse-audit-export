using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DataverseAuditExporter;

public sealed class BlobAuditSink(BlobContainerClient container, ExporterOptions options) : IAuditSink
{
    public string Id { get; } = "blob-" + ExporterOptions.Hash(container.Uri.AbsoluteUri + "/" + options.OrganizationUri.Host);

    public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge, CancellationToken cancellationToken)
    {
        foreach (var audit in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var date = audit.CreatedOn.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
            var blob = container.GetBlobClient($"{options.OrganizationUri.Host}/{date}/{audit.Id:D}.json");
            try
            {
                await blob.UploadAsync(BinaryData.FromString(audit.Json), new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }
                }, cancellationToken);
            }
            catch (RequestFailedException exception) when (
                (exception.Status == 409 && exception.ErrorCode == "BlobAlreadyExists") ||
                (exception.Status == 412 && exception.ErrorCode == "ConditionNotMet"))
            {
            }
            cancellationToken.ThrowIfCancellationRequested();
            await acknowledge(audit);
        }
    }
}