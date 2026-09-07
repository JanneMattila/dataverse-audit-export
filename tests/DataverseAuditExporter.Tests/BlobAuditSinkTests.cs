using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace DataverseAuditExporter.Tests;

public sealed class BlobAuditSinkTests
{
    private static readonly AuditRecord Audit = new(Guid.NewGuid(),
        DateTimeOffset.Parse("2026-09-07T01:00:00+03:00"), "{\"organization\":\"contoso.crm.dynamics.com\"}");

    private static ExporterOptions Options(string organization = "contoso")
    {
        var options = new ExporterOptions
        {
            OrganizationName = organization,
            StorageTableEndpoint = "https://state.table.core.windows.net",
            BlobStorageEndpoint = "https://exports.blob.core.windows.net",
            BlobContainerName = "audits"
        };
        options.Validate();
        return options.Organizations.Single();
    }

    [Fact]
    public async Task UploadUsesHostnameUtcPathJsonAndCreateOnlyCondition()
    {
        var container = new FakeContainer();
        var acknowledged = false;
        await new BlobAuditSink(container, Options()).DeliverAsync([Audit], _ =>
        {
            Assert.Equal(Audit.Json, container.Blob.Content!.ToString());
            acknowledged = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(acknowledged);
        Assert.Equal($"contoso.crm.dynamics.com/2026/09/06/{Audit.Id:D}.json", container.RequestedBlobName);
        Assert.Equal(ETag.All, container.Blob.Options!.Conditions.IfNoneMatch);
        Assert.Equal("application/json; charset=utf-8", container.Blob.Options.HttpHeaders.ContentType);
        Assert.NotEqual(new BlobAuditSink(container, Options()).Id, new BlobAuditSink(container, Options("second")).Id);
    }

    [Theory]
    [InlineData(409, "BlobAlreadyExists", true)]
    [InlineData(412, "ConditionNotMet", true)]
    [InlineData(409, "ContainerBeingDeleted", false)]
    [InlineData(412, "LeaseIdMissing", false)]
    [InlineData(403, "AuthorizationPermissionMismatch", false)]
    [InlineData(404, "ContainerNotFound", false)]
    [InlineData(500, "InternalError", false)]
    public async Task OnlyExistingBlobConflictsAreAcknowledged(int status, string code, bool success)
    {
        var container = new FakeContainer();
        container.Blob.Failure = new RequestFailedException(status, "Injected failure", code, null);
        var acknowledged = false;
        var delivery = new BlobAuditSink(container, Options()).DeliverAsync([Audit], _ =>
        {
            acknowledged = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        if (success)
            await delivery;
        else
            await Assert.ThrowsAsync<RequestFailedException>(() => delivery);
        Assert.Equal(success, acknowledged);
    }

    [Fact]
    public async Task CancellationDoesNotUploadOrAcknowledge()
    {
        var container = new FakeContainer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BlobAuditSink(container, Options())
            .DeliverAsync([Audit], _ => throw new InvalidOperationException("Must not acknowledge"), cancellation.Token));
        Assert.Null(container.Blob.Content);
    }

    [Fact]
    public async Task AcknowledgementWaitsForUploadCompletion()
    {
        var container = new FakeContainer();
        container.Blob.Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = false;
        var delivery = new BlobAuditSink(container, Options()).DeliverAsync([Audit], _ =>
        {
            acknowledged = true;
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.False(acknowledged);
        container.Blob.Completion.SetResult();
        await delivery;
        Assert.True(acknowledged);
    }

    private sealed class FakeContainer : BlobContainerClient
    {
        public override Uri Uri => new("https://exports.blob.core.windows.net/audits");
        public FakeBlob Blob { get; } = new();
        public string? RequestedBlobName { get; private set; }
        public override BlobClient GetBlobClient(string blobName)
        {
            RequestedBlobName = blobName;
            return Blob;
        }
    }

    private sealed class FakeBlob : BlobClient
    {
        public BinaryData? Content { get; private set; }
        public BlobUploadOptions? Options { get; private set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Completion { get; set; }
        public override async Task<Response<BlobContentInfo>> UploadAsync(BinaryData content,
            BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            Content = content;
            Options = options;
            if (Completion is not null)
                await Completion.Task.WaitAsync(cancellationToken);
            if (Failure is not null)
                throw Failure;
            return null!;
        }
    }
}