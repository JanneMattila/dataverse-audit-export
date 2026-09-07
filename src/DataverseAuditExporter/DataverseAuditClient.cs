using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace DataverseAuditExporter;

public sealed class DataverseAuditClient(HttpClient http, TokenCredential credential, ExporterOptions options,
    ILogger<DataverseAuditClient> logger) : IAuditSource
{
    private AccessToken token;

    public Uri GetAuditsUri(DateTimeOffset? checkpoint)
    {
        var query = "api/data/v9.2/audits?$orderby=createdon asc";
        if (checkpoint.HasValue)
        {
            var timestamp = checkpoint.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            query += "&$filter=" + Uri.EscapeDataString("createdon ge " + timestamp);
        }
        return new Uri(options.OrganizationUri, query);
    }

    public async IAsyncEnumerable<IReadOnlyList<AuditRecord>> ReadPagesAsync(DateTimeOffset? checkpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Uri? next = GetAuditsUri(checkpoint);
        while (next is not null)
        {
            ValidateRequestUri(next);
            using var response = await GetAsync(next, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var records = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(value => AuditRecord.Parse(value, options.OrganizationUri.Host)).ToArray();
            next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) && !string.IsNullOrWhiteSpace(link.GetString())
                ? new Uri(options.OrganizationUri, link.GetString()!) : null;
            yield return records;
        }
    }

    private void ValidateRequestUri(Uri uri)
    {
        if (uri.Scheme != "https" || uri.Authority != options.OrganizationUri.Authority || uri.UserInfo.Length != 0 ||
            !uri.AbsolutePath.StartsWith("/api/data/v9.2/", StringComparison.Ordinal) || uri.Fragment.Length != 0)
            throw new InvalidDataException("Dataverse continuation link points outside the configured API origin.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(2))
                token = await credential.GetTokenAsync(new TokenRequestContext([options.OrganizationUri.GetLeftPart(UriPartial.Authority) + "/.default"]), cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Add("OData-MaxVersion", "4.0");
            request.Headers.Add("OData-Version", "4.0");
            request.Headers.Add("Prefer", "odata.include-annotations=\"*\",odata.maxpagesize=5000");
            TimeSpan delay;
            try
            {
                var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return response;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    token = default;
                var transient = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (!transient || attempt >= 4)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new HttpRequestException($"Dataverse request failed with HTTP {(int)status}.", null, status);
                }
                delay = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? Backoff(attempt);
                response.Dispose();
            }
            catch (HttpRequestException exception) when (exception.StatusCode is null && attempt < 4)
            {
                delay = Backoff(attempt);
            }
            logger.LogWarning("Dataverse retry {Attempt} in {Delay}.", attempt + 1, delay);
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, cancellationToken);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)) + Random.Shared.NextDouble());
}