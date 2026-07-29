using System.Net;
using System.Net.Http.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// <c>GET /receipts/{id}/download-url</c> — ownership guard, not-found, auth gate, and
/// SAS issuance shape, mirroring <see cref="ReceiptDeleteTests"/> for the download surface.
/// </summary>
public class ReceiptDownloadTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task Download_of_an_owned_receipt_is_200_with_a_sas_download_uri()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();
        const string oid = "download-happy-path-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = oid, FileName = "receipt.png" },
                Substitute.For<Response>()));

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var receiptsContainerName = config["AzureStorage:ReceiptsContainerName"]!;
        var receiptsContainer = Substitute.For<BlobContainerClient>();
        var targetBlob = Substitute.For<BlobClient>();
        factory.BlobServiceClient.GetBlobContainerClient(receiptsContainerName).Returns(receiptsContainer);
        receiptsContainer.GetBlobClient($"{oid}/{receiptId}").Returns(targetBlob);
        targetBlob.CanGenerateSasUri.Returns(true);
        targetBlob.GenerateSasUri(Arg.Any<BlobSasBuilder>())
            .Returns(new Uri("https://receipts.localhost.test/blob?sig=test-sas-signature"));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/receipts/{receiptId}/download-url");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<DownloadUrlResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.DownloadUri));
        Assert.Contains("sig=", body.DownloadUri);
    }

    [Fact]
    public async Task Download_fails_when_sas_generation_throws_returns_honest_500()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();
        const string oid = "download-failure-shape-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = oid, FileName = "receipt.png" },
                Substitute.For<Response>()));

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var receiptsContainerName = config["AzureStorage:ReceiptsContainerName"]!;
        var receiptsContainer = Substitute.For<BlobContainerClient>();
        var targetBlob = Substitute.For<BlobClient>();
        factory.BlobServiceClient.GetBlobContainerClient(receiptsContainerName).Returns(receiptsContainer);
        receiptsContainer.GetBlobClient($"{oid}/{receiptId}").Returns(targetBlob);
        targetBlob.CanGenerateSasUri.Returns(true);
        targetBlob.GenerateSasUri(Arg.Any<BlobSasBuilder>())
            .Returns(_ => throw new RequestFailedException(500, "Simulated SAS generation failure"));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/receipts/{receiptId}/download-url");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        await ProblemDetailsAssertions.AssertHonest500Async(response);
    }

    [Fact]
    public async Task Download_of_another_users_receipt_is_403_with_no_side_effects()
    {
        factory.SearchClient.ClearReceivedCalls();
        factory.BlobServiceClient.ClearReceivedCalls();
        const string ownerOid = "download-owner-user";
        const string callerOid = "download-other-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = ownerOid },
                Substitute.For<Response>()));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/receipts/{receiptId}/download-url");
        request.Headers.Add(TestAuthHandler.OidHeader, callerOid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        factory.BlobServiceClient.DidNotReceiveWithAnyArgs().GetBlobContainerClient(default!);
    }

    [Fact]
    public async Task Download_of_a_nonexistent_receipt_is_404()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "download-not-found-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<ReceiptDocument>>(
                new RequestFailedException(404, "Not found")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/receipts/{receiptId}/download-url");
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_without_a_token_is_401()
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/receipts/{Guid.NewGuid()}/download-url");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record DownloadUrlResponse(string DownloadUri);
}
