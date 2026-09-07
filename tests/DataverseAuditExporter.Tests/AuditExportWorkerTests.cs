using System.Runtime.CompilerServices;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataverseAuditExporter.Tests;

[Collection("Process state")]
public sealed class AuditExportWorkerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartupPollsImmediatelyAndCancellationStopsSourceWithoutCheckpointThenReleases()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = TableExportStateStoreTests.Options();
        options.IntervalSeconds = 86400;
        var state = new TableExportStateStore(table, options, time);
        var source = new ControlledSource();
        var sink = new RecordingSink();
        using var worker = Create(state, source, sink, options, new SignalLogger<AuditExportWorker>());
        await worker.StartAsync(default);
        try
        {
            await source.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(options.InitialCheckpoint, source.Checkpoint);
            Assert.NotEmpty(table.Snapshot(options.PartitionKey).GetString("Owner")!);
        }
        finally
        {
            await Stop(worker);
        }

        Assert.True(source.Cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(1, source.ReadCount);
        Assert.Empty(sink.Delivered);
        Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
        Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Owner"));
        Assert.True(await new TableExportStateStore(table, options, time).TryAcquireAsync(default));
    }

    [Fact]
    public async Task StandbyDoesNotReadSourceDeliverOrReleaseAnotherOwnersLease()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = TableExportStateStoreTests.Options();
        options.IntervalSeconds = 86400;
        var active = new TableExportStateStore(table, options, time);
        Assert.True(await active.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        var standby = new TableExportStateStore(table, options, time);
        var source = new ControlledSource();
        var sink = new RecordingSink();
        var logger = new SignalLogger<AuditExportWorker>();
        using var worker = Create(standby, source, sink, options, logger);
        await worker.StartAsync(default);
        try
        {
            await logger.Standby.Task.WaitAsync(Deadline);
        }
        finally
        {
            await Stop(worker);
        }

        Assert.Equal(0, source.ReadCount);
        Assert.Empty(sink.Delivered);
        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        await active.RenewAsync(default);
    }

    [Fact]
    public async Task SuccessfulCyclePersistsReceiptAndCheckpointBeforeCancellationOfLongPollingDelay()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = TableExportStateStoreTests.Options();
        options.IntervalSeconds = 86400;
        var state = new TableExportStateStore(table, options, time);
        var audit = new AuditRecord(Guid.NewGuid(), time.GetUtcNow(), "{}");
        var source = new ControlledSource { Page = [audit] };
        source.Continue.SetResult();
        var sink = new RecordingSink();
        var cycleLogger = new SignalLogger<AuditExportCycle>();
        using var worker = Create(state, source, sink, options, new SignalLogger<AuditExportWorker>(), cycleLogger);
        await worker.StartAsync(default);
        try
        {
            await cycleLogger.Completed.Task.WaitAsync(Deadline);
            Assert.Equal(audit.CreatedOn, state.Checkpoint);
            Assert.True(await state.IsDeliveredAsync(sink.Id, audit.Id, default));
        }
        finally
        {
            await Stop(worker);
        }

        Assert.Equal(new[] { audit.Id }, sink.Delivered);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(audit.CreatedOn.ToString("O"), table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
        Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Owner"));
    }

    [Fact]
    public async Task SourceFailureRetainsCheckpointAndReleasesBeforeRetryDelay()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = TableExportStateStoreTests.Options();
        options.IntervalSeconds = 86400;
        var state = new TableExportStateStore(table, options, time);
        var source = new ControlledSource { Failure = new IOException("Injected source failure") };
        source.Continue.SetResult();
        var sink = new RecordingSink();
        var logger = new SignalLogger<AuditExportWorker>();
        using var worker = Create(state, source, sink, options, logger);
        await worker.StartAsync(default);
        try
        {
            await logger.Failed.Task.WaitAsync(Deadline);
            Assert.False(worker.ExecuteTask!.IsCompleted);
            Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Owner"));
        }
        finally
        {
            await Stop(worker);
        }

        Assert.Equal(options.InitialCheckpoint, state.Checkpoint);
        Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
        Assert.Empty(sink.Delivered);
    }

    [Fact]
    public async Task TransientTableReadFailureDoesNotStartSourceOrTerminateWorker()
    {
        var table = new InMemoryTableClient
        {
            Failure = (operation, _) => operation == "read" ? new RequestFailedException(503, "Injected unavailable table") : null
        };
        var options = TableExportStateStoreTests.Options();
        options.IntervalSeconds = 86400;
        var state = new TableExportStateStore(table, options, new ManualTimeProvider());
        var source = new ControlledSource();
        var sink = new RecordingSink();
        var logger = new SignalLogger<AuditExportWorker>();
        using var worker = Create(state, source, sink, options, logger);
        await worker.StartAsync(default);
        try
        {
            await logger.Failed.Task.WaitAsync(Deadline);
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await Stop(worker);
        }

        Assert.Equal(0, source.ReadCount);
        Assert.Empty(sink.Delivered);
        Assert.Null(state.Checkpoint);
    }

    [Fact]
    public async Task IncompatibleStateFailsWorkerWithNonzeroExitCodeBeforeReadingSource()
    {
        var originalExitCode = Environment.ExitCode;
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = TableExportStateStoreTests.Options();
        Assert.True(await new TableExportStateStore(table, options, time).TryAcquireAsync(default));
        var incompatible = table.Snapshot(options.PartitionKey);
        incompatible["Version"] = 2;
        table.Seed(incompatible);
        var source = new ControlledSource();
        var sink = new RecordingSink();
        using var worker = Create(new TableExportStateStore(table, options, time), source, sink, options,
            new SignalLogger<AuditExportWorker>());
        try
        {
            await worker.StartAsync(default);
            await Assert.ThrowsAsync<StateConfigurationException>(() => worker.ExecuteTask!.WaitAsync(Deadline));
            Assert.Equal(1, Environment.ExitCode);
            Assert.Equal(0, source.ReadCount);
            Assert.Empty(sink.Delivered);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(Deadline);
            await worker.StopAsync(timeout.Token);
            Environment.ExitCode = originalExitCode;
        }
    }

    private static AuditExportWorker Create(TableExportStateStore state, IAuditSource source, IAuditSink sink,
        ExporterOptions options, ILogger<AuditExportWorker> logger, ILogger<AuditExportCycle>? cycleLogger = null) =>
        new(state, new AuditExportCycle(source, [sink], state, cycleLogger ?? NullLogger<AuditExportCycle>.Instance), options, logger);

    private static async Task Stop(AuditExportWorker worker)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        await worker.StopAsync(timeout.Token);
        await worker.ExecuteTask!.WaitAsync(Deadline);
    }

    private sealed class ControlledSource : IAuditSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AuditRecord> Page { get; init; } = [];
        public Exception? Failure { get; init; }
        public DateTimeOffset? Checkpoint { get; private set; }
        public int ReadCount { get; private set; }

        public async IAsyncEnumerable<IReadOnlyList<AuditRecord>> ReadPagesAsync(DateTimeOffset? checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            Checkpoint = checkpoint;
            Entered.TrySetResult();
            try
            {
                await Continue.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            if (Failure is not null)
                throw Failure;
            yield return Page;
        }
    }

    private sealed class RecordingSink : IAuditSink
    {
        public string Id => "test-events";
        public List<Guid> Delivered { get; } = [];
        public async Task DeliverAsync(IReadOnlyList<AuditRecord> records, Func<AuditRecord, Task> acknowledge,
            CancellationToken cancellationToken)
        {
            foreach (var audit in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Delivered.Add(audit.Id);
                await acknowledge(audit);
            }
        }
    }

    private sealed class SignalLogger<T> : ILogger<T>
    {
        public TaskCompletionSource Standby { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("Another instance owns", StringComparison.Ordinal))
                Standby.TrySetResult();
            if (message.StartsWith("Audit export failed;", StringComparison.Ordinal))
                Failed.TrySetResult();
            if (message.StartsWith("Audit export completed.", StringComparison.Ordinal))
                Completed.TrySetResult();
        }
    }
}