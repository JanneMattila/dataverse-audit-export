using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataverseAuditExporter.Tests;

public sealed class DataverseAuditClientTests
{
    private const string Timestamp = "2026-09-07T12:34:56.1234567Z";
    private const string FirstId = "00000000-0000-0000-0000-000000000001";
    private const string SecondId = "00000000-0000-0000-0000-000000000002";

    [Fact]
    public async Task EqualFractionalTimestampsAcrossPagesPreserveEveryRecordAndContinuationQuery()
    {
        const string continuation = "https://example.crm.dynamics.com/api/data/v9.2/audits?$skiptoken=opaque%2Btoken%26value";
        using var harness = new ClientHarness(
            JsonResponse(Page([RecordJson(FirstId), RecordJson(SecondId)], continuation)),
            JsonResponse(Page([RecordJson("00000000-0000-0000-0000-000000000003")])));
        var checkpoint = DateTimeOffset.Parse(Timestamp);

        var pages = await ReadAllAsync(harness.Client, checkpoint);

        Assert.Equal(new[] { 2, 1 }, pages.Select(page => page.Count));
        Assert.Equal(3, pages.SelectMany(page => page).Select(audit => audit.Id).Distinct().Count());
        Assert.All(pages.SelectMany(page => page), audit => Assert.Equal(checkpoint, audit.CreatedOn));
        var firstQuery = Uri.UnescapeDataString(harness.Handler.Requests[0].Uri.Query);
        Assert.Contains("$orderby=createdon asc", firstQuery);
        Assert.Contains("$filter=createdon ge " + Timestamp, firstQuery);
        Assert.Equal(continuation, harness.Handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal(1, harness.Credential.Calls);
        Assert.Equal(new[] { "https://example.crm.dynamics.com/.default" }, harness.Credential.Scopes);
    }

    [Theory]
    [InlineData("https://attacker.example/api/data/v9.2/audits")]
    [InlineData("//attacker.example/api/data/v9.2/audits")]
    [InlineData("http://example.crm.dynamics.com/api/data/v9.2/audits")]
    [InlineData("https://example.crm.dynamics.com:444/api/data/v9.2/audits")]
    [InlineData("https://user@example.crm.dynamics.com/api/data/v9.2/audits")]
    [InlineData("https://example.crm.dynamics.com/other/audits")]
    [InlineData("https://example.crm.dynamics.com/api/data/v9.2/audits#fragment")]
    public async Task UnsafeContinuationIsRejectedBeforeAnyCredentialCanBeSentToIt(string continuation)
    {
        using var harness = new ClientHarness(JsonResponse(Page([RecordJson(FirstId)], continuation)));

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(harness.Client));

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("example.crm.dynamics.com", request.Uri.Host);
        Assert.Equal("Bearer unit-test-token", request.Authorization);
        Assert.Equal(1, harness.Credential.Calls);
    }

    [Fact]
    public async Task ZeroRetryAfterRetriesSameRequestAndThenReturnsRecords()
    {
        using var harness = new ClientHarness(ThrottledResponse(), JsonResponse(Page([RecordJson(FirstId)])));

        var pages = await ReadAllAsync(harness.Client);

        Assert.Single(Assert.Single(pages));
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(harness.Handler.Requests[0].Uri, harness.Handler.Requests[1].Uri);
        Assert.All(harness.Handler.Requests, request => Assert.Equal("Bearer unit-test-token", request.Authorization));
        Assert.Equal(1, harness.Credential.Calls);
    }

    [Fact]
    public async Task ZeroRetryAfterIsBoundedToFiveAttempts()
    {
        using var harness = new ClientHarness(Enumerable.Range(0, 5).Select(_ => ThrottledResponse()).ToArray());

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => ReadAllAsync(harness.Client));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Equal(5, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task RawAnnotationsAndUnknownPropertiesArePreservedWithRequestHeaders()
    {
        var raw = $$"""{ "auditid": "{{FirstId}}", "createdon": "2026-09-07T14:34:56.1234567+02:00", "action@OData.Community.Display.V1.FormattedValue": "Updated", "unknown": { "nested": [1, true, null] } }""";
        using var harness = new ClientHarness(JsonResponse(Page([raw])));

        var audit = Assert.Single(Assert.Single(await ReadAllAsync(harness.Client)));

        Assert.Equal(raw, audit.Json);
        Assert.Equal(DateTimeOffset.Parse(Timestamp), audit.CreatedOn);
        Assert.Equal(TimeSpan.Zero, audit.CreatedOn.Offset);
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Contains("odata.include-annotations=\"*\"", request.Prefer);
        Assert.Contains("odata.maxpagesize=5000", request.Prefer);
        Assert.Equal("4.0", request.ODataVersion);
        Assert.Equal("4.0", request.ODataMaxVersion);
        Assert.Equal("application/json", request.Accept);
        Assert.DoesNotContain("$filter", request.Uri.Query);
    }

    [Fact]
    public async Task EmptyPageStillFollowsContinuationAndEmptyTerminalPageIsReturned()
    {
        using var harness = new ClientHarness(
            JsonResponse(Page([], "/api/data/v9.2/audits?$skiptoken=second")),
            JsonResponse(Page([RecordJson(FirstId)], "/api/data/v9.2/audits?$skiptoken=third")),
            JsonResponse(Page([])));

        var pages = await ReadAllAsync(harness.Client);

        Assert.Equal(new[] { 0, 1, 0 }, pages.Select(page => page.Count));
        Assert.Equal(3, harness.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"auditid\":\"invalid\",\"createdon\":\"2026-09-07T00:00:00Z\"}")]
    [InlineData("{\"auditid\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"auditid\":\"00000000-0000-0000-0000-000000000001\",\"createdon\":\"invalid\"}")]
    public async Task MalformedAuditFailsWithoutYieldingAPartialPage(string malformed)
    {
        using var harness = new ClientHarness(JsonResponse(Page([RecordJson(FirstId), malformed])));
        var yieldedPages = 0;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var page in harness.Client.ReadPagesAsync(null, CancellationToken.None))
                yieldedPages++;
        });

        Assert.Equal(0, yieldedPages);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("not json", typeof(JsonException))]
    [InlineData("{}", typeof(KeyNotFoundException))]
    [InlineData("{\"value\":{}}", typeof(InvalidOperationException))]
    [InlineData("{\"value\":[{\"auditid\":42,\"createdon\":\"2026-09-07T00:00:00Z\"}]}", typeof(InvalidOperationException))]
    public async Task MalformedSourceEnvelopeOrPropertyTypesFailClosed(string response, Type exceptionType)
    {
        using var harness = new ClientHarness(JsonResponse(response));

        var exception = await Record.ExceptionAsync(() => ReadAllAsync(harness.Client));
        Assert.NotNull(exception);
        Assert.IsAssignableFrom(exceptionType, exception);

        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    public async Task CancellationBeforeReadingDoesNotSendHttpRequest()
    {
        using var harness = new ClientHarness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var page in harness.Client.ReadPagesAsync(null, cancellation.Token))
                Assert.Fail("A canceled read must not return a page.");
        });

        Assert.Empty(harness.Handler.Requests);
    }

    private static string RecordJson(string id) => $$"""{"auditid":"{{id}}","createdon":"{{Timestamp}}"}""";

    private static string Page(string[] records, string? continuation = null) =>
        "{\"value\":[" + string.Join(',', records) + "]" +
        (continuation is null ? "" : ",\"@odata.nextLink\":" + JsonSerializer.Serialize(continuation)) + "}";

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ThrottledResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        return response;
    }

    private static async Task<List<IReadOnlyList<AuditRecord>>> ReadAllAsync(DataverseAuditClient client, DateTimeOffset? checkpoint = null)
    {
        var pages = new List<IReadOnlyList<AuditRecord>>();
        await foreach (var page in client.ReadPagesAsync(checkpoint, CancellationToken.None))
            pages.Add(page);
        return pages;
    }

    private sealed class ClientHarness : IDisposable
    {
        private readonly HttpClient http;
        public RecordingHandler Handler { get; }
        public FakeCredential Credential { get; } = new();
        public DataverseAuditClient Client { get; }

        public ClientHarness(params HttpResponseMessage[] responses)
        {
            Handler = new RecordingHandler(responses);
            http = new HttpClient(Handler);
            var options = ExporterOptionsTests.ValidOptions();
            options.Validate();
            Client = new DataverseAuditClient(http, Credential, options, NullLogger<DataverseAuditClient>.Instance);
        }

        public void Dispose() => http.Dispose();
    }

    private sealed record RequestSnapshot(Uri Uri, string Authorization, string Prefer, string ODataVersion, string ODataMaxVersion, string Accept);

    private sealed class RecordingHandler(HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> pending = new(responses);
        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!, request.Headers.Authorization!.ToString(),
                string.Join(',', request.Headers.GetValues("Prefer")),
                request.Headers.GetValues("OData-Version").Single(),
                request.Headers.GetValues("OData-MaxVersion").Single(), request.Headers.Accept.ToString()));
            Assert.NotEmpty(pending);
            return Task.FromResult(pending.Dequeue());
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                foreach (var response in pending)
                    response.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Scopes = requestContext.Scopes;
            return new AccessToken("unit-test-token", DateTimeOffset.MaxValue);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}