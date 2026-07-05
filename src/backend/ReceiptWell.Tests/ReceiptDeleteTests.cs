using System.Net;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// <c>DELETE /receipts/{id}</c> — ownership guard, not-found, and honest-5xx failure
/// shape, mirroring <see cref="ReceiptConfirmOwnershipTests"/> and
/// <see cref="ReceiptConfirmFailureShapeTests"/> for the delete surface (Risk #8).
/// </summary>
public class ReceiptDeleteTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task Delete_of_another_users_receipt_is_403_with_no_side_effects()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string ownerOid = "delete-owner-user";
        const string callerOid = "delete-other-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = ownerOid },
                Substitute.For<Response>()));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/receipts/{receiptId}");
        request.Headers.Add(TestAuthHandler.OidHeader, callerOid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .DeleteDocumentsAsync<ReceiptDocument>(default!);
        factory.BlobServiceClient.DidNotReceiveWithAnyArgs().GetBlobContainerClient(default!);
    }

    [Fact]
    public async Task Delete_of_a_nonexistent_receipt_is_404()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "delete-not-found-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<ReceiptDocument>>(
                new RequestFailedException(404, "Not found")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/receipts/{receiptId}");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_of_an_owned_receipt_is_204_and_removes_both_search_document_and_blob()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();
        const string oid = "delete-happy-path-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = oid },
                Substitute.For<Response>()));
        factory.SearchClient
            .DeleteDocumentsAsync(
                Arg.Any<IEnumerable<ReceiptDocument>>(),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                SearchModelFactory.IndexDocumentsResult(results: Array.Empty<IndexingResult>()),
                Substitute.For<Response>()));

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var receiptsContainerName = config["AzureStorage:ReceiptsContainerName"]!;
        var receiptsContainer = Substitute.For<BlobContainerClient>();
        var targetBlob = Substitute.For<BlobClient>();
        factory.BlobServiceClient.GetBlobContainerClient(receiptsContainerName).Returns(receiptsContainer);
        receiptsContainer.GetBlobClient($"{oid}/{receiptId}").Returns(targetBlob);
        targetBlob.DeleteIfExistsAsync().Returns(Response.FromValue(true, Substitute.For<Response>()));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/receipts/{receiptId}");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SearchClient.Received(1).DeleteDocumentsAsync(
            Arg.Any<IEnumerable<ReceiptDocument>>(),
            Arg.Any<IndexDocumentsOptions>(),
            Arg.Any<CancellationToken>());
        await targetBlob.Received(1).DeleteIfExistsAsync();
    }

    [Fact]
    public async Task BlobDelete_fails_after_successful_search_delete_returns_honest_500()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();
        const string oid = "delete-failure-shape-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = oid },
                Substitute.For<Response>()));
        factory.SearchClient
            .DeleteDocumentsAsync(
                Arg.Any<IEnumerable<ReceiptDocument>>(),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                SearchModelFactory.IndexDocumentsResult(results: Array.Empty<IndexingResult>()),
                Substitute.For<Response>()));

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var receiptsContainerName = config["AzureStorage:ReceiptsContainerName"]!;
        var receiptsContainer = Substitute.For<BlobContainerClient>();
        var targetBlob = Substitute.For<BlobClient>();
        factory.BlobServiceClient.GetBlobContainerClient(receiptsContainerName).Returns(receiptsContainer);
        receiptsContainer.GetBlobClient($"{oid}/{receiptId}").Returns(targetBlob);
        targetBlob.DeleteIfExistsAsync().Returns(Task.FromException<Response<bool>>(
            new RequestFailedException(500, "Simulated blob delete failure")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/receipts/{receiptId}");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        await ProblemDetailsAssertions.AssertHonest500Async(response);
        await factory.SearchClient.Received(1).DeleteDocumentsAsync(
            Arg.Any<IEnumerable<ReceiptDocument>>(),
            Arg.Any<IndexDocumentsOptions>(),
            Arg.Any<CancellationToken>());
        await targetBlob.Received(1).DeleteIfExistsAsync();
    }
}
