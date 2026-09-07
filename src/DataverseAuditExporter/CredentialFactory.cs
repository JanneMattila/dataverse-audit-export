using Azure.Core;
using Azure.Identity;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DataverseAuditExporter.Tests")]

namespace DataverseAuditExporter;

public static class CredentialFactory
{
    public static TokenCredential Create(ExporterOptions options) => Create(options, IsHostedEnvironment());

    internal static TokenCredential Create(ExporterOptions options, bool hostedEnvironment)
    {
        var credentials = CreateSources(options, hostedEnvironment);
        return credentials.Count == 1 ? credentials[0] : new ChainedTokenCredential([.. credentials]);
    }

    internal static IReadOnlyList<TokenCredential> CreateSources(ExporterOptions options, bool hostedEnvironment)
    {
        if (options.AuthenticationMode is not ("ManagedIdentity" or "AzureCli"))
            throw new ArgumentException("Unsupported AuthenticationMode.");
        if (options.AuthenticationMode == "AzureCli" && hostedEnvironment)
            throw new ArgumentException("AzureCli authentication is allowed only for local, non-container runs.");

        List<TokenCredential> credentials =
        [
            string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId))
        ];
        if (!string.IsNullOrWhiteSpace(options.ClientId) || !string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            if (string.IsNullOrWhiteSpace(options.TenantId) || string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.ClientSecret))
                throw new ArgumentException("Client-secret authentication requires TenantId, ClientId and ClientSecret together.");
            credentials.Add(new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret));
        }
        if (options.AuthenticationMode == "AzureCli")
            credentials.Add(new AzureCliCredential(new AzureCliCredentialOptions { TenantId = options.TenantId }));
        return credentials;
    }

    private static bool IsHostedEnvironment() =>
        new[]
        {
            "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_RUNNING_IN_CONTAINERS",
            "IDENTITY_ENDPOINT", "MSI_ENDPOINT", "IMDS_ENDPOINT", "IDENTITY_SERVER_THUMBPRINT",
            "CONTAINER_APP_NAME", "CONTAINER_APP_ENV_DNS_SUFFIX", "WEBSITE_INSTANCE_ID",
            "WEBSITE_SITE_NAME", "KUBERNETES_SERVICE_HOST", "AZURE_HTTP_USER_AGENT"
        }.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) ||
        File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
}