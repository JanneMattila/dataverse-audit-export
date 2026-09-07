using Azure.Data.Tables;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataverseAuditExporter.Tests;

public sealed class InMemoryDuplicateTrackingTests
{
    [Fact]
    public async Task OnlyLatestTimestampIdsAreKeptPerOutputWithoutStorageCalls()
    {
        var options = ExporterOptionsTests.ValidOptions();
        options.Validate();
        var state = new TableExportStateStore(new UnconfiguredTableClient(), options, TimeProvider.System);
        var timestamp = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var first = new AuditRecord(Guid.NewGuid(), timestamp, "{}");
        var tied = new AuditRecord(Guid.NewGuid(), timestamp, "{}");
        var newer = new AuditRecord(Guid.NewGuid(), timestamp.AddTicks(1), "{}");

        await state.MarkDeliveredAsync("events", first, default);
        await state.MarkDeliveredAsync("events", tied, default);
        await state.MarkDeliveredAsync("files", first, default);
        Assert.True(await state.IsDeliveredAsync("events", first.Id, default));
        Assert.True(await state.IsDeliveredAsync("events", tied.Id, default));
        await state.MarkDeliveredAsync("events", newer, default);
        Assert.False(await state.IsDeliveredAsync("events", first.Id, default));
        Assert.False(await state.IsDeliveredAsync("events", tied.Id, default));
        Assert.True(await state.IsDeliveredAsync("events", newer.Id, default));
        Assert.True(await state.IsDeliveredAsync("files", first.Id, default));
        await state.MarkDeliveredAsync("events", first, default);
        Assert.False(await state.IsDeliveredAsync("events", first.Id, default));
        Assert.True(await state.IsDeliveredAsync("events", newer.Id, default));

        var restarted = new TableExportStateStore(new UnconfiguredTableClient(), options, TimeProvider.System);
        Assert.False(await restarted.IsDeliveredAsync("events", newer.Id, default));
    }

    [Fact]
    public async Task FailedPagedExportReplaysOlderRecordsAndDeduplicatesLatestTimestamp()
    {
        var options = TableExportStateStoreTests.Options();
        var state = new TableExportStateStore(new InMemoryTableClient(), options, new ManualTimeProvider());
        Assert.True(await state.TryAcquireAsync(default));
        var initial = state.Checkpoint;
        var timestamp = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var first = new AuditRecord(Guid.NewGuid(), timestamp, "{}");
        var second = new AuditRecord(Guid.NewGuid(), timestamp.AddTicks(1), "{}");
        var third = new AuditRecord(Guid.NewGuid(), second.CreatedOn, "{}");
        var sink = new RecordingSink { FailId = third.Id };
        var cycle = new AuditExportCycle(new PagedSource([first, second, third]), [sink], state,
            NullLogger<AuditExportCycle>.Instance);

        await Assert.ThrowsAsync<IOException>(() => cycle.RunAsync(state.Checkpoint, default));
        Assert.Equal(initial, state.Checkpoint);
        Assert.False(await state.IsDeliveredAsync(sink.Id, first.Id, default));
        Assert.True(await state.IsDeliveredAsync(sink.Id, second.Id, default));
        Assert.False(await state.IsDeliveredAsync(sink.Id, third.Id, default));

        sink.FailId = null;
        await cycle.RunAsync(state.Checkpoint, default);
        Assert.Equal(new[] { first.Id, second.Id, first.Id, third.Id }, sink.Delivered);
        Assert.Equal(second.CreatedOn, state.Checkpoint);
        await cycle.RunAsync(state.Checkpoint, default);
        Assert.Equal(4, sink.Delivered.Count);
    }

    [Fact]
    public async Task AdvancingTimestampsDoNotAccumulateHistory()
    {
        var options = ExporterOptionsTests.ValidOptions();
        options.Validate();
        var state = new TableExportStateStore(new UnconfiguredTableClient(), options, TimeProvider.System);
        var timestamp = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var audits = Enumerable.Range(0, 1000)
            .Select(index => new AuditRecord(Guid.NewGuid(), timestamp.AddTicks(index), "{}")).ToArray();
        foreach (var audit in audits)
            await state.MarkDeliveredAsync("events", audit, default);
        foreach (var audit in audits.SkipLast(1))
            Assert.False(await state.IsDeliveredAsync("events", audit.Id, default));
        Assert.True(await state.IsDeliveredAsync("events", audits[^1].Id, default));
    }

    private sealed class PagedSource(AuditRecord[] records) : IAuditSource
    {
        public async IAsyncEnumerable<IReadOnlyList<AuditRecord>> ReadPagesAsync(DateTimeOffset? checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var audit in records.Where(audit => !checkpoint.HasValue || audit.CreatedOn >= checkpoint))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.CompletedTask;
                yield return new[] { audit };
            }
        }
    }

    private sealed class RecordingSink : IAuditSink
    {
        public string Id => "events";
        public Guid? FailId { get; set; }
        public List<Guid> Delivered { get; } = [];

        public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge,
            CancellationToken cancellationToken)
        {
            foreach (var audit in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (audit.Id == FailId)
                    throw new IOException("Injected output failure.");
                Delivered.Add(audit.Id);
                await acknowledge(audit);
            }
        }
    }

    private sealed class UnconfiguredTableClient : TableClient;
}