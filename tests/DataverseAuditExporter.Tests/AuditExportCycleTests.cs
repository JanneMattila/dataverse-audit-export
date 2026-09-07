using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataverseAuditExporter.Tests;

public sealed class AuditExportCycleTests
{
    private static readonly DateTimeOffset Initial = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
    private static readonly AuditRecord First = Audit(1, Initial.AddTicks(1));
    private static readonly AuditRecord Second = Audit(2, Initial.AddTicks(1));
    private static readonly AuditRecord Third = Audit(3, Initial.AddTicks(2));

    [Fact]
    public async Task PartialDualSinkFailureRetainsCheckpointAndRetrySkipsEachSinksOwnReceipts()
    {
        var source = new FakeSource([First, Second]);
        var state = new FakeState(Initial);
        var files = new FakeSink("files");
        var events = new FakeSink("events") { FailBeforeId = Second.Id };
        var cycle = Create(source, state, files, events);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(state.Checkpoint, CancellationToken.None));

        Assert.Equal(Initial, state.Checkpoint);
        Assert.Empty(state.SavedCheckpoints);
        Assert.Equal(new[] { First.Id, Second.Id }, files.Delivered);
        Assert.Equal(new[] { First.Id }, events.Delivered);
        Assert.Equal(3, state.Receipts.Count);
        Assert.DoesNotContain((events.Id, Second.Id), state.Receipts);

        events.FailBeforeId = null;
        await cycle.RunAsync(state.Checkpoint, CancellationToken.None);

        Assert.Equal(new[] { First.Id, Second.Id }, files.Delivered);
        Assert.Equal(new[] { First.Id, Second.Id }, events.Delivered);
        Assert.Equal(4, state.Receipts.Count);
        Assert.Equal(Second.CreatedOn, Assert.Single(state.SavedCheckpoints));
        Assert.Equal(new DateTimeOffset?[] { Initial, Initial }, source.Checkpoints);
    }

    [Fact]
    public async Task NextPageFailureRetainsCheckpointAndRetrySkipsCompletedPage()
    {
        var source = new FakeSource([First], [Third]) { FailBeforePage = 1 };
        var state = new FakeState(Initial);
        var sink = new FakeSink("events");
        var cycle = Create(source, state, sink);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(Initial, CancellationToken.None));

        Assert.Equal(Initial, state.Checkpoint);
        Assert.Empty(state.SavedCheckpoints);
        Assert.Equal(new[] { First.Id }, sink.Delivered);
        Assert.Contains((sink.Id, First.Id), state.Receipts);

        source.FailBeforePage = null;
        await cycle.RunAsync(state.Checkpoint, CancellationToken.None);

        Assert.Equal(new[] { First.Id, Third.Id }, sink.Delivered);
        Assert.Equal(Third.CreatedOn, Assert.Single(state.SavedCheckpoints));
    }

    [Fact]
    public async Task ReceiptFailureAfterDeliveryPermitsDuplicateButDoesNotRepeatPriorReceipts()
    {
        var state = new FakeState(Initial) { FailReceiptId = Second.Id };
        var sink = new FakeSink("events");
        var cycle = Create(new FakeSource([First, Second]), state, sink);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(Initial, CancellationToken.None));

        Assert.Equal(new[] { First.Id, Second.Id }, sink.Delivered);
        Assert.Contains((sink.Id, First.Id), state.Receipts);
        Assert.DoesNotContain((sink.Id, Second.Id), state.Receipts);
        Assert.Equal(Initial, state.Checkpoint);
        Assert.Empty(state.SavedCheckpoints);

        state.FailReceiptId = null;
        await cycle.RunAsync(state.Checkpoint, CancellationToken.None);

        Assert.Equal(new[] { First.Id, Second.Id, Second.Id }, sink.Delivered);
        Assert.Equal(Second.CreatedOn, Assert.Single(state.SavedCheckpoints));
    }

    [Fact]
    public async Task EqualFractionalTimestampsAndRepeatedIdsAcrossPagesAreHandledIndependently()
    {
        var state = new FakeState(Initial);
        var sink = new FakeSink("events");
        var cycle = Create(new FakeSource([First, First], [First, Second], [Third]), state, sink);

        await cycle.RunAsync(Initial, CancellationToken.None);

        Assert.Equal(new[] { First.Id, Second.Id, Third.Id }, sink.Delivered);
        Assert.Equal(3, state.Receipts.Count);
        Assert.Equal(Third.CreatedOn, Assert.Single(state.SavedCheckpoints));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCyclePreservesNullOrExistingCheckpoint(bool hasCheckpoint)
    {
        DateTimeOffset? checkpoint = hasCheckpoint ? Initial : null;
        var state = new FakeState(checkpoint);
        var sink = new FakeSink("events");

        await Create(new FakeSource([]), state, sink).RunAsync(checkpoint, CancellationToken.None);

        Assert.Equal(checkpoint, Assert.Single(state.SavedCheckpoints));
        Assert.Empty(sink.Delivered);
        Assert.Empty(state.Receipts);
    }

    [Fact]
    public async Task OlderSourceRecordsDoNotRegressCheckpoint()
    {
        var checkpoint = Third.CreatedOn.AddHours(1);
        var state = new FakeState(checkpoint);
        var sink = new FakeSink("events");

        await Create(new FakeSource([First]), state, sink).RunAsync(checkpoint, CancellationToken.None);

        Assert.Equal(checkpoint, Assert.Single(state.SavedCheckpoints));
        Assert.Equal(new[] { First.Id }, sink.Delivered);
    }

    [Fact]
    public async Task CancellationAfterDeliveryPreventsReceiptAndCheckpoint()
    {
        using var cancellation = new CancellationTokenSource();
        var state = new FakeState(Initial);
        var sink = new FakeSink("events") { AfterDelivery = _ => cancellation.Cancel() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new FakeSource([First, Second]), state, sink).RunAsync(Initial, cancellation.Token));

        Assert.Equal(new[] { First.Id }, sink.Delivered);
        Assert.Empty(state.Receipts);
        Assert.Empty(state.SavedCheckpoints);
        Assert.Equal(Initial, state.Checkpoint);
    }

    [Fact]
    public async Task CancellationAfterFinalPagePreventsCheckpointEvenWhenSourceDoesNotThrow()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource([First]) { AfterPages = cancellation.Cancel };
        var state = new FakeState(Initial);
        var sink = new FakeSink("events");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(source, state, sink).RunAsync(Initial, cancellation.Token));

        Assert.Contains((sink.Id, First.Id), state.Receipts);
        Assert.Empty(state.SavedCheckpoints);
        Assert.Equal(Initial, state.Checkpoint);
    }

    [Fact]
    public async Task CancellationBeforeCyclePreventsDeliveryAndCheckpoint()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var state = new FakeState(Initial);
        var sink = new FakeSink("events");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(new FakeSource([First]), state, sink).RunAsync(Initial, cancellation.Token));

        Assert.Empty(sink.Delivered);
        Assert.Empty(state.Receipts);
        Assert.Empty(state.SavedCheckpoints);
    }

    [Fact]
    public async Task CheckpointFailureLeavesReceiptsAvailableForRetry()
    {
        var state = new FakeState(Initial) { FailCheckpoint = true };
        var sink = new FakeSink("events");
        var cycle = Create(new FakeSource([First]), state, sink);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(Initial, CancellationToken.None));

        Assert.Equal(Initial, state.Checkpoint);
        Assert.Contains((sink.Id, First.Id), state.Receipts);
        state.FailCheckpoint = false;
        await cycle.RunAsync(state.Checkpoint, CancellationToken.None);
        Assert.Equal(new[] { First.Id }, sink.Delivered);
        Assert.Equal(First.CreatedOn, Assert.Single(state.SavedCheckpoints));
    }

    [Fact]
    public async Task NoSinksFailsBeforeReadingSource()
    {
        var source = new FakeSource([First]);
        var state = new FakeState(Initial);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create(source, state).RunAsync(Initial, CancellationToken.None));

        Assert.Empty(source.Checkpoints);
        Assert.Empty(state.SavedCheckpoints);
    }

    private static AuditRecord Audit(int number, DateTimeOffset createdOn) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{number:D12}"), createdOn, "{}");

    private static AuditExportCycle Create(IAuditSource source, IExportState state, params IAuditSink[] sinks) =>
        new(source, sinks, state, NullLogger<AuditExportCycle>.Instance);

    private sealed class FakeSource(params IReadOnlyList<AuditRecord>[] pages) : IAuditSource
    {
        public List<DateTimeOffset?> Checkpoints { get; } = [];
        public int? FailBeforePage { get; set; }
        public Action? AfterPages { get; init; }

        public async IAsyncEnumerable<IReadOnlyList<AuditRecord>> ReadPagesAsync(DateTimeOffset? checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Checkpoints.Add(checkpoint);
            for (var index = 0; index < pages.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index == FailBeforePage)
                    throw new IOException("Injected next-page failure.");
                await Task.CompletedTask;
                yield return pages[index];
            }
            AfterPages?.Invoke();
        }
    }

    private sealed class FakeSink(string id) : IAuditSink
    {
        public string Id => id;
        public List<Guid> Delivered { get; } = [];
        public Guid? FailBeforeId { get; set; }
        public Action<AuditRecord>? AfterDelivery { get; init; }

        public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge, CancellationToken cancellationToken)
        {
            foreach (var audit in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (audit.Id == FailBeforeId)
                    throw new IOException("Injected output failure.");
                Delivered.Add(audit.Id);
                AfterDelivery?.Invoke(audit);
                await acknowledge(audit);
            }
        }
    }

    private sealed class FakeState(DateTimeOffset? checkpoint) : IExportState
    {
        public DateTimeOffset? Checkpoint { get; private set; } = checkpoint;
        public HashSet<(string SinkId, Guid AuditId)> Receipts { get; } = [];
        public List<DateTimeOffset?> SavedCheckpoints { get; } = [];
        public Guid? FailReceiptId { get; set; }
        public bool FailCheckpoint { get; set; }

        public Task<bool> IsDeliveredAsync(string sinkId, Guid auditId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Receipts.Contains((sinkId, auditId)));
        }

        public Task MarkDeliveredAsync(string sinkId, AuditRecord audit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (audit.Id == FailReceiptId)
                throw new IOException("Injected receipt failure.");
            Receipts.Add((sinkId, audit.Id));
            return Task.CompletedTask;
        }

        public Task SaveCheckpointAsync(DateTimeOffset? newCheckpoint, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailCheckpoint)
                throw new IOException("Injected checkpoint failure.");
            Checkpoint = newCheckpoint;
            SavedCheckpoints.Add(newCheckpoint);
            return Task.CompletedTask;
        }
    }
}