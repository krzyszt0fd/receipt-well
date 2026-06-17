using System.ClientModel;
using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReceiptWell.Models;

namespace ReceiptWell.Functions;

public partial class ExtractReceiptFunction(
    ReceiptStore receiptStore,
    BlobServiceClient blobServiceClient,
    ReceiptExtractionService extractionService,
    TagNormalizer tagNormalizer,
    IConfiguration configuration,
    ILogger<ExtractReceiptFunction> logger)
{
    private readonly string _receiptsContainerName = configuration["AzureStorage:ReceiptsContainerName"]!;

    [Function(nameof(ExtractReceiptFunction))]
    public async Task Run(
        [QueueTrigger("%ExtractionQueueName%", Connection = "AzureWebJobsStorage")] string receiptId,
        CancellationToken cancellationToken)
    {
        var receipt = await receiptStore.GetByIdAsync(receiptId, cancellationToken);
        if (receipt is null)
        {
            LogReceiptMissing(logger, receiptId);
            return;
        }

        if (receipt.Status == ReceiptStatus.Ready)
        {
            LogAlreadyReady(logger, receiptId);
            return;
        }

        var blobClient = blobServiceClient
            .GetBlobContainerClient(_receiptsContainerName)
            .GetBlobClient($"{receipt.UserId}/{receipt.Id}");

        byte[] imageBytes;
        string contentType;
        try
        {
            var download = await blobClient.DownloadContentAsync(cancellationToken);
            imageBytes = download.Value.Content.ToArray();
            contentType = download.Value.Details.ContentType;
        }
        catch (RequestFailedException ex) when (IsTransient(ex))
        {
            LogTransientBlobDownloadFailure(logger, receiptId, ex);
            throw; // let the queue retry up to maxDequeueCount
        }
        catch (RequestFailedException ex)
        {
            // The receipt blob is missing or unreadable — terminal, not retriable.
            LogBlobDownloadFailed(logger, receiptId, ex);
            await receiptStore.SetErrorAsync(receiptId, cancellationToken);
            return;
        }

        ReceiptExtractionResult extraction;
        try
        {
            extraction = await extractionService.ExtractAsync(imageBytes, contentType, cancellationToken);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            LogTransientExtractionFailure(logger, receiptId, ex);
            throw; // let the queue retry up to maxDequeueCount
        }
        catch (Exception ex)
        {
            // Content-filter block, unreadable image, or malformed model output — terminal.
            LogTerminalExtractionFailure(logger, receiptId, ex);
            await receiptStore.SetErrorAsync(receiptId, cancellationToken);
            return;
        }

        var tags = tagNormalizer.Normalize(extraction.Tags);
        await receiptStore.SetReadyAsync(receiptId, extraction.StoreName, extraction.PurchaseDate, tags, cancellationToken);
        LogExtractionSucceeded(logger, receiptId);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        ClientResultException cre => cre.Status >= 500 || cre.Status == (int)HttpStatusCode.TooManyRequests,
        RequestFailedException rfe => rfe.Status >= 500 || rfe.Status == (int)HttpStatusCode.TooManyRequests,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException hre => hre.StatusCode >= HttpStatusCode.InternalServerError || hre.StatusCode == HttpStatusCode.TooManyRequests,
        _ => false
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Receipt {ReceiptId} not found in index; dropping message")]
    private static partial void LogReceiptMissing(ILogger logger, string receiptId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Receipt {ReceiptId} already ready; skipping duplicate extraction")]
    private static partial void LogAlreadyReady(ILogger logger, string receiptId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to download receipt blob for {ReceiptId}")]
    private static partial void LogBlobDownloadFailed(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient blob download failure for {ReceiptId}; retrying")]
    private static partial void LogTransientBlobDownloadFailure(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient extraction failure for {ReceiptId}; retrying")]
    private static partial void LogTransientExtractionFailure(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Terminal extraction failure for {ReceiptId}")]
    private static partial void LogTerminalExtractionFailure(ILogger logger, string receiptId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Extraction succeeded for {ReceiptId}")]
    private static partial void LogExtractionSucceeded(ILogger logger, string receiptId);
}
