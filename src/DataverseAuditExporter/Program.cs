using Azure.Core;
using Azure.Data.Tables;
using Azure.Messaging.EventHubs.Producer;
using Azure.Storage.Blobs;
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
    builder.Logging.AddSimpleConsole(console => { console.SingleLine = true; console.IncludeScopes = true; console.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
    builder.Services.AddSingleton<TokenCredential>(_ => CredentialFactory.Create(options));
    builder.Services.AddSingleton(provider => new TableClient(new Uri(options.StorageTableEndpoint), options.StateTableName,
        provider.GetRequiredService<TokenCredential>()));
    builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) });
    if (options.EventHubNamespace is not null)
    {
        builder.Services.AddSingleton(provider => new EventHubProducerClient(options.EventHubNamespace, options.EventHubName,
            provider.GetRequiredService<TokenCredential>()));
    }
    if (options.BlobStorageEndpoint is not null)
    {
        builder.Services.AddSingleton(provider => new BlobServiceClient(new Uri(options.BlobStorageEndpoint),
            provider.GetRequiredService<TokenCredential>()).GetBlobContainerClient(options.BlobContainerName));
    }
    builder.Services.AddSingleton<IReadOnlyList<OrganizationExportJob>>(provider =>
        options.Organizations.Select(organization =>
        {
            var state = new TableExportStateStore(provider.GetRequiredService<TableClient>(), organization,
                provider.GetRequiredService<TimeProvider>());
            var source = new DataverseAuditClient(provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<TokenCredential>(), organization, provider.GetRequiredService<ILogger<DataverseAuditClient>>());
            var sinks = new List<IAuditSink>();
            if (organization.ExportPath is not null)
                sinks.Add(new FileAuditSink(organization.ExportPath));
            if (organization.BlobStorageEndpoint is not null)
                sinks.Add(new BlobAuditSink(provider.GetRequiredService<BlobContainerClient>(), organization));
            if (organization.EventHubNamespace is not null)
                sinks.Add(new EventHubAuditSink(provider.GetRequiredService<EventHubProducerClient>(), organization));
            var cycle = new AuditExportCycle(source, sinks, state, provider.GetRequiredService<ILogger<AuditExportCycle>>());
            return new OrganizationExportJob(organization, state, cycle);
        }).ToArray());
    builder.Services.AddHostedService(provider => new AuditExportWorker(
        provider.GetRequiredService<IReadOnlyList<OrganizationExportJob>>(), options,
        provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<AuditExportWorker>>()));
    using var host = builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}