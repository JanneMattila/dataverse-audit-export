using DataverseAuditExporter;

namespace DataverseAuditExporter.Tests;

public sealed class ExporterOptionsTests
{
    internal static ExporterOptions ValidOptions() => new()
    {
        OrganizationName = "example",
        StorageTableEndpoint = "https://example.table.core.windows.net",
        EventHubNamespace = "example.servicebus.windows.net",
        EventHubName = "audits"
    };

    [Fact]
    public void NoOutputIsRejected()
    {
        var options = ValidOptions();
        options.EventHubNamespace = null;
        options.EventHubName = null;
        Assert.Throws<ArgumentException>(options.Validate);
        Assert.Null(options.ExportPath);
    }

    [Theory]
    [InlineData(null, "audits")]
    [InlineData("example.servicebus.windows.net", null)]
    [InlineData(" ", "audits")]
    [InlineData("example.servicebus.windows.net", " ")]
    public void IncompleteHubPairIsRejected(string? hubNamespace, string? hubName)
    {
        var options = ValidOptions();
        options.EventHubNamespace = hubNamespace;
        options.EventHubName = hubName;
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:password@example.com")]
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com/?query=value")]
    [InlineData("https://example.com/#fragment")]
    public void InvalidOriginsAreRejectedForBothEndpoints(string origin)
    {
        var organizationOptions = ValidOptions();
        organizationOptions.OrganizationName = origin;
        Assert.Throws<ArgumentException>(organizationOptions.Validate);
        var storageOptions = ValidOptions();
        storageOptions.StorageTableEndpoint = origin;
        Assert.Throws<ArgumentException>(storageOptions.Validate);
    }

    [Theory]
    [InlineData("OrganizationName", " ")]
    [InlineData("StorageTableEndpoint", "")]
    [InlineData("StateTableName", "ab")]
    [InlineData("StateTableName", "1table")]
    [InlineData("StateTableName", "table_name")]
    [InlineData("StateId", " ")]
    [InlineData("IntervalSeconds", "0")]
    [InlineData("IntervalSeconds", "86401")]
    [InlineData("EventHubNamespace", "https://example.servicebus.windows.net")]
    [InlineData("EventHubNamespace", "localhost")]
    [InlineData("EventHubNamespace", "Endpoint=sb://example/;SharedAccessKey=fake")]
    [InlineData("AuthenticationMode", "DefaultAzureCredential")]
    [InlineData("ManagedIdentityClientId", "not-a-guid")]
    [InlineData("StartFrom", "2026-09-07T12:00:00")]
    [InlineData("StartFrom", "invalidZ")]
    public void InvalidConfigurationIsRejected(string propertyName, string value)
    {
        var options = ValidOptions();
        var property = typeof(ExporterOptions).GetProperty(propertyName)!;
        property.SetValue(options, property.PropertyType == typeof(int) ? int.Parse(value) : value);
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void AzureCliCannotUseManagedIdentityClientId()
    {
        var options = ValidOptions();
        options.AuthenticationMode = "AzureCli";
        options.ManagedIdentityClientId = "00000000-0000-0000-0000-000000000001";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SupportedDestinationsNormalizeWithoutCreatingDirectories(bool files, bool events)
    {
        var options = ValidOptions();
        var path = Path.Combine(Path.GetTempPath(), "audit-options-" + Guid.NewGuid().ToString("N"));
        options.ExportPath = files ? path : null;
        options.EventHubNamespace = events ? " EXAMPLE.servicebus.windows.net " : null;
        options.EventHubName = events ? " audits " : null;
        options.StartFrom = "2026-09-07T01:02:03.1234567+02:00";
        options.Validate();
        Assert.Equal("https://example.crm.dynamics.com/", options.OrganizationUri.AbsoluteUri);
        Assert.Equal(DateTimeOffset.Parse("2026-09-06T23:02:03.1234567Z"), options.InitialCheckpoint);
        Assert.Equal(events ? "example.servicebus.windows.net" : null, options.EventHubNamespace);
        Assert.Equal(events ? "audits" : null, options.EventHubName);
        Assert.Equal(files ? Path.GetFullPath(path) : null, options.ExportPath);
        Assert.False(Directory.Exists(path));
    }
}