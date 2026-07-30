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
        LogRenamePayload(logger, newFileName);

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
            // Partial merge via a dedicated DTO: Azure AI Search merge overwrites *every*
            // field present in the serialized payload, so a full ReceiptDocument (with its
            // defaulted UserId/Status/FileSize/Tags) would clobber those. Sending only the
            // fields below leaves everything else — including UserId — untouched.
            await searchClient.MergeOrUploadDocumentsAsync(
                new[] { new ReceiptRenameDocument { Id = receiptId, FileName = newFileName } });
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Rename request body: {NewFileName}")]
    private static partial void LogRenamePayload(ILogger logger, string newFileName);
}

// Partial merge document: only the fields below are sent to MergeOrUploadDocumentsAsync, so
// fields omitted here (UserId, BlobUrl, FileSize, Status, UploadedAt, Tags, TagsPl) are left
// untouched in the index. Mirrors ReceiptWell.Functions' ReceiptEnrichmentDocument pattern.
public sealed class ReceiptRenameDocument
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}
