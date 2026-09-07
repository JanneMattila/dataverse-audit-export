using System.Text.Json;
using Xunit;

namespace DataverseAuditExporter.Tests;

public sealed class BlobOptionsTests
{
    private static ExporterOptions Options() => new()
    {
        OrganizationName = "contoso",
        StorageTableEndpoint = "https://state.table.core.windows.net",
        BlobStorageEndpoint = "https://exports.blob.core.windows.net",
        BlobContainerName = "audits"
    };

    [Fact]
    public void BlobOnlyOutputHasStableOrganizationIdentity()
    {
        var options = Options();
        options.Validate();
        var original = options.Organizations.Single();
        options.OrganizationName = "[\"second\",\"contoso\"]";
        options.Validate();
        var matching = options.Organizations[1];
        Assert.Null(matching.ExportPath);
        Assert.Equal("https://exports.blob.core.windows.net/", matching.BlobStorageEndpoint);
        Assert.Equal(original.PartitionKey, matching.PartitionKey);
        Assert.Equal(original.DestinationFingerprint, matching.DestinationFingerprint);
        options.BlobContainerName = "other";
        options.Validate();
        Assert.NotEqual(original.DestinationFingerprint, options.Organizations[1].DestinationFingerprint);
    }

    [Fact]
    public void DisabledBlobOutputPreservesExistingFingerprint()
    {
        var options = Options();
        options.BlobStorageEndpoint = null;
        options.BlobContainerName = null;
        options.ExportPath = "exports";
        options.Validate();
        var organization = options.Organizations.Single();
        Assert.Equal(ExporterOptions.Hash(JsonSerializer.Serialize(new[]
            { organization.ExportPath, organization.EventHubNamespace, organization.EventHubName })), organization.DestinationFingerprint);
    }

    [Theory]
    [InlineData(null, "audits")]
    [InlineData("https://exports.blob.core.windows.net", null)]
    [InlineData("http://exports.blob.core.windows.net", "audits")]
    [InlineData("https://exports.blob.core.windows.net/path", "audits")]
    [InlineData("https://exports.blob.core.windows.net/?sig=secret", "audits")]
    [InlineData("https://exports.blob.core.windows.net", "Uppercase")]
    [InlineData("https://exports.blob.core.windows.net", "ab")]
    [InlineData("https://exports.blob.core.windows.net", "audit--logs")]
    public void InvalidBlobSettingsAreRejected(string? endpoint, string? container)
    {
        var options = Options();
        options.BlobStorageEndpoint = endpoint;
        options.BlobContainerName = container;
        Assert.Throws<ArgumentException>(options.Validate);
    }
}