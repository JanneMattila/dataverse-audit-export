using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataverseAuditExporter;

public sealed class ExporterOptions
{
    public string OrganizationName { get; set; } = "";
    public string StorageTableEndpoint { get; set; } = "";
    public string StateTableName { get; set; } = "DataverseAuditExporter";
    public string StateId { get; set; } = "default";
    public string? ExportPath { get; set; }
    public string? EventHubNamespace { get; set; }
    public string? EventHubName { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public string? StartFrom { get; set; }
    public string AuthenticationMode { get; set; } = "ManagedIdentity";
    public string? ManagedIdentityClientId { get; set; }
    public string? TenantId { get; set; }

    public Uri OrganizationUri { get; private set; } = null!;
    public DateTimeOffset? InitialCheckpoint { get; private set; }
    public string PartitionKey => Hash(JsonSerializer.Serialize(new[] { OrganizationUri.AbsoluteUri, StateId }));
    public string DestinationFingerprint => Hash(JsonSerializer.Serialize(new[] { ExportPath, EventHubNamespace, EventHubName }));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OrganizationName))
            throw new ArgumentException("OrganizationName is required.");
        var organization = OrganizationName.Trim().TrimEnd('/');
        if (!organization.Contains("://", StringComparison.Ordinal))
            organization = "https://" + (organization.Contains('.') ? organization : organization + ".crm.dynamics.com");
        OrganizationUri = ValidateOrigin(organization, "OrganizationName");
        StorageTableEndpoint = ValidateOrigin(StorageTableEndpoint, "StorageTableEndpoint").AbsoluteUri;
        if (!Regex.IsMatch(StateTableName, "^[A-Za-z][A-Za-z0-9]{2,62}$"))
            throw new ArgumentException("StateTableName must contain 3-63 alphanumeric characters, starting with a letter.");
        if (string.IsNullOrWhiteSpace(StateId))
            throw new ArgumentException("StateId must not be empty.");
        if (IntervalSeconds is < 1 or > 86400)
            throw new ArgumentException("IntervalSeconds must be between 1 and 86400.");
        ExportPath = string.IsNullOrWhiteSpace(ExportPath) ? null : Path.GetFullPath(ExportPath);
        EventHubNamespace = string.IsNullOrWhiteSpace(EventHubNamespace) ? null : EventHubNamespace.Trim().ToLowerInvariant();
        EventHubName = string.IsNullOrWhiteSpace(EventHubName) ? null : EventHubName.Trim();
        if ((EventHubNamespace is null) != (EventHubName is null))
            throw new ArgumentException("EventHubNamespace and EventHubName must be supplied together.");
        if (EventHubNamespace is not null && (Uri.CheckHostName(EventHubNamespace) != UriHostNameType.Dns || !EventHubNamespace.Contains('.')))
            throw new ArgumentException("EventHubNamespace must be a fully qualified host name, not a connection string or URL.");
        if (ExportPath is null && EventHubNamespace is null)
            throw new ArgumentException("Configure ExportPath or EventHubNamespace and EventHubName (or both).");
        if (AuthenticationMode is not ("ManagedIdentity" or "AzureCli"))
            throw new ArgumentException("AuthenticationMode must be ManagedIdentity or AzureCli.");
        if (AuthenticationMode == "AzureCli" && !string.IsNullOrWhiteSpace(ManagedIdentityClientId))
            throw new ArgumentException("ManagedIdentityClientId cannot be used with AzureCli authentication.");
        if (!string.IsNullOrWhiteSpace(ManagedIdentityClientId) && !Guid.TryParse(ManagedIdentityClientId, out _))
            throw new ArgumentException("ManagedIdentityClientId must be a GUID.");
        InitialCheckpoint = null;
        if (!string.IsNullOrWhiteSpace(StartFrom))
        {
            if (!Regex.IsMatch(StartFrom, "(Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.IgnoreCase) ||
                !DateTimeOffset.TryParse(StartFrom, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                throw new ArgumentException("StartFrom must be an ISO timestamp with a UTC offset or Z.");
            InitialCheckpoint = timestamp.ToUniversalTime();
        }
    }

    private static Uri ValidateOrigin(string value, string name)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException($"{name} must identify an HTTPS origin without credentials, path, query or fragment.");
        return uri;
    }
}