using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DataverseAuditExporter.Tests;

[CollectionDefinition("Process state", DisableParallelization = true)]
public sealed class ProcessStateCollection;

[Collection("Process state")]
public sealed class ApplicationConfigurationTests
{
    private static readonly string[] Required =
    [
        "--OrganizationName", "config-test",
        "--StorageTableEndpoint", "https://config-test.table.core.windows.net",
        "--EventHubNamespace", "config-test.servicebus.windows.net",
        "--EventHubName", "audits"
    ];

    [Fact]
    public void OrganizationArrayInEnvironmentAndCliPreservesOrderAndPrecedence()
    {
        using var environment = new ConfigurationEnvironment();
        environment.Set("OrganizationName", "[\"contoso.crm4.dynamics.com\",\"fabrikam.crm4.dynamics.com\"]");
        var options = ApplicationConfiguration.Load(Required[2..]);
        Assert.Equal(new[] { "contoso.crm4.dynamics.com", "fabrikam.crm4.dynamics.com" },
            options.Organizations.Select(organization => organization.OrganizationUri.Host));
        Assert.Equal(2, options.Organizations.Select(organization => organization.PartitionKey).Distinct().Count());
        Assert.Single(ApplicationConfiguration.Load(Required).Organizations);
        var overridden = ApplicationConfiguration.Load([.. Required, "--OrganizationName", "[\"third\",\"fourth\"]"]);
        Assert.Equal(new[] { "third.crm.dynamics.com", "fourth.crm.dynamics.com" },
            overridden.Organizations.Select(organization => organization.OrganizationUri.Host));
    }

    [Fact]
    public void JsonSettingsSupportNativeOrganizationArrayAndScalarOverride()
    {
        const string json = """
            {"OrganizationName":["contoso","fabrikam"],"StorageTableEndpoint":"https://test.table.core.windows.net","ExportPath":"exports"}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var builder = new ConfigurationBuilder().AddJsonStream(stream);
        var configuration = builder.Build();
        var options = ApplicationConfiguration.Load(configuration);
        Assert.Equal(2, options.Organizations.Count);
        Assert.Equal(Path.Combine(Path.GetFullPath("exports"), "contoso.crm.dynamics.com"), options.Organizations[0].ExportPath);
        Assert.Equal(Path.Combine(Path.GetFullPath("exports"), "fabrikam.crm.dynamics.com"), options.Organizations[1].ExportPath);
        var single = ApplicationConfiguration.Load(new ConfigurationBuilder().AddConfiguration(configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OrganizationName"] = "single" }).Build());
        Assert.Single(single.Organizations);
        Assert.Equal(Path.Combine(Path.GetFullPath("exports"), "single.crm.dynamics.com"), single.Organizations[0].ExportPath);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[123]")]
    [InlineData("[\"contoso\",")]
    [InlineData("[\"contoso\",\"https://contoso.crm.dynamics.com/\"]")]
    [InlineData("[\"contoso\",\"http://invalid\"]")]
    public void InvalidOrganizationArraysAreRejected(string organizations)
    {
        using var environment = new ConfigurationEnvironment();
        Assert.Throws<ArgumentException>(() => ApplicationConfiguration.Load([.. Required, "--OrganizationName", organizations]));
    }

    [Theory]
    [InlineData("[\"config-test\"]")]
    [InlineData("[\"config-test\",\"second\"]")]
    [InlineData("[\"second\",\"config-test\"]")]
    public void OrganizationCountAndOrderDoNotChangeStateOrOutputPath(string organizations)
    {
        using var environment = new ConfigurationEnvironment();
        var original = ApplicationConfiguration.Load([.. Required, "--ExportPath", "exports"]).Organizations.Single();
        var options = ApplicationConfiguration.Load([.. Required, "--ExportPath", "exports", "--OrganizationName", organizations]);
        var matching = options.Organizations.Single(organization => organization.OrganizationUri == original.OrganizationUri);
        Assert.Equal(Path.Combine(Path.GetFullPath("exports"), "config-test.crm.dynamics.com"), original.ExportPath);
        Assert.Equal(original.ExportPath, matching.ExportPath);
        Assert.Equal(original.PartitionKey, matching.PartitionKey);
        Assert.Equal(original.DestinationFingerprint, matching.DestinationFingerprint);
        options.Validate();
        Assert.Equal(original.ExportPath, options.Organizations.Single(organization => organization.OrganizationUri == original.OrganizationUri).ExportPath);
    }

    [Theory]
    [InlineData("tenant", "client_id", "client_secret")]
    [InlineData("DATAVERSE_TENANT_ID", "DATAVERSE_CLIENT_ID", "DATAVERSE_CLIENT_SECRET")]
    [InlineData("DATAVERSE_EXPORTER_TenantId", "DATAVERSE_EXPORTER_ClientId", "DATAVERSE_EXPORTER_ClientSecret")]
    public void DockerEnvironmentSupportsClientCredentials(string tenant, string client, string secret)
    {
        using var environment = new ConfigurationEnvironment();
        environment.SetRaw(tenant, "22222222-2222-2222-2222-222222222222");
        environment.SetRaw(client, "11111111-1111-1111-1111-111111111111");
        environment.SetRaw(secret, "synthetic-test-secret");

        var options = ApplicationConfiguration.Load(Required);

        Assert.Equal("22222222-2222-2222-2222-222222222222", options.TenantId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", options.ClientId);
        Assert.Equal("synthetic-test-secret", options.ClientSecret);
        Assert.Equal("ManagedIdentity", options.AuthenticationMode);
    }

    [Fact]
    public void CredentialAliasesHaveLowerPrecedenceThanExporterVariablesAndCli()
    {
        using var environment = new ConfigurationEnvironment();
        environment.SetRaw("tenant", "short-alias");
        environment.SetRaw("DATAVERSE_TENANT_ID", "long-alias");
        Assert.Equal("long-alias", ApplicationConfiguration.Load(Required).TenantId);
        environment.Set("TenantId", "prefixed");
        Assert.Equal("prefixed", ApplicationConfiguration.Load(Required).TenantId);
        Assert.Equal("cli", ApplicationConfiguration.Load([.. Required, "--TenantId", "cli"]).TenantId);
    }

    [Theory]
    [InlineData(null, null, "synthetic-test-secret")]
    [InlineData("tenant", null, "synthetic-test-secret")]
    [InlineData("tenant", "client", null)]
    [InlineData(null, "client", "synthetic-test-secret")]
    public void IncompleteClientCredentialsFailWithoutEchoingSecret(string? tenant, string? client, string? secret)
    {
        using var environment = new ConfigurationEnvironment();
        environment.SetRaw("tenant", tenant);
        environment.SetRaw("client_id", client);
        environment.SetRaw("client_secret", secret);
        var exception = Assert.Throws<ArgumentException>(() => ApplicationConfiguration.Load(Required));
        Assert.Contains("requires TenantId, ClientId and ClientSecret together", exception.Message);
        Assert.DoesNotContain("synthetic-test-secret", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommandLineOverridesEnvironmentAndEnvironmentOverridesJson(bool equalsSyntax)
    {
        using var environment = new ConfigurationEnvironment();
        environment.Set("IntervalSeconds", "37");
        environment.Set("StateId", "environment-state");
        environment.Set("OrganizationName", "environment-org");
        environment.Set("EventHubName", "environment-hub");
        environment.Set("AuthenticationMode", "AzureCli");
        string[] interval = equalsSyntax ? ["--IntervalSeconds=19"] : ["--IntervalSeconds", "19"];

        var fromEnvironment = ApplicationConfiguration.Load(Required);
        var fromCommandLine = ApplicationConfiguration.Load([.. Required, .. interval]);

        Assert.Equal(37, fromEnvironment.IntervalSeconds);
        Assert.Equal(19, fromCommandLine.IntervalSeconds);
        Assert.Equal("environment-state", fromCommandLine.StateId);
        Assert.Equal("AzureCli", fromCommandLine.AuthenticationMode);
        Assert.Equal("https://config-test.crm.dynamics.com/", fromCommandLine.OrganizationUri.AbsoluteUri);
        Assert.Equal("audits", fromCommandLine.EventHubName);
    }

    [Fact]
    public void UnspecifiedSettingsUseDefaultsAndDoNotSelectFileOutput()
    {
        using var environment = new ConfigurationEnvironment();

        var options = ApplicationConfiguration.Load(Required);

        Assert.Equal(5, options.IntervalSeconds);
        Assert.Equal("default", options.StateId);
        Assert.Equal("DataverseAuditExporter", options.StateTableName);
        Assert.Equal("ManagedIdentity", options.AuthenticationMode);
        Assert.Null(options.ExportPath);
        Assert.Null(options.InitialCheckpoint);
    }

    [Fact]
    public void CommandLineNamesAreCaseInsensitiveAndLastDuplicateWins()
    {
        using var environment = new ConfigurationEnvironment();

        var options = ApplicationConfiguration.Load([.. Required, "--intervalseconds=11", "--INTERVALSECONDS", "12"]);

        Assert.Equal(12, options.IntervalSeconds);
    }

    [Theory]
    [InlineData("--Unknown")]
    [InlineData("--Once")]
    [InlineData("--StatePath")]
    [InlineData("--EventHubConnectionString")]
    [InlineData("--PartitionKey")]
    [InlineData("-IntervalSeconds")]
    [InlineData("IntervalSeconds")]
    public void UnknownOrReadOnlyOptionRejectedBeforeBinding(string argument)
    {
        using var environment = new ConfigurationEnvironment();

        var exception = Assert.Throws<ArgumentException>(() => ApplicationConfiguration.Load([argument, "unused"]));

        Assert.Contains("Unknown option", exception.Message);
        Assert.Contains(argument, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingValueAtEndOrBeforeNextOptionIsRejected(bool nextOption)
    {
        using var environment = new ConfigurationEnvironment();
        string[] args = nextOption ? ["--StateId", "--IntervalSeconds", "5"] : ["--StateId"];

        var exception = Assert.Throws<ArgumentException>(() => ApplicationConfiguration.Load(args));

        Assert.Contains("A value is required for --StateId", exception.Message);
    }

    [Fact]
    public void EmptyRequiredValueIsRejectedByValidation()
    {
        using var environment = new ConfigurationEnvironment();

        var exception = Assert.Throws<ArgumentException>(() => ApplicationConfiguration.Load([.. Required, "--OrganizationName="]));

        Assert.Contains("OrganizationName is required", exception.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task EntryPointSelectsHelpWithoutLoadingInvalidConfiguration(string help)
    {
        using var environment = new ConfigurationEnvironment();
        environment.Set("IntervalSeconds", "not-an-integer");

        var result = await InvokeEntryPoint([help]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ApplicationConfiguration.Help.Trim(), result.Output.Trim());
        Assert.Equal("", result.Error);
    }

    [Theory]
    [InlineData("--help", "--Unknown")]
    [InlineData("--Unknown", "--help")]
    [InlineData("-h", "--Unknown")]
    [InlineData("--StateId", "--help")]
    [InlineData("--help=true", "unused")]
    public async Task HelpCombinedWithOtherArgumentsDoesNotBypassValidation(string first, string second)
    {
        using var environment = new ConfigurationEnvironment();

        var result = await InvokeEntryPoint([first, second]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains(first == "--StateId" ? "A value is required" : "Unknown option", result.Error);
    }

    [Fact]
    public async Task EntryPointReturnsFailureForMissingValue()
    {
        using var environment = new ConfigurationEnvironment();

        var result = await InvokeEntryPoint(["--StateId"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("A value is required for --StateId", result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> InvokeEntryPoint(string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var entryPoint = typeof(ApplicationConfiguration).Assembly.EntryPoint!;
            var result = entryPoint.Invoke(null, [args]);
            var exitCode = result is Task<int> task ? await task : Assert.IsType<int>(result);
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class ConfigurationEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> originals = [];

        public ConfigurationEnvironment()
        {
            foreach (var name in new[] { "tenant", "client_id", "client_secret", "DATAVERSE_TENANT_ID", "DATAVERSE_CLIENT_ID", "DATAVERSE_CLIENT_SECRET" })
            {
                originals[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
            foreach (var property in typeof(ExporterOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.SetMethod?.IsPublic == true))
            {
                var name = "DATAVERSE_EXPORTER_" + property.Name;
                originals[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Set(string option, string value) => Environment.SetEnvironmentVariable("DATAVERSE_EXPORTER_" + option, value);

        public void SetRaw(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

        public void Dispose()
        {
            foreach (var original in originals)
                Environment.SetEnvironmentVariable(original.Key, original.Value);
        }
    }
}