using System.Net;
using System.Net.Http.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// Positive-control integration test for the confirm flow harness. Proves that the harness
/// drives the real <c>POST /receipts/confirm</c> flow to a genuine 200 with all four side
/// effects committed — without this, the Phase 2 failure assertions could be vacuously true
/// by never reaching the targeted step.
/// </summary>
public class ReceiptConfirmFlowTests(ReceiptWellWebFactory factory) : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task Happy_path_confirm_returns_200_with_all_side_effects_committed()
    {
        const string oid = "confirm-happy-path-user";
        var harness = new ConfirmFlowHarness(factory, oid, ConfirmStep.Complete);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/receipts/confirm")
        {
            Content = JsonContent.Create(new
            {
                stagingBlobName = harness.StagingBlobName,
                originalFileName = "receipt.png",
            }),
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // All four side effects must have fired exactly once.
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
}
