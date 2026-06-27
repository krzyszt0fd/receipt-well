using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #7 — infra-boundary failure shape for the single-step endpoints.
/// Proves the staging-slot upload and receipt-list endpoints surface an honest 500
/// when their single dependency throws, with no spurious side effects.
/// </summary>
public class ReceiptEndpointFailureShapeTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task StagingSlot_upload_failure_returns_honest_500()
    {
        factory.BlobServiceClient.ClearReceivedCalls();

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var stagingContainerName = config["AzureStorage:StagingContainerName"]!;

        var stagingContainer = Substitute.For<BlobContainerClient>();
        var stagingBlob = Substitute.For<BlobClient>();
        factory.BlobServiceClient.GetBlobContainerClient(stagingContainerName)
            .Returns(stagingContainer);
        stagingContainer.GetBlobClient(Arg.Any<string>()).Returns(stagingBlob);
        stagingBlob.UploadAsync(Arg.Any<BinaryData>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<BlobContentInfo>>(
                new RequestFailedException(500, "Simulated upload failure")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/receipts/staging-slot");
        request.Headers.Add(TestAuthHandler.OidHeader, "staging-slot-failure-user");

        var response = await client.SendAsync(request);

        await ProblemDetailsAssertions.AssertHonest500Async(response);
    }

    [Fact]
    public async Task ListSearch_failure_returns_honest_500_with_no_write_side_effects()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.QueueClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();

        factory.SearchClient.SearchAsync<ReceiptDocument>(
                Arg.Any<string>(), Arg.Any<SearchOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<SearchResults<ReceiptDocument>>>(
                new RequestFailedException(500, "Simulated search failure")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/receipts");
        request.Headers.Add(TestAuthHandler.OidHeader, "list-search-failure-user");

        var response = await client.SendAsync(request);

        await ProblemDetailsAssertions.AssertHonest500Async(response);

        factory.BlobServiceClient.DidNotReceiveWithAnyArgs()
            .GetBlobContainerClient(default!);
        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptDocument>(default!);
        await factory.QueueClient.DidNotReceiveWithAnyArgs()
            .SendMessageAsync(default(string)!);
    }
}
