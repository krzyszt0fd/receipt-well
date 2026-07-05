using Azure;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public abstract record ReceiptDeleteResult
{
    public sealed record Success() : ReceiptDeleteResult;
    public sealed record NotFound() : ReceiptDeleteResult;
    public sealed record Forbidden() : ReceiptDeleteResult;
}

public partial class ReceiptDeleteService(
    SearchClient searchClient,
    BlobServiceClient blobServiceClient,
    IConfiguration configuration,
    ILogger<ReceiptDeleteService> logger)
{
    private readonly string _receiptsContainerName =
        configuration["AzureStorage:ReceiptsContainerName"]!;

    public async Task<ReceiptDeleteResult> DeleteAsync(string receiptId, string userId)
    {
        ReceiptDocument document;
        try
        {
            var response = await searchClient.GetDocumentAsync<ReceiptDocument>(receiptId);
            document = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            LogReceiptNotFound(logger, receiptId);
            return new ReceiptDeleteResult.NotFound();
        }

        if (document.UserId != userId)
        {
            LogOwnershipViolation(logger, userId, receiptId);
            return new ReceiptDeleteResult.Forbidden();
        }

        try
        {
            await searchClient.DeleteDocumentsAsync(new[] { new ReceiptDocument { Id = receiptId } });
        }
        catch (RequestFailedException ex)
        {
            LogSearchDeleteFailed(logger, receiptId, ex);
            throw;
        }

        try
        {
            var blobClient = blobServiceClient
                .GetBlobContainerClient(_receiptsContainerName)
                .GetBlobClient($"{userId}/{receiptId}");
            await blobClient.DeleteIfExistsAsync();
        }
        catch (Exception ex)
        {
            LogBlobDeleteFailed(logger, receiptId, ex);
            throw;
        }

        LogReceiptDeleted(logger, userId, receiptId);
        return new ReceiptDeleteResult.Success();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Delete requested for receipt {ReceiptId} but it no longer exists")]
    private static partial void LogReceiptNotFound(ILogger logger, string receiptId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ownership violation: user {UserId} attempted to delete receipt {ReceiptId}")]
    private static partial void LogOwnershipViolation(ILogger logger, string userId, string receiptId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure AI Search delete failed for receiptId {ReceiptId}")]
    private static partial void LogSearchDeleteFailed(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Blob delete failed for receiptId {ReceiptId} after Search document was already deleted (orphaned blob, needs manual reconciliation)")]
    private static partial void LogBlobDeleteFailed(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Receipt deleted: user {UserId} receiptId {ReceiptId}")]
    private static partial void LogReceiptDeleted(ILogger logger, string userId, string receiptId);
}
