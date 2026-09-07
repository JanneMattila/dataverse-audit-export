using System.Text;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace DataverseAuditExporter.Tests;

public sealed class EventHubAuditSinkTests
{
    private static readonly AuditRecord First = Audit(1);
    private static readonly AuditRecord Second = Audit(2);
    private static readonly AuditRecord Third = Audit(3);

    [Fact]
    public async Task FullBatchFlushesAndRejectedRecordIsRetriedInNextBatchWithRawBodyAndMetadata()
    {
        await using var producer = new FakeProducer(Encoding.UTF8.GetByteCount(First.Json) * 2);
        var options = Options();
        var sink = new EventHubAuditSink(producer, options);
        var acknowledged = new List<Guid>();

        await sink.DeliverAsync([First, Second, Third], audit =>
        {
            Assert.Contains(producer.Sent.SelectMany(batch => batch), data =>
                Equals(data.Properties["auditid"], audit.Id.ToString("D")));
            acknowledged.Add(audit.Id);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(new[] { 2, 1 }, producer.Sent.Select(batch => batch.Length));
        Assert.Equal(2, producer.BatchesCreated);
        Assert.Equal(new[] { First.Id, Second.Id, Third.Id }, acknowledged);
        var sent = producer.Sent.SelectMany(batch => batch).ToArray();
        var records = new[] { First, Second, Third };
        for (var index = 0; index < records.Length; index++)
        {
            Assert.Equal(records[index].Json, sent[index].Body);
            Assert.Equal(records[index].Id.ToString("D"), sent[index].Properties["auditid"]);
            Assert.Equal("https://example.crm.dynamics.com", sent[index].Properties["organization"]);
            Assert.Equal(records[index].CreatedOn.ToString("O"), sent[index].Properties["createdon"]);
            Assert.Equal(3, sent[index].Properties.Count);
        }
    }

    [Fact]
    public async Task NoAcknowledgementOccursUntilSendActuallyCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var producer = new FakeProducer(int.MaxValue)
        {
            BeforeSend = async cancellationToken =>
            {
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        };
        var acknowledged = new List<Guid>();
        var delivery = new EventHubAuditSink(producer, Options()).DeliverAsync([First, Second], audit =>
        {
            acknowledged.Add(audit.Id);
            return Task.CompletedTask;
        }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(delivery.IsCompleted);
            Assert.Empty(acknowledged);
            Assert.Empty(producer.Sent);
        }
        finally
        {
            release.TrySetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(new[] { First.Id, Second.Id }, acknowledged);
        Assert.Single(producer.Sent);
    }

    [Fact]
    public async Task OversizedSingleEventFailsExplicitlyWithoutSendingOrAcknowledging()
    {
        await using var producer = new FakeProducer(Encoding.UTF8.GetByteCount(First.Json) - 1);
        var acknowledged = new List<Guid>();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EventHubAuditSink(producer, Options()).DeliverAsync([First], audit =>
            {
                acknowledged.Add(audit.Id);
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Contains(First.Id.ToString(), exception.Message);
        Assert.Equal(1, producer.BatchesCreated);
        Assert.Equal(0, producer.SendAttempts);
        Assert.Empty(producer.Sent);
        Assert.Empty(acknowledged);
    }

    [Fact]
    public async Task OversizedLaterEventDoesNotDiscardPreviouslySentBatch()
    {
        await using var producer = new FakeProducer(Encoding.UTF8.GetByteCount(First.Json));
        var oversized = Second with { Json = new string('x', 1000) };
        var acknowledged = new List<Guid>();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new EventHubAuditSink(producer, Options()).DeliverAsync([First, oversized], audit =>
            {
                acknowledged.Add(audit.Id);
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(First.Json, Assert.Single(Assert.Single(producer.Sent)).Body);
        Assert.Equal(new[] { First.Id }, acknowledged);
        Assert.Equal(2, producer.BatchesCreated);
    }

    [Fact]
    public async Task FailedSecondSendAcknowledgesOnlyFirstSuccessfulBatch()
    {
        await using var producer = new FakeProducer(Encoding.UTF8.GetByteCount(First.Json)) { FailSendAttempt = 2 };
        var acknowledged = new List<Guid>();

        await Assert.ThrowsAsync<IOException>(() =>
            new EventHubAuditSink(producer, Options()).DeliverAsync([First, Second, Third], audit =>
            {
                acknowledged.Add(audit.Id);
                return Task.CompletedTask;
            }, CancellationToken.None));

        Assert.Equal(2, producer.SendAttempts);
        Assert.Equal(First.Json, Assert.Single(Assert.Single(producer.Sent)).Body);
        Assert.Equal(new[] { First.Id }, acknowledged);
    }

    [Fact]
    public async Task ReceiptFailureAfterSendCanDuplicateAnAlreadyDeliveredBatchOnRetry()
    {
        await using var producer = new FakeProducer(int.MaxValue);
        var sink = new EventHubAuditSink(producer, Options());

        await Assert.ThrowsAsync<IOException>(() => sink.DeliverAsync([First, Second],
            _ => throw new IOException("Injected receipt failure."), CancellationToken.None));

        Assert.Equal(2, Assert.Single(producer.Sent).Length);
        var acknowledged = new List<Guid>();
        await sink.DeliverAsync([First, Second], audit =>
        {
            acknowledged.Add(audit.Id);
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(new[] { First.Json, Second.Json, First.Json, Second.Json },
            producer.Sent.SelectMany(batch => batch).Select(data => data.Body));
        Assert.Equal(new[] { First.Id, Second.Id }, acknowledged);
    }

    [Fact]
    public async Task CancellationDuringSendDoesNotAcknowledgeBatch()
    {
        using var cancellation = new CancellationTokenSource();
        await using var producer = new FakeProducer(int.MaxValue)
        {
            BeforeSend = cancellationToken =>
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        var acknowledged = new List<Guid>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EventHubAuditSink(producer, Options()).DeliverAsync([First], audit =>
            {
                acknowledged.Add(audit.Id);
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.Empty(acknowledged);
        Assert.Empty(producer.Sent);
        Assert.Equal(1, producer.SendAttempts);
    }

    [Fact]
    public async Task EmptyInputDoesNotCreateOrSendBatch()
    {
        await using var producer = new FakeProducer(int.MaxValue);

        await new EventHubAuditSink(producer, Options()).DeliverAsync([], _ =>
        {
            Assert.Fail("No records should be acknowledged.");
            return Task.CompletedTask;
        }, CancellationToken.None);

        Assert.Equal(0, producer.BatchesCreated);
        Assert.Equal(0, producer.SendAttempts);
    }

    private static ExporterOptions Options()
    {
        var options = ExporterOptionsTests.ValidOptions();
        options.Validate();
        return options;
    }

    private static AuditRecord Audit(int number) => new(
        Guid.Parse($"00000000-0000-0000-0000-{number:D12}"),
        DateTimeOffset.Parse("2026-09-07T12:34:56.1234567Z"),
        $$"""{ "auditid": "00000000-0000-0000-0000-{{number:D12}}", "raw@annotation": "preserved" }""");

    private sealed record SentEvent(string Body, Dictionary<string, object> Properties);

    private sealed class FakeProducer(int bodyCapacityBytes) : EventHubProducerClient
    {
        private readonly Dictionary<EventDataBatch, List<EventData>> batches = [];
        public List<SentEvent[]> Sent { get; } = [];
        public int BatchesCreated { get; private set; }
        public int SendAttempts { get; private set; }
        public int? FailSendAttempt { get; init; }
        public Func<CancellationToken, Task>? BeforeSend { get; init; }

        public override ValueTask<EventDataBatch> CreateBatchAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var events = new List<EventData>();
            var acceptedBytes = 0;
            var batch = EventHubsModelFactory.EventDataBatch(0, events, new CreateBatchOptions(), data =>
            {
                var bodyBytes = data.EventBody.ToMemory().Length;
                if (bodyBytes > bodyCapacityBytes - acceptedBytes)
                    return false;
                acceptedBytes += bodyBytes;
                return true;
            });
            batches.Add(batch, events);
            BatchesCreated++;
            return ValueTask.FromResult(batch);
        }

        public override async Task SendAsync(EventDataBatch eventBatch, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendAttempts++;
            if (SendAttempts == FailSendAttempt)
                throw new IOException("Injected send failure.");
            if (BeforeSend is not null)
                await BeforeSend(cancellationToken);
            Sent.Add(batches[eventBatch].Select(data => new SentEvent(data.EventBody.ToString(), new(data.Properties))).ToArray());
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}