using Azure;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using ReceiptWell.Models;

namespace ReceiptWell.Services;

public abstract record ReceiptDownloadResult
{
    public sealed record Success(Uri DownloadUri) : ReceiptDownloadResult;
    public sealed record NotFound() : ReceiptDownloadResult;
    public sealed record Forbidden() : ReceiptDownloadResult;
}

public partial class ReceiptDownloadService(
    SearchClient searchClient,
    BlobServiceClient blobServiceClient,
    IConfiguration configuration,
    DelegationTokenProvider delegationTokenProvider,
    ILogger<ReceiptDownloadService> logger)
{
    private readonly string _receiptsContainerName =
        configuration["AzureStorage:ReceiptsContainerName"]!;

    public async Task<ReceiptDownloadResult> GetDownloadUrlAsync(string receiptId, string userId)
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
            return new ReceiptDownloadResult.NotFound();
        }

        if (document.UserId != userId)
        {
            LogOwnershipViolation(logger, userId, receiptId);
            return new ReceiptDownloadResult.Forbidden();
        }

        var blobClient = blobServiceClient
            .GetBlobContainerClient(_receiptsContainerName)
            .GetBlobClient($"{userId}/{receiptId}");

        var downloadUri = await GenerateReadSasAsync(blobClient, document.FileName);

        LogDownloadUrlIssued(logger, userId, receiptId);
        return new ReceiptDownloadResult.Success(downloadUri);
    }

    private async Task<Uri> GenerateReadSasAsync(BlobClient blob, string fileName)
    {
        var sasExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var contentDisposition = $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName = blob.Name,
            Resource = "b",
            ExpiresOn = sasExpiry,
            ContentDisposition = contentDisposition
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        if (blob.CanGenerateSasUri)
            return blob.GenerateSasUri(sasBuilder);

        var key = await delegationTokenProvider.GetOrFetchAsync();
        var queryParams = sasBuilder.ToSasQueryParameters(key, blobServiceClient.AccountName);
        return new BlobUriBuilder(blob.Uri) { Sas = queryParams }.ToUri();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Download requested for receipt {ReceiptId} but it no longer exists")]
    private static partial void LogReceiptNotFound(ILogger logger, string receiptId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ownership violation: user {UserId} attempted to download receipt {ReceiptId}")]
    private static partial void LogOwnershipViolation(ILogger logger, string userId, string receiptId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Download URL issued: user {UserId} receiptId {ReceiptId}")]
    private static partial void LogDownloadUrlIssued(ILogger logger, string userId, string receiptId);
}
