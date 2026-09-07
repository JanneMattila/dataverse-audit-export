using Azure.Identity;

namespace DataverseAuditExporter.Tests;

[Collection("Process state")]
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
    public void LocalAzureCliSelectionCreatesCredentialChain(string? tenantId)
    {
        var options = new ExporterOptions { AuthenticationMode = "AzureCli", TenantId = tenantId };

        var credential = CredentialFactory.Create(options, hostedEnvironment: false);

        Assert.IsType<ChainedTokenCredential>(credential);
    }

    [Fact]
    public void ClientSecretConfigurationAddsFallback()
    {
        var credential = CredentialFactory.Create(new ExporterOptions
        {
            TenantId = "22222222-2222-2222-2222-222222222222",
            ClientId = "11111111-1111-1111-1111-111111111111",
            ClientSecret = "synthetic-test-secret"
        });

        Assert.IsType<ChainedTokenCredential>(credential);
    }

    [Theory]
    [InlineData("ManagedIdentity", false)]
    [InlineData("ManagedIdentity", true)]
    [InlineData("AzureCli", false)]
    [InlineData("AzureCli", true)]
    public void SourcesAreOrderedManagedIdentityThenSecretThenLocalCli(string mode, bool secret)
    {
        var sources = CredentialFactory.CreateSources(new ExporterOptions
        {
            AuthenticationMode = mode,
            TenantId = "22222222-2222-2222-2222-222222222222",
            ClientId = secret ? "11111111-1111-1111-1111-111111111111" : null,
            ClientSecret = secret ? "synthetic-test-secret" : null
        }, hostedEnvironment: false);

        Assert.IsType<ManagedIdentityCredential>(sources[0]);
        Assert.Equal(1 + (secret ? 1 : 0) + (mode == "AzureCli" ? 1 : 0), sources.Count);
        if (secret)
            Assert.IsType<ClientSecretCredential>(sources[1]);
        if (mode == "AzureCli")
            Assert.IsType<AzureCliCredential>(sources[^1]);
    }

    [Theory]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER")]
    [InlineData("IDENTITY_ENDPOINT")]
    [InlineData("MSI_ENDPOINT")]
    [InlineData("CONTAINER_APP_NAME")]
    [InlineData("WEBSITE_INSTANCE_ID")]
    [InlineData("KUBERNETES_SERVICE_HOST")]
    public void HostedEnvironmentRejectsAzureCli(string name)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "test-host");
            Assert.Throws<ArgumentException>(() => CredentialFactory.Create(new ExporterOptions { AuthenticationMode = "AzureCli" }));
            Assert.IsType<ManagedIdentityCredential>(CredentialFactory.Create(new ExporterOptions()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
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