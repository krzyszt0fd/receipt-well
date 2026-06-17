using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using ReceiptWell.Functions;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var configuration = builder.Configuration;

var openTelemetryBuilder = builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults();
if (!string.IsNullOrEmpty(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    openTelemetryBuilder.UseAzureMonitorExporter();
}

var storageConnectionString = configuration["AzureStorage:ConnectionString"];
builder.Services.AddSingleton(_ => string.IsNullOrEmpty(storageConnectionString)
    ? new BlobServiceClient(
        new Uri(configuration["AzureStorage:BlobServiceUri"]!), new DefaultAzureCredential())
    : new BlobServiceClient(storageConnectionString));

var searchUri = new Uri(configuration["AzureSearch:ServiceUri"]!);
var searchCredential = new AzureKeyCredential(configuration["AzureSearch:ApiKey"]!);
builder.Services.AddSingleton(_ => new SearchClient(
    searchUri, configuration["AzureSearch:IndexName"]!, searchCredential));

var openAiEndpoint = configuration["AzureOpenAI:Endpoint"]!;
var openAiDeploymentName = configuration["AzureOpenAI:DeploymentName"]!;
var openAiApiKey = configuration["AzureOpenAI:ApiKey"];
builder.Services.AddSingleton(_ =>
{
    AzureOpenAIClient client = string.IsNullOrEmpty(openAiApiKey)
        ? new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential())
        : new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiApiKey));
    return client.GetChatClient(openAiDeploymentName).AsIChatClient();
});

builder.Services.AddSingleton<ReceiptExtractionService>();
builder.Services.AddSingleton<ReceiptStore>();
builder.Services.AddSingleton<TagNormalizer>();

builder.Build().Run();
