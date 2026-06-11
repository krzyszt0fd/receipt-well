using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public partial class SearchIndexInitializer(
    SearchIndexClient indexClient,
    IConfiguration configuration,
    ILogger<SearchIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var indexName = configuration["AzureSearch:IndexName"]!;
        try
        {
            var fields = new FieldBuilder().Build(typeof(ReceiptDocument));
            var index = new SearchIndex(indexName, fields);
            await indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
            LogIndexEnsured(logger, indexName);
        }
        catch (Exception ex)
        {
            LogIndexFailed(logger, indexName, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Azure AI Search index '{IndexName}' ensured.")]
    private static partial void LogIndexEnsured(ILogger logger, string indexName);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Failed to initialize Azure AI Search index '{IndexName}'.")]
    private static partial void LogIndexFailed(ILogger logger, string indexName, Exception ex);
}
