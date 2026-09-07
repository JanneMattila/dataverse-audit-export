using Azure.Identity;

namespace DataverseAuditExporter.Tests;

public sealed class CredentialFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public void ManagedIdentitySelectionCreatesOnlyManagedIdentityCredential(string? clientId)
    {
        var options = new ExporterOptions { ManagedIdentityClientId = clientId };

        var credential = CredentialFactory.Create(options);

        Assert.IsType<ManagedIdentityCredential>(credential);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("22222222-2222-2222-2222-222222222222")]
    public void AzureCliSelectionCreatesOnlyAzureCliCredential(string? tenantId)
    {
        var options = new ExporterOptions { AuthenticationMode = "AzureCli", TenantId = tenantId };

        var credential = CredentialFactory.Create(options);

        Assert.IsType<AzureCliCredential>(credential);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Default")]
    [InlineData("azurecli")]
    [InlineData("ClientSecret")]
    public void UnsupportedModeFailsWithoutCredentialFallback(string mode)
    {
        var options = new ExporterOptions { AuthenticationMode = mode };

        Assert.Throws<ArgumentException>(() => CredentialFactory.Create(options));
    }
}