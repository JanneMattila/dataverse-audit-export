using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace DataverseAuditExporter;

public static class ApplicationConfiguration
{
    public static ExporterOptions Load(string[] args)
    {
        var known = typeof(ExporterOptions).GetProperties().Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            var name = argument.Split('=', 2)[0];
            if (!name.StartsWith("--", StringComparison.Ordinal) || !known.Contains(name[2..]))
                throw new ArgumentException($"Unknown option '{name}'. Run with --help for supported options.");
            if (!argument.Contains('='))
            {
                index++;
                if (index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"A value is required for {name}.");
            }
        }
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddInMemoryCollection(ClientCredentialEnvironment())
            .AddEnvironmentVariables("DATAVERSE_EXPORTER_")
            .AddCommandLine(args)
            .Build();
        return Load(configuration);
    }

    internal static ExporterOptions Load(IConfiguration configuration)
    {
        var options = new ExporterOptions();
        configuration.Bind(options);
        var organizationSection = configuration.GetSection("OrganizationName");
        if (organizationSection.Value is null && organizationSection.GetChildren().Any())
            options.OrganizationName = JsonSerializer.Serialize(organizationSection.Get<string[]>());
        options.Validate();
        return options;
    }

    private static IEnumerable<KeyValuePair<string, string?>> ClientCredentialEnvironment()
    {
        (string Option, string Primary, string Alias)[] names =
        [
            ("TenantId", "DATAVERSE_TENANT_ID", "tenant"),
            ("ClientId", "DATAVERSE_CLIENT_ID", "client_id"),
            ("ClientSecret", "DATAVERSE_CLIENT_SECRET", "client_secret")
        ];
        foreach (var (option, primary, alias) in names)
        {
            var value = Environment.GetEnvironmentVariable(primary);
            if (string.IsNullOrWhiteSpace(value))
                value = Environment.GetEnvironmentVariable(alias);
            if (!string.IsNullOrWhiteSpace(value))
                yield return new(option, value);
        }
    }

    public const string Help = """
        Dataverse Audit Exporter: continuously poll until Ctrl+C.
        Usage: dotnet DataverseAuditExporter.dll --OrganizationName <org> --StorageTableEndpoint <https://account.table.core.windows.net> [options]

        --OrganizationName <org-or-array>    One organization or a JSON array, processed sequentially.
        --ExportPath <directory>             Enable JSON file output (disabled by default).
        --BlobStorageEndpoint <https-url>    Enable Blob output with --BlobContainerName.
        --BlobContainerName <name>           Existing private Blob container.
        --EventHubNamespace <host>           Enable Event Hubs output with --EventHubName.
        --EventHubName <name>                Destination event hub.
        --StateTableName <name>              Existing table (default: DataverseAuditExporter).
        --StateId <id>                       Export state namespace (default: default).
        --IntervalSeconds <1..86400>         Target round interval (default: 5); minimum 1-second sleep after each round.
        --StartFrom <ISO timestamp>          Starting timestamp for fresh state only.
        --AuthenticationMode <mode>          ManagedIdentity (default); AzureCli permits final fallback locally only.
        --ManagedIdentityClientId <guid>     User-assigned identity; omit for system-assigned.
        --TenantId <tenant>                  Tenant for client-secret or Azure CLI authentication.
        --ClientId <guid>                    Application client ID for optional secret fallback.
        --ClientSecret <secret>              Prefer DATAVERSE_EXPORTER_ClientSecret environment variable.

        At least one output is required. Any combination of outputs can be enabled.
        Precedence: CLI > DATAVERSE_EXPORTER_<Option> environment variables > appsettings.json.
        Secret aliases: DATAVERSE_TENANT_ID/CLIENT_ID/CLIENT_SECRET or tenant/client_id/client_secret.
        Aliases override appsettings.json; prefixed exporter variables override aliases.
        Authentication: managed identity first, then configured client secret, then local-only opted-in Azure CLI.
        Fallback occurs only for unavailable credentials, not authentication or authorization failures.
        Docker --env-file supplies environment variables; the app does not open .env files.
        Example organization value: ["contoso.crm4.dynamics.com","fabrikam.crm4.dynamics.com"]
        File output always uses ExportPath/<organization-host>/yyyy/MM/dd/<auditid>.json.
        Blob output uses <organization-host>/yyyy/MM/dd/<auditid>.json; existing blobs are not overwritten.
        Event Hubs delivery is at least once. Consumers deduplicate by organization and auditid.
        """;
}