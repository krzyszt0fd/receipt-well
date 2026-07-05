using Azure;
using Azure.Search.Documents;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public abstract record ReceiptRenameResult
{
    public sealed record Success() : ReceiptRenameResult;
    public sealed record NotFound() : ReceiptRenameResult;
    public sealed record Forbidden() : ReceiptRenameResult;
}

public partial class ReceiptRenameService(
    SearchClient searchClient,
    ILogger<ReceiptRenameService> logger)
{
    public async Task<ReceiptRenameResult> RenameAsync(string receiptId, string userId, string newFileName)
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
            return new ReceiptRenameResult.NotFound();
        }

        if (document.UserId != userId)
        {
            LogOwnershipViolation(logger, userId, receiptId);
            return new ReceiptRenameResult.Forbidden();
        }

        try
        {
            // Partial merge: send only Id + FileName so AI-written fields
            // (StoreName/PurchaseDate/Tags/TagsPl) are left untouched.
            await searchClient.MergeOrUploadDocumentsAsync(
                new[] { new ReceiptDocument { Id = receiptId, FileName = newFileName } });
        }
        catch (RequestFailedException ex)
        {
            LogSearchMergeFailed(logger, receiptId, ex);
            throw;
        }

        LogReceiptRenamed(logger, userId, receiptId);
        return new ReceiptRenameResult.Success();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Rename requested for receipt {ReceiptId} but it no longer exists")]
    private static partial void LogReceiptNotFound(ILogger logger, string receiptId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ownership violation: user {UserId} attempted to rename receipt {ReceiptId}")]
    private static partial void LogOwnershipViolation(ILogger logger, string userId, string receiptId);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure AI Search merge failed for receiptId {ReceiptId}")]
    private static partial void LogSearchMergeFailed(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Receipt renamed: user {UserId} receiptId {ReceiptId}")]
    private static partial void LogReceiptRenamed(ILogger logger, string userId, string receiptId);
}
