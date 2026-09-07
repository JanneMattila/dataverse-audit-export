using Azure;
using Azure.Core;
using Azure.Data.Tables;

namespace DataverseAuditExporter.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.Parse("2026-09-07T12:00:00.1234567+00:00");
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan elapsed) => now += elapsed;
}

internal sealed class InMemoryTableClient : TableClient
{
    private readonly object gate = new();
    private readonly Dictionary<(string Partition, string Row), TableEntity> rows = [];
    private int version;
    public Func<string, Task>? AfterRead { get; set; }
    public Func<string, string, Exception?>? Failure { get; set; }
    public int ConditionalConflicts { get; private set; }

    public TableEntity Snapshot(string partition, string row = "checkpoint")
    {
        lock (gate)
            return Copy(rows[(partition, row)]);
    }

    public void Seed(TableEntity entity)
    {
        lock (gate)
            Store(entity);
    }

    public override async Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(string partitionKey, string rowKey,
        IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowFailure("read", rowKey);
        TableEntity? snapshot;
        lock (gate)
            snapshot = rows.TryGetValue((partitionKey, rowKey), out var entity) ? Copy(entity) : null;
        if (AfterRead is { } afterRead)
            await afterRead(rowKey);
        return new OptionalEntity<T>(snapshot is null ? default : (T)(object)snapshot, snapshot is not null);
    }

    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowFailure("add", entity.RowKey);
        lock (gate)
        {
            if (rows.ContainsKey((entity.PartitionKey, entity.RowKey)))
            {
                ConditionalConflicts++;
                throw new RequestFailedException(409, "Entity already exists.");
            }
            return Task.FromResult(Store((TableEntity)(object)entity));
        }
    }

    public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch,
        TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowFailure("update", entity.RowKey);
        Assert.Equal(TableUpdateMode.Replace, mode);
        Assert.NotEqual(ETag.All, ifMatch);
        lock (gate)
        {
            if (!rows.TryGetValue((entity.PartitionKey, entity.RowKey), out var stored))
                throw new RequestFailedException(404, "Entity missing.");
            if (stored.ETag != ifMatch)
            {
                ConditionalConflicts++;
                throw new RequestFailedException(412, "ETag mismatch.");
            }
            return Task.FromResult(Store((TableEntity)(object)entity));
        }
    }

    public override Task<Response> UpsertEntityAsync<T>(T entity,
        TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowFailure("upsert", entity.RowKey);
        Assert.Equal(TableUpdateMode.Replace, mode);
        lock (gate)
            return Task.FromResult(Store((TableEntity)(object)entity));
    }

    private void ThrowFailure(string operation, string row)
    {
        if (Failure?.Invoke(operation, row) is { } exception)
            throw exception;
    }

    private Response Store(TableEntity entity)
    {
        var stored = Copy(entity);
        stored.ETag = new ETag($"{++version}");
        rows[(stored.PartitionKey, stored.RowKey)] = stored;
        return new TableResponse(stored.ETag.ToString("H"));
    }

    private static TableEntity Copy(TableEntity entity) => new(entity.ToDictionary(pair => pair.Key, pair => pair.Value))
    {
        PartitionKey = entity.PartitionKey,
        RowKey = entity.RowKey,
        Timestamp = entity.Timestamp,
        ETag = entity.ETag
    };

    private sealed class OptionalEntity<T>(T? value, bool hasValue) : NullableResponse<T>
    {
        public override bool HasValue => hasValue;
        public override T Value => hasValue ? value! : throw new InvalidOperationException("No entity.");
        public override Response GetRawResponse() => new TableResponse(null, hasValue ? 200 : 404);
    }

    private sealed class TableResponse(string? etag, int status = 204) : Response
    {
        public override int Status => status;
        public override string ReasonPhrase => "Test response";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "test";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => etag is not null && name.Equals("ETag", StringComparison.OrdinalIgnoreCase);
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => etag is null ? [] : [new HttpHeader("ETag", etag)];
        protected override bool TryGetHeader(string name, out string value)
        {
            value = ContainsHeader(name) ? etag! : "";
            return ContainsHeader(name);
        }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = ContainsHeader(name) ? [etag!] : [];
            return ContainsHeader(name);
        }
    }
}