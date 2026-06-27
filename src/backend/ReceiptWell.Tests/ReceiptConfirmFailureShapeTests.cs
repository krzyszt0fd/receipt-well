using System.Net;
using System.Net.Http.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #7 — infra-boundary failure shape for the non-atomic confirm sequence.
/// Each test drives the flow to a specific step, makes that step throw, and asserts:
/// (1) the HTTP failure shape (honest-5xx or intentional 200), and (2) the exact
/// side-effect commit boundary — which steps ran to completion and which did not.
/// </summary>
public class ReceiptConfirmFailureShapeTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task BlobCopy_fails_returns_500_with_nothing_committed()
    {
        const string oid = "failure-shape-blob-copy-user";
        var harness = new ConfirmFlowHarness(factory, oid, ConfirmStep.BlobCopy);

        var response = await PostConfirmAsync(oid, harness.StagingBlobName);

        await ProblemDetailsAssertions.AssertHonest500Async(response);

        await harness.TargetBlobClient.Received(1)
            .SyncCopyFromUriAsync(
                Arg.Any<Uri>(),
                Arg.Any<BlobCopyFromUriOptions>(),
                Arg.Any<CancellationToken>());
        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptDocument>(default!);
        await factory.QueueClient.DidNotReceiveWithAnyArgs()
            .SendMessageAsync(default(string)!);
    }

    [Fact]
    public async Task SearchWrite_fails_returns_500_with_blob_copied_only()
    {
        const string oid = "failure-shape-search-write-user";
        var harness = new ConfirmFlowHarness(factory, oid, ConfirmStep.SearchWrite);

        var response = await PostConfirmAsync(oid, harness.StagingBlobName);

        await ProblemDetailsAssertions.AssertHonest500Async(response);

        await harness.TargetBlobClient.Received(1)
            .SyncCopyFromUriAsync(
                Arg.Any<Uri>(),
                Arg.Any<BlobCopyFromUriOptions>(),
                Arg.Any<CancellationToken>());
        await factory.SearchClient.Received(1)
            .MergeOrUploadDocumentsAsync(
                Arg.Any<IEnumerable<ReceiptDocument>>(),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>());
        await factory.QueueClient.DidNotReceiveWithAnyArgs()
            .SendMessageAsync(default(string)!);
    }

    [Fact]
    public async Task QueueSend_fails_returns_500_with_blob_and_search_committed()
    {
        const string oid = "failure-shape-queue-send-user";
        var harness = new ConfirmFlowHarness(factory, oid, ConfirmStep.QueueSend);

        var response = await PostConfirmAsync(oid, harness.StagingBlobName);

        await ProblemDetailsAssertions.AssertHonest500Async(response);

        await harness.TargetBlobClient.Received(1)
            .SyncCopyFromUriAsync(
                Arg.Any<Uri>(),
                Arg.Any<BlobCopyFromUriOptions>(),
                Arg.Any<CancellationToken>());
        await factory.SearchClient.Received(1)
            .MergeOrUploadDocumentsAsync(
                Arg.Any<IEnumerable<ReceiptDocument>>(),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>());
        await factory.QueueClient.Received(1)
            .SendMessageAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Intentional fire-and-forget contract: staging cleanup throws but the exception
    /// is swallowed — the response is 200 even though DeleteAsync failed. This test
    /// pins that contract; if the catch block is changed to re-throw it goes red.
    /// </summary>
    [Fact]
    public async Task StagingDelete_fails_still_returns_200_fire_and_forget()
    {
        const string oid = "failure-shape-staging-delete-user";
        var harness = new ConfirmFlowHarness(factory, oid, ConfirmStep.StagingDelete);

        var response = await PostConfirmAsync(oid, harness.StagingBlobName);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await harness.TargetBlobClient.Received(1)
            .SyncCopyFromUriAsync(
                Arg.Any<Uri>(),
                Arg.Any<BlobCopyFromUriOptions>(),
                Arg.Any<CancellationToken>());
        await factory.SearchClient.Received(1)
            .MergeOrUploadDocumentsAsync(
                Arg.Any<IEnumerable<ReceiptDocument>>(),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>());
        await factory.QueueClient.Received(1)
            .SendMessageAsync(Arg.Any<string>());
        await harness.StagingBlobClient.Received(1)
            .DeleteAsync(
                Arg.Any<DeleteSnapshotsOption>(),
                Arg.Any<BlobRequestConditions>(),
                Arg.Any<CancellationToken>());
    }

    private async Task<HttpResponseMessage> PostConfirmAsync(string oid, string stagingBlobName)
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/receipts/confirm")
        {
            Content = JsonContent.Create(new
            {
                stagingBlobName,
                originalFileName = "receipt.png",
            }),
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);
        return await client.SendAsync(request);
    }
}
