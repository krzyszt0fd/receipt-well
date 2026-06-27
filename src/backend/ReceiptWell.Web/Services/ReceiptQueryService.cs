using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public partial class ReceiptQueryService(SearchClient searchClient, ILogger<ReceiptQueryService> logger)
{
    public async Task<IReadOnlyList<ReceiptSummary>> GetReceiptsAsync(
        string userId, string? query, CancellationToken cancellationToken)
    {
        var term = query?.Trim();
        var isSearch = !string.IsNullOrEmpty(term);

        var options = new SearchOptions
        {
            Filter = $"UserId eq '{userId}'",
            Size = 1000
        };

        if (isSearch)
        {
            options.SearchFields.Add("TagsPl");
        }
        else
        {
            options.OrderBy.Add("UploadedAt desc");
        }

        var searchText = isSearch ? term : "*";
        var response = await searchClient.SearchAsync<ReceiptDocument>(searchText, options, cancellationToken);

        var summaries = new List<ReceiptSummary>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var document = result.Document;
            summaries.Add(new ReceiptSummary(
                document.Id,
                document.FileName,
                document.FileSize,
                document.Status,
                document.UploadedAt,
                document.StoreName,
                document.PurchaseDate,
                document.Tags.AsReadOnly()));
        }

        LogReceiptsListed(logger, userId, summaries.Count);
        return summaries;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Listed {Count} receipt(s) for user {UserId}")]
    private static partial void LogReceiptsListed(ILogger logger, string userId, int count);
}
