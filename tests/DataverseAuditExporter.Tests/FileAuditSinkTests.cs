using System.Text;

namespace DataverseAuditExporter.Tests;

public sealed class FileAuditSinkTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "audit-file-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly AuditRecord Audit = new(
        Guid.Parse("00000000-0000-0000-0000-000000000001"),
        DateTimeOffset.Parse("2026-09-07T00:30:00.1234567+02:00"),
        "{ \"auditid\": \"00000000-0000-0000-0000-000000000001\", \"annotation\": \"raw value\" }");

    private string TargetPath => Path.Combine(root, "2026", "09", "06", Audit.Id.ToString("D") + ".json");

    [Fact]
    public async Task PublishesCompleteUtf8WithoutBomInUtcHierarchyBeforeAcknowledgement()
    {
        var acknowledged = new List<Guid>();
        var sink = new FileAuditSink(root);

        await sink.DeliverAsync([Audit], async audit =>
        {
            Assert.Equal(Encoding.UTF8.GetBytes(Audit.Json), await File.ReadAllBytesAsync(TargetPath));
            Assert.Equal(new[] { TargetPath }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            acknowledged.Add(audit.Id);
        }, CancellationToken.None);

        Assert.Equal(new[] { Audit.Id }, acknowledged);
        Assert.False(Directory.Exists(Path.Combine(root, "2026", "09", "07")));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingFileIsNotRewrittenButIsAcknowledged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
        await File.WriteAllTextAsync(TargetPath, "existing successful export");
        var originalTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(TargetPath, originalTimestamp);
        var acknowledged = new List<Guid>();

        await new FileAuditSink(root).DeliverAsync([Audit], audit =>
        {
            acknowledged.Add(audit.Id);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal("existing successful export", await File.ReadAllTextAsync(TargetPath));
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(TargetPath));
        Assert.Equal(new[] { Audit.Id }, acknowledged);
        Assert.Equal(new[] { TargetPath }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AtomicMoveFailureCleansTemporaryFileAndDoesNotAcknowledge()
    {
        Directory.CreateDirectory(TargetPath);
        var acknowledgements = 0;

        await Assert.ThrowsAnyAsync<IOException>(() => new FileAuditSink(root).DeliverAsync([Audit], _ =>
        {
            acknowledgements++;
            return Task.CompletedTask;
        }, CancellationToken.None));

        Assert.Equal(0, acknowledgements);
        Assert.True(Directory.Exists(TargetPath));
        Assert.False(File.Exists(TargetPath));
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReceiptFailureLeavesCompleteFileAndRetryDoesNotRewriteIt()
    {
        var sink = new FileAuditSink(root);
        await Assert.ThrowsAsync<IOException>(() => sink.DeliverAsync([Audit],
            _ => throw new IOException("Injected receipt failure."), CancellationToken.None));

        Assert.Equal(Audit.Json, await File.ReadAllTextAsync(TargetPath));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        var originalTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(TargetPath, originalTimestamp);
        var acknowledgements = 0;

        await sink.DeliverAsync([Audit], _ =>
        {
            acknowledgements++;
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(1, acknowledgements);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(TargetPath));
        Assert.Equal(Audit.Json, await File.ReadAllTextAsync(TargetPath));
    }

    [Fact]
    public async Task PreCanceledDeliveryDoesNotCreateDirectoriesOrAcknowledge()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var acknowledgements = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileAuditSink(root).DeliverAsync([Audit], _ =>
        {
            acknowledgements++;
            return Task.CompletedTask;
        }, cancellation.Token));

        Assert.Equal(0, acknowledgements);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task CancellationBetweenRecordsLeavesOnlyCompletedFileAndNoTemporaryFiles()
    {
        using var cancellation = new CancellationTokenSource();
        var next = Audit with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var acknowledged = new List<Guid>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileAuditSink(root).DeliverAsync([Audit, next], audit =>
        {
            acknowledged.Add(audit.Id);
            cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token));

        Assert.Equal(new[] { Audit.Id }, acknowledged);
        Assert.Equal(Audit.Json, await File.ReadAllTextAsync(TargetPath));
        Assert.Equal(new[] { TargetPath }, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task EmptyInputDoesNotCreateDirectoriesOrAcknowledge()
    {
        await new FileAuditSink(root).DeliverAsync([], _ =>
        {
            Assert.Fail("No records should be acknowledged.");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}