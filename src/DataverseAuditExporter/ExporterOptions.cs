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
    public string? BlobStorageEndpoint { get; set; }
    public string? BlobContainerName { get; set; }
    public string? EventHubNamespace { get; set; }
    public string? EventHubName { get; set; }
    public int IntervalSeconds { get; set; } = 5;
    public string? StartFrom { get; set; }
    public string AuthenticationMode { get; set; } = "ManagedIdentity";
    public string? ManagedIdentityClientId { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public Uri OrganizationUri { get; private set; } = null!;
    public IReadOnlyList<ExporterOptions> Organizations { get; private set; } = [];
    public DateTimeOffset? InitialCheckpoint { get; private set; }
    public string PartitionKey => Hash(JsonSerializer.Serialize(new[] { OrganizationUri.AbsoluteUri, StateId }));
    public string DestinationFingerprint => Hash(JsonSerializer.Serialize(BlobStorageEndpoint is null
        ? new[] { ExportPath, EventHubNamespace, EventHubName }
        : new[] { ExportPath, EventHubNamespace, EventHubName, BlobStorageEndpoint, BlobContainerName }));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public void Validate()
    {
        string[] names;
        try
        {
            names = OrganizationName?.TrimStart().StartsWith('[') == true
                ? JsonSerializer.Deserialize<string[]>(OrganizationName) ?? []
                : [OrganizationName!];
        }
        catch (JsonException)
        {
            throw new ArgumentException("OrganizationName must be a single organization or a JSON array of organization strings.");
        }
        if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("OrganizationName is required and must not contain empty organizations.");

        ValidateSingle(names[0]);
        var organizations = new List<ExporterOptions>();
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var organization = (ExporterOptions)MemberwiseClone();
            organization.OrganizationName = name;
            organization.Organizations = [];
            organization.ValidateSingle(name);
            if (!origins.Add(organization.OrganizationUri.AbsoluteUri))
                throw new ArgumentException("OrganizationName contains duplicate organizations after URL normalization.");
            if (ExportPath is not null || BlobStorageEndpoint is not null)
            {
                if (!directories.Add(organization.OrganizationUri.Host))
                    throw new ArgumentException("File and Blob output require distinct organization hostnames.");
                if (ExportPath is not null)
                    organization.ExportPath = Path.Combine(ExportPath, organization.OrganizationUri.Host);
            }
            organizations.Add(organization);
        }
        Organizations = organizations;
    }

    private void ValidateSingle(string organizationName)
    {
        if (string.IsNullOrWhiteSpace(organizationName))
            throw new ArgumentException("OrganizationName is required.");
        var organization = organizationName.Trim().TrimEnd('/');
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
        BlobStorageEndpoint = string.IsNullOrWhiteSpace(BlobStorageEndpoint) ? null
            : ValidateOrigin(BlobStorageEndpoint.Trim(), "BlobStorageEndpoint").AbsoluteUri;
        BlobContainerName = string.IsNullOrWhiteSpace(BlobContainerName) ? null : BlobContainerName.Trim();
        if ((BlobStorageEndpoint is null) != (BlobContainerName is null))
            throw new ArgumentException("BlobStorageEndpoint and BlobContainerName must be supplied together.");
        if (BlobContainerName is not null &&
            (!Regex.IsMatch(BlobContainerName, "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$") || BlobContainerName.Contains("--")))
            throw new ArgumentException("BlobContainerName must contain 3-63 lowercase letters, digits or single hyphens, starting and ending with a letter or digit.");
        EventHubNamespace = string.IsNullOrWhiteSpace(EventHubNamespace) ? null : EventHubNamespace.Trim().ToLowerInvariant();
        EventHubName = string.IsNullOrWhiteSpace(EventHubName) ? null : EventHubName.Trim();
        if ((EventHubNamespace is null) != (EventHubName is null))
            throw new ArgumentException("EventHubNamespace and EventHubName must be supplied together.");
        if (EventHubNamespace is not null && (Uri.CheckHostName(EventHubNamespace) != UriHostNameType.Dns || !EventHubNamespace.Contains('.')))
            throw new ArgumentException("EventHubNamespace must be a fully qualified host name, not a connection string or URL.");
        if (ExportPath is null && EventHubNamespace is null && BlobStorageEndpoint is null)
            throw new ArgumentException("Configure at least one output: ExportPath, EventHubNamespace and EventHubName, or BlobStorageEndpoint and BlobContainerName.");
        if (AuthenticationMode is not ("ManagedIdentity" or "AzureCli"))
            throw new ArgumentException("AuthenticationMode must be ManagedIdentity or AzureCli.");
        if (AuthenticationMode == "AzureCli" && !string.IsNullOrWhiteSpace(ManagedIdentityClientId))
            throw new ArgumentException("ManagedIdentityClientId cannot be used with AzureCli authentication.");
        if (!string.IsNullOrWhiteSpace(ManagedIdentityClientId) && !Guid.TryParse(ManagedIdentityClientId, out _))
            throw new ArgumentException("ManagedIdentityClientId must be a GUID.");
        if ((!string.IsNullOrWhiteSpace(ClientId) || !string.IsNullOrWhiteSpace(ClientSecret)) &&
            (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret)))
            throw new ArgumentException("Client-secret authentication requires TenantId, ClientId and ClientSecret together.");
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