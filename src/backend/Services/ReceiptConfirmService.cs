using Azure;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
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
    DelegationTokenProvider delegationTokenProvider,
    ILogger<ReceiptConfirmService> logger)
{
    private static readonly HashSet<string> AllowedContentTypes =
        new(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/webp", "image/gif" };
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

        var headerBytes = await DownloadFirstBytesAsync(stagingBlob, 16);
        if (!MatchesMagicBytes(stagingProperties.ContentType, headerBytes))
        {
            LogValidationFailure(logger, stagingBlobName, $"Magic bytes do not match declared ContentType '{stagingProperties.ContentType}'");
            return new ReceiptConfirmResult.InvalidBlob("File content does not match the declared content type.");
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

        var stagingReadSasUri = await GenerateReadSasAsync(stagingBlob);
        await targetBlob.SyncCopyFromUriAsync(stagingReadSasUri);

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

    private static async Task<byte[]> DownloadFirstBytesAsync(BlobClient blob, int count)
    {
        var response = await blob.DownloadContentAsync(new BlobDownloadOptions { Range = new HttpRange(0, count) });
        return response.Value.Content.ToArray();
    }

    private static bool MatchesMagicBytes(string contentType, byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        return contentType.ToLowerInvariant() switch
        {
            "image/png"  => bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
            "image/jpeg" => bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            "image/webp" => bytes.Length >= 12
                && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50,
            "image/gif"  => bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38,
            _ => false
        };
    }

    private async Task<Uri> GenerateReadSasAsync(BlobClient blob)
    {
        var sasExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            ExpiresOn = sasExpiry
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        if (blob.CanGenerateSasUri)
            return blob.GenerateSasUri(sasBuilder);

        var key = await delegationTokenProvider.GetOrFetchAsync();
        var queryParams = sasBuilder.ToSasQueryParameters(key, blobServiceClient.AccountName);
        return new BlobUriBuilder(blob.Uri) { Sas = queryParams }.ToUri();
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
