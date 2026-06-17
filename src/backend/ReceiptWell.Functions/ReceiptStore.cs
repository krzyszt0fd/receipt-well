using Azure;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using ReceiptWell.Models;

namespace ReceiptWell.Functions;

public partial class ReceiptStore(SearchClient searchClient, ILogger<ReceiptStore> logger)
{
    public async Task<ReceiptDocument?> GetByIdAsync(string receiptId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await searchClient.GetDocumentAsync<ReceiptDocument>(
                receiptId, cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            LogReceiptNotFound(logger, receiptId, ex);
            return null;
        }
    }

    public Task SetReadyAsync(
        string receiptId, string? storeName, DateOnly? purchaseDate, IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        var document = new ReceiptEnrichmentDocument
        {
            Id = receiptId,
            StoreName = storeName,
            // The frozen index schema stores PurchaseDate as DateTimeOffset; a receipt date has no
            // time component, so midnight UTC is the single conversion point from the domain DateOnly.
            PurchaseDate = purchaseDate is { } date
                ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null,
            Tags = tags.ToList(),
            Status = ReceiptStatus.Ready
        };
        return searchClient.MergeOrUploadDocumentsAsync(new[] { document }, cancellationToken: cancellationToken);
    }

    public Task SetErrorAsync(string receiptId, CancellationToken cancellationToken)
    {
        var document = new ReceiptStatusDocument { Id = receiptId, Status = ReceiptStatus.Error };
        return searchClient.MergeOrUploadDocumentsAsync(new[] { document }, cancellationToken: cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receipt not found in index: {ReceiptId}")]
    private static partial void LogReceiptNotFound(ILogger logger, string receiptId, Exception ex);
}

// Partial merge documents: only the fields below are sent to MergeOrUploadDocumentsAsync, so
// fields omitted here (UserId, BlobUrl, FileName, FileSize, UploadedAt) are left untouched in the index.
internal sealed class ReceiptEnrichmentDocument
{
    public string Id { get; set; } = string.Empty;
    public string? StoreName { get; set; }
    public DateTimeOffset? PurchaseDate { get; set; }
    public IList<string> Tags { get; set; } = [];
    public string Status { get; set; } = string.Empty;
}

internal sealed class ReceiptStatusDocument
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
