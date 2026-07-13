using System.Text.RegularExpressions;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public partial class ReceiptQueryService(SearchClient searchClient, ILogger<ReceiptQueryService> logger)
{
    // Lucene special characters that must be escaped before a user term enters a Full query string.
    [GeneratedRegex("[+\\-&|!(){}\\[\\]^\"~*?:\\\\/]")]
    private static partial Regex LuceneSpecialCharacters();

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

        string searchText;
        if (isSearch)
        {
            // Combine both matching styles in one relevance-ranked query: TagsPl is analyzed
            // (pl.microsoft) so it lemmatizes whole words ("rowery" -> "rower"), while a raw
            // wildcard on Tags bypasses the analyzer for as-you-type prefix matching. The ^3
            // boost ranks lemma/exact hits above noisy prefix hits.
            var escaped = LuceneSpecialCharacters().Replace(term!, "\\$0");
            searchText = $"TagsPl:{escaped}^3 OR Tags:{escaped}*";
            options.QueryType = SearchQueryType.Full;
        }
        else
        {
            options.OrderBy.Add("UploadedAt desc");
            searchText = "*";
        }

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
