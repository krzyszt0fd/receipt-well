using Azure;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public abstract record ReceiptConfirmResult
{
    public sealed record Success(string ReceiptId, string FileName, long FileSize) : ReceiptConfirmResult;
    public sealed record Forbidden() : ReceiptConfirmResult;
    public sealed record InvalidBlob(string Message) : ReceiptConfirmResult;
}

public partial class ReceiptConfirmService(
    BlobServiceClient blobServiceClient,
    SearchClient searchClient,
    IConfiguration configuration,
    ILogger<ReceiptConfirmService> logger)
{
    private static readonly HashSet<string> AllowedContentTypes =
        ["image/png", "image/jpeg", "image/webp", "image/gif"];
    private const long MaxFileSize = 20_000_000;

    private readonly string _stagingContainerName =
        configuration["AzureStorage:StagingContainerName"]!;
    private readonly string _receiptsContainerName =
        configuration["AzureStorage:ReceiptsContainerName"]!;

    public async Task<ReceiptConfirmResult> ConfirmUploadAsync(
        string stagingBlobName, string userId, string originalFileName)
    {
        if (!stagingBlobName.StartsWith($"{userId}/", StringComparison.Ordinal))
        {
            LogOwnershipViolation(logger, userId, stagingBlobName);
            return new ReceiptConfirmResult.Forbidden();
        }

        var stagingContainer = blobServiceClient.GetBlobContainerClient(_stagingContainerName);
        var stagingBlob = stagingContainer.GetBlobClient(stagingBlobName);

        BlobProperties stagingProperties;
        try
        {
            var propertiesResponse = await stagingBlob.GetPropertiesAsync();
            stagingProperties = propertiesResponse.Value;
        }
        catch (RequestFailedException ex)
        {
            LogBlobNotFound(logger, stagingBlobName, ex);
            return new ReceiptConfirmResult.InvalidBlob("Staging blob not found.");
        }

        if (!AllowedContentTypes.Contains(stagingProperties.ContentType))
        {
            LogValidationFailure(logger, stagingBlobName, $"ContentType '{stagingProperties.ContentType}' not allowed");
            return new ReceiptConfirmResult.InvalidBlob($"Content type '{stagingProperties.ContentType}' is not allowed.");
        }

        if (stagingProperties.ContentLength > MaxFileSize)
        {
            LogValidationFailure(logger, stagingBlobName, $"ContentLength {stagingProperties.ContentLength} exceeds limit");
            return new ReceiptConfirmResult.InvalidBlob("File exceeds the 20 MB limit.");
        }

        if (string.IsNullOrEmpty(stagingProperties.ContentDisposition))
        {
            LogValidationFailure(logger, stagingBlobName, "ContentDisposition is empty");
            return new ReceiptConfirmResult.InvalidBlob("Content-Disposition header is missing.");
        }

        var receiptId = stagingBlobName.Split('/').Last();
        var targetBlobName = $"{userId}/{receiptId}";
        var receiptsContainer = blobServiceClient.GetBlobContainerClient(_receiptsContainerName);
        var targetBlob = receiptsContainer.GetBlobClient(targetBlobName);

        var downloadResponse = await stagingBlob.DownloadContentAsync();
        await targetBlob.UploadAsync(
            downloadResponse.Value.Content.ToStream(),
            new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = stagingProperties.ContentType,
                    ContentDisposition = stagingProperties.ContentDisposition
                }
            });

        var targetBlobUrl = targetBlob.Uri.ToString();
        var receiptDocument = new ReceiptDocument
        {
            Id = receiptId,
            UserId = userId,
            BlobUrl = targetBlobUrl,
            FileName = originalFileName,
            FileSize = stagingProperties.ContentLength,
            Status = "pending",
            UploadedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await searchClient.MergeOrUploadDocumentsAsync(new[] { receiptDocument });
        }
        catch (RequestFailedException ex)
        {
            LogSearchWriteFailed(logger, receiptId, targetBlobUrl, ex);
            throw;
        }

        try
        {
            await stagingBlob.DeleteAsync();
        }
        catch (Exception ex)
        {
            LogStagingDeleteFailed(logger, stagingBlobName, ex);
        }

        LogReceiptConfirmed(logger, userId, receiptId);
        return new ReceiptConfirmResult.Success(receiptId, originalFileName, stagingProperties.ContentLength);
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ownership violation: user {UserId} attempted to confirm blob {StagingBlobName}")]
    private static partial void LogOwnershipViolation(ILogger logger, string userId, string stagingBlobName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Staging blob not found: {StagingBlobName}")]
    private static partial void LogBlobNotFound(ILogger logger, string stagingBlobName, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Blob validation failed for {StagingBlobName}: {Reason}")]
    private static partial void LogValidationFailure(ILogger logger, string stagingBlobName, string reason);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Azure AI Search write failed for receiptId {ReceiptId} blobUrl {BlobUrl}")]
    private static partial void LogSearchWriteFailed(ILogger logger, string receiptId, string blobUrl, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Failed to delete staging blob {StagingBlobName} after successful confirm")]
    private static partial void LogStagingDeleteFailed(ILogger logger, string stagingBlobName, Exception ex);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Receipt confirmed: user {UserId} receiptId {ReceiptId}")]
    private static partial void LogReceiptConfirmed(ILogger logger, string userId, string receiptId);
}
