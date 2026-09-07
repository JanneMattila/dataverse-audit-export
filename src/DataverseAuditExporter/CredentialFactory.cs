using Azure.Core;
using Azure.Identity;

namespace DataverseAuditExporter;

public static class CredentialFactory
{
    public static TokenCredential Create(ExporterOptions options) => options.AuthenticationMode switch
    {
        "AzureCli" => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = options.TenantId }),
        "ManagedIdentity" => string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId)),
        _ => throw new ArgumentException("Unsupported AuthenticationMode.")
    };
}