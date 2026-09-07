using Microsoft.Extensions.Configuration;

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
            .AddEnvironmentVariables("DATAVERSE_EXPORTER_")
            .AddCommandLine(args)
            .Build();
        var options = new ExporterOptions();
        configuration.Bind(options);
        options.Validate();
        return options;
    }

    public const string Help = """
        Dataverse Audit Exporter: continuously poll until Ctrl+C.
        Usage: dotnet DataverseAuditExporter.dll --OrganizationName <org> --StorageTableEndpoint <https://account.table.core.windows.net> [options]

        --ExportPath <directory>             Enable JSON file output (disabled by default).
        --EventHubNamespace <host>           Enable Event Hubs output with --EventHubName.
        --EventHubName <name>                Destination event hub.
        --StateTableName <name>              Existing table (default: DataverseAuditExporter).
        --StateId <id>                       Export state namespace (default: default).
        --IntervalSeconds <1..86400>         Polling delay (default: 5).
        --StartFrom <ISO timestamp>          Starting timestamp for fresh state only.
        --AuthenticationMode <mode>          ManagedIdentity (default) or AzureCli locally.
        --ManagedIdentityClientId <guid>     User-assigned identity; omit for system-assigned.
        --TenantId <tenant>                  Optional Azure CLI tenant.

        At least one output is required. Both can be enabled.
        Precedence: CLI > DATAVERSE_EXPORTER_<Option> environment variables > appsettings.json.
        Event Hubs delivery is at least once. Consumers deduplicate by organization and auditid.
        """;
}