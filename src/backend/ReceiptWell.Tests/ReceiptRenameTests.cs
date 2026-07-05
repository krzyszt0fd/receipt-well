using System.Net;
using System.Net.Http.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ReceiptWell.Models;
using ReceiptWell.Services;

namespace ReceiptWell.Tests;

/// <summary>
/// <c>PUT /receipts/{id}</c> — ownership guard, not-found, empty-name validation, and the
/// partial-merge happy path that renames only <c>FileName</c> while leaving AI-written
/// fields untouched. Mirrors <see cref="ReceiptDeleteTests"/> for the rename surface.
/// </summary>
public class ReceiptRenameTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task Rename_of_another_users_receipt_is_403_with_no_side_effects()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string ownerOid = "rename-owner-user";
        const string callerOid = "rename-other-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId, UserId = ownerOid },
                Substitute.For<Response>()));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/receipts/{receiptId}")
        {
            Content = JsonContent.Create(new { fileName = "new-name.jpg" })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, callerOid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptRenameDocument>(default!);
    }

    [Fact]
    public async Task Rename_of_a_nonexistent_receipt_is_404()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "rename-not-found-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<ReceiptDocument>>(
                new RequestFailedException(404, "Not found")));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/receipts/{receiptId}")
        {
            Content = JsonContent.Create(new { fileName = "new-name.jpg" })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Rename_of_an_owned_receipt_is_204_and_merges_only_the_filename()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "rename-happy-path-user";
        var receiptId = Guid.NewGuid().ToString();

        factory.SearchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument
                {
                    Id = receiptId,
                    UserId = oid,
                    FileName = "old-name.jpg",
                    StoreName = "Corner Store",
                    Tags = new List<string> { "food" }
                },
                Substitute.For<Response>()));

        IEnumerable<ReceiptRenameDocument>? mergedDocuments = null;
        factory.SearchClient
            .MergeOrUploadDocumentsAsync(
                Arg.Do<IEnumerable<ReceiptRenameDocument>>(docs => mergedDocuments = docs),
                Arg.Any<IndexDocumentsOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                SearchModelFactory.IndexDocumentsResult(results: Array.Empty<IndexingResult>()),
                Substitute.For<Response>()));

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/receipts/{receiptId}")
        {
            Content = JsonContent.Create(new { fileName = "renamed-receipt.jpg" })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await factory.SearchClient.Received(1).MergeOrUploadDocumentsAsync(
            Arg.Any<IEnumerable<ReceiptRenameDocument>>(),
            Arg.Any<IndexDocumentsOptions>(),
            Arg.Any<CancellationToken>());

        // The merge must use the partial-merge DTO — a type that carries ONLY Id + FileName —
        // so no defaulted UserId/Status/FileSize/Tags can clobber the stored document.
        var merged = Assert.Single(mergedDocuments!);
        Assert.Equal(receiptId, merged.Id);
        Assert.Equal("renamed-receipt.jpg", merged.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rename_with_empty_or_whitespace_name_is_400_with_no_merge(string fileName)
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "rename-empty-name-user";
        var receiptId = Guid.NewGuid().ToString();

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/receipts/{receiptId}")
        {
            Content = JsonContent.Create(new { fileName })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptRenameDocument>(default!);
    }

    [Fact]
    public async Task Rename_with_a_name_over_255_characters_is_400_with_no_merge()
    {
        factory.SearchClient.ClearReceivedCalls();
        const string oid = "rename-too-long-name-user";
        var receiptId = Guid.NewGuid().ToString();
        var fileName = new string('a', 256);

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/receipts/{receiptId}")
        {
            Content = JsonContent.Create(new { fileName })
        };
        request.Headers.Add(TestAuthHandler.OidHeader, oid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptRenameDocument>(default!);
    }
}
