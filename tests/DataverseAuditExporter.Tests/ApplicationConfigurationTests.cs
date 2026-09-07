using System.Reflection;

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
            foreach (var property in typeof(ExporterOptions).GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.SetMethod?.IsPublic == true))
            {
                var name = "DATAVERSE_EXPORTER_" + property.Name;
                originals[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Set(string option, string value) => Environment.SetEnvironmentVariable("DATAVERSE_EXPORTER_" + option, value);

        public void Dispose()
        {
            foreach (var original in originals)
                Environment.SetEnvironmentVariable(original.Key, original.Value);
        }
    }
}