using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReceiptWell.Models;

namespace ReceiptWell.Tests.Infrastructure;

public enum ConfirmStep { BlobCopy, SearchWrite, QueueSend, StagingDelete, Complete }

/// <summary>
/// Encapsulates the chained-substitute setup that drives a <c>POST /receipts/confirm</c>
/// request through the non-atomic 4-step sequence. Create once per test method; the
/// constructor clears accumulated call history on the shared factory substitutes and
/// re-stubs every step so tests declare only <em>which step throws</em> via
/// <see cref="ConfirmStep"/>.
/// </summary>
public sealed class ConfirmFlowHarness
{
    /// <summary>The staging <see cref="BlobClient"/> substitute — assert <c>DeleteAsync</c> on this.</summary>
    public BlobClient StagingBlobClient { get; }

    /// <summary>The receipts (target) <see cref="BlobClient"/> substitute — assert <c>SyncCopyFromUriAsync</c> on this.</summary>
    public BlobClient TargetBlobClient { get; }

    /// <summary>Staging blob name to send in the request body.</summary>
    public string StagingBlobName { get; }

    public ConfirmFlowHarness(ReceiptWellWebFactory factory, string oid, ConfirmStep failAt = ConfirmStep.Complete)
    {
        // Clear accumulated call history so Received/DidNotReceive assertions start fresh.
        factory.BlobServiceClient.ClearReceivedCalls();
        factory.SearchClient.ClearReceivedCalls();
        factory.QueueClient.ClearReceivedCalls();

        // Container names are coupled to the service's IConfiguration reads — read them
        // from the running host so the harness never hard-codes them.
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var stagingContainerName = config["AzureStorage:StagingContainerName"]!;
        var receiptsContainerName = config["AzureStorage:ReceiptsContainerName"]!;

        // Fresh container substitutes wired to the shared BlobServiceClient substitute.
        var stagingContainer = Substitute.For<BlobContainerClient>();
        var receiptsContainer = Substitute.For<BlobContainerClient>();
        factory.BlobServiceClient.GetBlobContainerClient(stagingContainerName).Returns(stagingContainer);
        factory.BlobServiceClient.GetBlobContainerClient(receiptsContainerName).Returns(receiptsContainer);

        // stagingBlobName = "{oid}/{guid}"; receiptId = guid; targetBlobName = "{oid}/{guid}" — same path.
        StagingBlobName = $"{oid}/{Guid.NewGuid()}";

        var stagingBlob = Substitute.For<BlobClient>();
        var targetBlob = Substitute.For<BlobClient>();
        StagingBlobClient = stagingBlob;
        TargetBlobClient = targetBlob;

        stagingContainer.GetBlobClient(StagingBlobName).Returns(stagingBlob);
        receiptsContainer.GetBlobClient(Arg.Any<string>()).Returns(targetBlob);

        // SAS bypass — avoids DelegationTokenProvider / GetUserDelegationKeyAsync entirely.
        stagingBlob.CanGenerateSasUri.Returns(true);
        stagingBlob.GenerateSasUri(Arg.Any<BlobSasBuilder>())
            .Returns(new Uri("https://staging.localhost.test/blob?sig=test"));

        // Target blob URI is used as BlobUrl in the receipt search document.
        targetBlob.Uri.Returns(
            new Uri($"https://receipts.localhost.test/{receiptsContainerName}/{StagingBlobName}"));

        // Pre-Step 1a: properties — content type, size, and disposition all pass validation.
        stagingBlob.GetPropertiesAsync(
                Arg.Any<BlobRequestConditions>(),
                Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                BlobsModelFactory.BlobProperties(
                    contentType: "image/png",
                    contentLength: 1024,
                    contentDisposition: "attachment; filename=\"r.png\""),
                Substitute.For<Response>()));

        // Pre-Step 1b: download — first 4 bytes match PNG magic (89 50 4E 47).
        stagingBlob.DownloadContentAsync(
                Arg.Any<BlobDownloadOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(
                    content: new BinaryData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })),
                Substitute.For<Response>()));

        // Step 1: blob copy (SyncCopyFromUriAsync on the receipts-container blob).
        if (failAt == ConfirmStep.BlobCopy)
            targetBlob.SyncCopyFromUriAsync(
                    Arg.Any<Uri>(),
                    Arg.Any<BlobCopyFromUriOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Response<BlobCopyInfo>>(
                    new RequestFailedException(500, "Simulated blob copy failure")));
        else
            targetBlob.SyncCopyFromUriAsync(
                    Arg.Any<Uri>(),
                    Arg.Any<BlobCopyFromUriOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Response.FromValue(
                    BlobsModelFactory.BlobCopyInfo(
                        eTag: default,
                        lastModified: DateTimeOffset.UtcNow,
                        copyId: "test-copy-id",
                        copyStatus: CopyStatus.Success),
                    Substitute.For<Response>()));

        // Step 2: search write (MergeOrUploadDocumentsAsync).
        if (failAt == ConfirmStep.SearchWrite)
            factory.SearchClient.MergeOrUploadDocumentsAsync(
                    Arg.Any<IEnumerable<ReceiptDocument>>(),
                    Arg.Any<IndexDocumentsOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Response<IndexDocumentsResult>>(
                    new RequestFailedException(500, "Simulated search write failure")));
        else
            factory.SearchClient.MergeOrUploadDocumentsAsync(
                    Arg.Any<IEnumerable<ReceiptDocument>>(),
                    Arg.Any<IndexDocumentsOptions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Response.FromValue(
                    SearchModelFactory.IndexDocumentsResult(
                        results: Array.Empty<IndexingResult>()),
                    Substitute.For<Response>()));

        // Step 3: queue send (SendMessageAsync — service calls the 1-param overload).
        if (failAt == ConfirmStep.QueueSend)
            factory.QueueClient.SendMessageAsync(Arg.Any<string>())
                .Returns(Task.FromException<Response<SendReceipt>>(
                    new RequestFailedException(500, "Simulated queue send failure")));
        else
            factory.QueueClient.SendMessageAsync(Arg.Any<string>())
                .Returns(Response.FromValue(
                    QueuesModelFactory.SendReceipt(
                        messageId: "test-msg-id",
                        insertionTime: DateTimeOffset.UtcNow,
                        expirationTime: DateTimeOffset.UtcNow.AddDays(7),
                        popReceipt: "test-pop-receipt",
                        timeNextVisible: DateTimeOffset.UtcNow.AddSeconds(30)),
                    Substitute.For<Response>()));

        // Step 4: staging cleanup (DeleteAsync — fire-and-forget; failure is swallowed).
        if (failAt == ConfirmStep.StagingDelete)
            stagingBlob.DeleteAsync(
                    Arg.Any<DeleteSnapshotsOption>(),
                    Arg.Any<BlobRequestConditions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Response>(
                    new RequestFailedException(500, "Simulated staging delete failure")));
        else
            stagingBlob.DeleteAsync(
                    Arg.Any<DeleteSnapshotsOption>(),
                    Arg.Any<BlobRequestConditions>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(Substitute.For<Response>()));
    }
}
