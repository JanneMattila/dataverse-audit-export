using Azure.Core;
using Azure.Data.Tables;
using Azure.Messaging.EventHubs.Producer;
using DataverseAuditExporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["--help"] or ["-h"])
{
    Console.WriteLine(ApplicationConfiguration.Help);
    return 0;
}
try
{
    var options = ApplicationConfiguration.Load(args);
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(console => { console.SingleLine = true; console.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<TokenCredential>(_ => CredentialFactory.Create(options));
    builder.Services.AddSingleton(provider => new TableClient(new Uri(options.StorageTableEndpoint), options.StateTableName,
        provider.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton<TableExportStateStore>();
    builder.Services.AddSingleton<IExportState>(provider => provider.GetRequiredService<TableExportStateStore>());
    builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) });
    builder.Services.AddSingleton<IAuditSource, DataverseAuditClient>();
    if (options.ExportPath is not null)
        builder.Services.AddSingleton<IAuditSink>(new FileAuditSink(options.ExportPath));
    if (options.EventHubNamespace is not null)
    {
        builder.Services.AddSingleton(provider => new EventHubProducerClient(options.EventHubNamespace, options.EventHubName,
            provider.GetRequiredService<TokenCredential>()));
        builder.Services.AddSingleton<IAuditSink, EventHubAuditSink>();
    }
    builder.Services.AddSingleton<AuditExportCycle>();
    builder.Services.AddHostedService<AuditExportWorker>();
    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}