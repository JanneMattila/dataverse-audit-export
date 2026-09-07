using Azure;
using Azure.Data.Tables;

namespace DataverseAuditExporter.Tests;

public sealed class TableExportStateStoreTests
{
    [Fact]
    public async Task FakeTableResponseETagCanBeUsedForNextConditionalWrite()
    {
        var table = new InMemoryTableClient();
        var entity = new TableEntity("partition", "row");
        var response = await table.AddEntityAsync(entity);
        Assert.Equal(table.Snapshot("partition", "row").ETag, response.Headers.ETag);
        await table.UpdateEntityAsync(entity, response.Headers.ETag!.Value, TableUpdateMode.Replace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentAcquisitionHasExactlyOneWinner(bool existingExpiredLease)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        if (existingExpiredLease)
        {
            Assert.True(await Store(table, options, time).TryAcquireAsync(default));
            time.Advance(TimeSpan.FromSeconds(60));
        }
        var bothRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = 0;
        table.AfterRead = async row =>
        {
            Assert.Equal("checkpoint", row);
            if (Interlocked.Increment(ref readers) == 2)
                bothRead.SetResult();
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };
        var first = Store(table, options, time);
        var second = Store(table, options, time);

        var results = await Task.WhenAll(first.TryAcquireAsync(default), second.TryAcquireAsync(default));

        Assert.Single(results, acquired => acquired);
        Assert.Equal(1, table.ConditionalConflicts);
        table.AfterRead = null;
        await (results[0] ? first : second).SaveCheckpointAsync(time.GetUtcNow(), default);
        await Assert.ThrowsAsync<OwnershipLostException>(() =>
            (results[0] ? second : first).SaveCheckpointAsync(time.GetUtcNow(), default));
    }

    [Fact]
    public async Task UnexpiredLeaseLeavesStandbyAndDurableStateUnchanged()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        time.Advance(TimeSpan.FromSeconds(59));
        var standby = Store(table, options, time);

        Assert.False(await standby.TryAcquireAsync(default));

        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        await Assert.ThrowsAsync<OwnershipLostException>(() => standby.RenewAsync(default));
        await Assert.ThrowsAsync<OwnershipLostException>(() => standby.SaveCheckpointAsync(time.GetUtcNow(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiryTakeoverFencesOldOwnerEvenWithStaleLocalClock(bool rollBackOldClock)
    {
        var table = new InMemoryTableClient();
        var oldTime = new ManualTimeProvider();
        var newTime = new ManualTimeProvider();
        var options = Options();
        var oldOwner = Store(table, options, oldTime);
        Assert.True(await oldOwner.TryAcquireAsync(default));
        var checkpoint = oldTime.GetUtcNow();
        await oldOwner.SaveCheckpointAsync(checkpoint, default);
        newTime.Advance(TimeSpan.FromSeconds(60));
        if (!rollBackOldClock)
            oldTime.Advance(TimeSpan.FromSeconds(60));
        var newOwner = Store(table, options, newTime);

        Assert.True(await newOwner.TryAcquireAsync(default));
        Assert.Equal(checkpoint, newOwner.Checkpoint);
        await Assert.ThrowsAsync<OwnershipLostException>(() => oldOwner.SaveCheckpointAsync(checkpoint.AddTicks(1), default));
        await Assert.ThrowsAsync<OwnershipLostException>(() => oldOwner.RenewAsync(default));
        await Assert.ThrowsAsync<OwnershipLostException>(() => oldOwner.ReleaseAsync(default));

        Assert.Equal(checkpoint.ToString("O"), table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
        await newOwner.SaveCheckpointAsync(checkpoint.AddTicks(2), default);
        Assert.Equal(rollBackOldClock ? 1 : 0, table.ConditionalConflicts);
    }

    [Fact]
    public async Task RenewalExtendsLeaseAndUsesLatestETagForSubsequentCheckpoint()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        time.Advance(TimeSpan.FromSeconds(30));

        await owner.RenewAsync(default);

        var renewed = table.Snapshot(options.PartitionKey);
        Assert.NotEqual(before.ETag, renewed.ETag);
        Assert.Equal(time.GetUtcNow().AddSeconds(60).ToString("O"), renewed.GetString("LeaseExpires"));
        Assert.Equal(before.GetString("Owner"), renewed.GetString("Owner"));
        Assert.Equal(before.GetString("Checkpoint"), renewed.GetString("Checkpoint"));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.False(await Store(table, options, time).TryAcquireAsync(default));
        await owner.SaveCheckpointAsync(time.GetUtcNow(), default);
    }

    [Theory]
    [InlineData(54, true)]
    [InlineData(55, false)]
    [InlineData(60, false)]
    public async Task RenewalHonorsFiveSecondSafetyMargin(int elapsedSeconds, bool allowed)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        time.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        if (allowed)
            await owner.RenewAsync(default);
        else
        {
            await Assert.ThrowsAsync<OwnershipLostException>(() => owner.RenewAsync(default));
            Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        }
    }

    [Fact]
    public async Task GracefulReleaseAllowsImmediateTakeoverWithoutLosingCheckpoint()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        await owner.SaveCheckpointAsync(time.GetUtcNow(), default);

        await owner.ReleaseAsync(default);

        var released = table.Snapshot(options.PartitionKey);
        Assert.Equal("", released.GetString("Owner"));
        Assert.Equal(time.GetUtcNow().ToString("O"), released.GetString("LeaseExpires"));
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.RenewAsync(default));
        var successor = Store(table, options, time);
        Assert.True(await successor.TryAcquireAsync(default));
        Assert.Equal(time.GetUtcNow(), successor.Checkpoint);
    }

    [Fact]
    public async Task EqualTimestampsRoundTripAsSevenDigitStringsAcrossRestartAndReceipts()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        Assert.Equal(options.InitialCheckpoint, owner.Checkpoint);
        var checkpoint = time.GetUtcNow().ToOffset(TimeSpan.FromHours(2));
        var first = new AuditRecord(Guid.NewGuid(), checkpoint, "{}");
        var second = new AuditRecord(Guid.NewGuid(), checkpoint, "{}");
        await owner.MarkDeliveredAsync("events", first, default);
        await owner.MarkDeliveredAsync("events", second, default);
        await owner.SaveCheckpointAsync(checkpoint, default);
        await owner.SaveCheckpointAsync(checkpoint, default);

        var stored = table.Snapshot(options.PartitionKey);
        Assert.Equal("2026-09-01T00:00:00.1234567+00:00", Assert.IsType<string>(stored["InitialCheckpoint"]));
        Assert.Equal("2026-09-07T12:00:00.1234567+00:00", Assert.IsType<string>(stored["Checkpoint"]));
        Assert.Equal(time.GetUtcNow().ToString("O"), Assert.IsType<string>(stored["LastSuccessfulRunUtc"]));
        Assert.Equal(time.GetUtcNow().AddSeconds(60).ToString("O"), Assert.IsType<string>(stored["LeaseExpires"]));
        foreach (var audit in new[] { first, second })
        {
            var receipt = table.Snapshot(options.PartitionKey, $"events-{audit.Id:D}");
            Assert.Equal(time.GetUtcNow().ToString("O"), Assert.IsType<string>(receipt["CreatedOn"]));
            Assert.Equal(time.GetUtcNow().ToString("O"), Assert.IsType<string>(receipt["DeliveredOn"]));
        }
        await owner.ReleaseAsync(default);
        options.StartFrom = "2026-09-08T00:00:00Z";
        options.Validate();
        var restarted = Store(table, options, time);
        Assert.True(await restarted.TryAcquireAsync(default));
        Assert.Equal(checkpoint, restarted.Checkpoint);
        Assert.True(await restarted.IsDeliveredAsync("events", first.Id, default));
        Assert.True(await restarted.IsDeliveredAsync("events", second.Id, default));
    }

    [Fact]
    public async Task NullCheckpointRemainsNullAcrossEmptySaveAndRestart()
    {
        var options = Options();
        options.StartFrom = null;
        options.Validate();
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        Assert.Null(owner.Checkpoint);
        await owner.SaveCheckpointAsync(null, default);
        await owner.ReleaseAsync(default);
        var restarted = Store(table, options, time);
        Assert.True(await restarted.TryAcquireAsync(default));
        Assert.Null(restarted.Checkpoint);
        Assert.Equal("", table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
    }

    [Theory]
    [InlineData("Version")]
    [InlineData("Organization")]
    [InlineData("Destinations")]
    [InlineData("Checkpoint")]
    [InlineData("InitialCheckpoint")]
    [InlineData("LeaseExpires")]
    public async Task IncompatibleOrCorruptStateIsRejectedWithoutWriting(string property)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        Assert.True(await Store(table, options, time).TryAcquireAsync(default));
        var corrupted = table.Snapshot(options.PartitionKey);
        corrupted[property] = property == "Version" ? 2 : "incompatible";
        table.Seed(corrupted);
        var before = table.Snapshot(options.PartitionKey);

        await Assert.ThrowsAsync<StateConfigurationException>(() => Store(table, options, time).TryAcquireAsync(default));

        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
    }

    [Fact]
    [Trait("Category", "SourceDefect")]
    public async Task WronglyTypedSchemaVersionIsReportedAsStateConfigurationError()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        Assert.True(await Store(table, options, time).TryAcquireAsync(default));
        var corrupted = table.Snapshot(options.PartitionKey);
        corrupted["Version"] = "1";
        table.Seed(corrupted);
        var before = table.Snapshot(options.PartitionKey);

        var failure = await Record.ExceptionAsync(() => Store(table, options, time).TryAcquireAsync(default));

        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        Assert.IsType<StateConfigurationException>(failure);
    }

    [Fact]
    public async Task ChangedDestinationForSameStateIdRequiresNewState()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var original = Options();
        Assert.True(await Store(table, original, time).TryAcquireAsync(default));
        var changed = Options();
        changed.EventHubName = "another-hub";
        changed.Validate();
        Assert.Equal(original.PartitionKey, changed.PartitionKey);

        await Assert.ThrowsAsync<StateConfigurationException>(() => Store(table, changed, time).TryAcquireAsync(default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackwardsOrNullCheckpointRejectedWithoutMutatingDurableState(bool saveNull)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        await owner.SaveCheckpointAsync(time.GetUtcNow(), default);
        var before = table.Snapshot(options.PartitionKey);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.SaveCheckpointAsync(saveNull ? null : time.GetUtcNow().AddTicks(-1), default));

        Assert.Equal(time.GetUtcNow(), owner.Checkpoint);
        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        await owner.RenewAsync(default);
        Assert.Equal(before.GetString("Checkpoint"), table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(412)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task FailedCheckpointDoesNotAdvanceAndInvalidatesLocalOwnership(int status)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        table.Failure = (operation, _) => operation == "update" ? new RequestFailedException(status, "Injected failure") : null;

        var failure = await Record.ExceptionAsync(() => owner.SaveCheckpointAsync(time.GetUtcNow(), default));

        if (status is 404 or 409 or 412)
            Assert.IsType<OwnershipLostException>(failure);
        else
            Assert.Equal(status, Assert.IsType<RequestFailedException>(failure).Status);
        Assert.Equal(options.InitialCheckpoint, owner.Checkpoint);
        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        Assert.Equal(before.GetString("Checkpoint"), table.Snapshot(options.PartitionKey).GetString("Checkpoint"));
        table.Failure = null;
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.RenewAsync(default));
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.SaveCheckpointAsync(time.GetUtcNow(), default));
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await owner.TryAcquireAsync(default));
        Assert.Equal(options.InitialCheckpoint, owner.Checkpoint);
        await owner.SaveCheckpointAsync(time.GetUtcNow(), default);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("add")]
    [InlineData("update")]
    public async Task TransientAcquisitionFailureDoesNotGrantOwnershipAndCanBeRetried(string operation)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        if (operation == "update")
        {
            Assert.True(await Store(table, options, time).TryAcquireAsync(default));
            time.Advance(TimeSpan.FromSeconds(60));
        }
        var owner = Store(table, options, time);
        table.Failure = (attempt, _) => attempt == operation ? new RequestFailedException(503, "Injected failure") : null;

        await Assert.ThrowsAsync<RequestFailedException>(() => owner.TryAcquireAsync(default));
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.SaveCheckpointAsync(time.GetUtcNow(), default));

        table.Failure = null;
        Assert.True(await owner.TryAcquireAsync(default));
        Assert.Equal(options.InitialCheckpoint, owner.Checkpoint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRenewalOrReleaseCannotBeFollowedByCheckpoint(bool release)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        table.Failure = (operation, _) => operation == "update" ? new RequestFailedException(503, "Injected failure") : null;

        await Assert.ThrowsAsync<RequestFailedException>(() => release ? owner.ReleaseAsync(default) : owner.RenewAsync(default));

        table.Failure = null;
        await Assert.ThrowsAsync<OwnershipLostException>(() => owner.SaveCheckpointAsync(time.GetUtcNow(), default));
        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
        Assert.False(await Store(table, options, time).TryAcquireAsync(default));
    }

    [Fact]
    public async Task ReceiptsAreIsolatedBySinkAuditAndStatePartitionAndAreIdempotent()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        var audit = new AuditRecord(Guid.NewGuid(), time.GetUtcNow(), "{}");
        Assert.False(await owner.IsDeliveredAsync("files", audit.Id, default));

        await owner.MarkDeliveredAsync("files", audit, default);
        await owner.MarkDeliveredAsync("files", audit, default);

        Assert.True(await owner.IsDeliveredAsync("files", audit.Id, default));
        Assert.False(await owner.IsDeliveredAsync("events", audit.Id, default));
        Assert.False(await owner.IsDeliveredAsync("files", Guid.NewGuid(), default));
        var otherOptions = Options();
        otherOptions.StateId = "other-state";
        Assert.False(await Store(table, otherOptions, time).IsDeliveredAsync("files", audit.Id, default));
        await owner.MarkDeliveredAsync("events", audit, default);
        Assert.True(await owner.IsDeliveredAsync("events", audit.Id, default));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("upsert")]
    public async Task TransientReceiptFailureIsNotReportedAsDelivered(string operation)
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var owner = Store(table, Options(), time);
        var audit = new AuditRecord(Guid.NewGuid(), time.GetUtcNow(), "{}");
        table.Failure = (attempt, _) => attempt == operation ? new RequestFailedException(503, "Injected failure") : null;

        await Assert.ThrowsAsync<RequestFailedException>(async () =>
        {
            if (operation == "read")
                await owner.IsDeliveredAsync("events", audit.Id, default);
            else
                await owner.MarkDeliveredAsync("events", audit, default);
        });

        table.Failure = null;
        Assert.False(await owner.IsDeliveredAsync("events", audit.Id, default));
        await owner.MarkDeliveredAsync("events", audit, default);
        Assert.True(await owner.IsDeliveredAsync("events", audit.Id, default));
    }

    [Fact]
    public async Task CancelledCheckpointDoesNotMutateState()
    {
        var table = new InMemoryTableClient();
        var time = new ManualTimeProvider();
        var options = Options();
        var owner = Store(table, options, time);
        Assert.True(await owner.TryAcquireAsync(default));
        var before = table.Snapshot(options.PartitionKey);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SaveCheckpointAsync(time.GetUtcNow(), cancellation.Token));

        Assert.Equal(options.InitialCheckpoint, owner.Checkpoint);
        Assert.Equal(before.ETag, table.Snapshot(options.PartitionKey).ETag);
    }

    internal static ExporterOptions Options()
    {
        var options = new ExporterOptions
        {
            OrganizationName = "unit-test",
            StorageTableEndpoint = "https://unit-test.table.core.windows.net",
            EventHubNamespace = "unit-test.servicebus.windows.net",
            EventHubName = "audits",
            StartFrom = "2026-09-01T00:00:00.1234567+00:00"
        };
        options.Validate();
        return options;
    }

    private static TableExportStateStore Store(InMemoryTableClient table, ExporterOptions options, TimeProvider time) =>
        new(table, options, time);
}