using Azure;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using ReceiptWell.Functions;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #8 (ghost documents): if a receipt is deleted while AI extraction is still in
/// flight, <see cref="ReceiptStore.SetReadyAsync"/>/<see cref="ReceiptStore.SetErrorAsync"/>
/// must not resurrect it via <c>MergeOrUploadDocumentsAsync</c>'s upload fallback. Pure unit
/// against a substituted <see cref="SearchClient"/>, matching <c>ReceiptQueryScopingTests</c>.
/// </summary>
public class ReceiptStoreGhostDocumentTests
{
    [Fact]
    public async Task SetReadyAsync_skips_merge_when_receipt_was_deleted()
    {
        var receiptId = Guid.NewGuid().ToString();
        var searchClient = SubstituteSearchClientWithDeletedReceipt(receiptId);
        var store = new ReceiptStore(searchClient, Substitute.For<ILogger<ReceiptStore>>());

        await store.SetReadyAsync(receiptId, storeName: "Store", purchaseDate: null, tags: [], CancellationToken.None);

        AssertNoMergeCall(searchClient);
    }

    [Fact]
    public async Task SetErrorAsync_skips_merge_when_receipt_was_deleted()
    {
        var receiptId = Guid.NewGuid().ToString();
        var searchClient = SubstituteSearchClientWithDeletedReceipt(receiptId);
        var store = new ReceiptStore(searchClient, Substitute.For<ILogger<ReceiptStore>>());

        await store.SetErrorAsync(receiptId, CancellationToken.None);

        AssertNoMergeCall(searchClient);
    }

    [Fact]
    public async Task SetReadyAsync_still_writes_the_merge_when_receipt_exists()
    {
        var receiptId = Guid.NewGuid().ToString();
        var searchClient = SubstituteSearchClientWithExistingReceipt(receiptId);
        var store = new ReceiptStore(searchClient, Substitute.For<ILogger<ReceiptStore>>());

        await store.SetReadyAsync(receiptId, storeName: "Store", purchaseDate: null, tags: [], CancellationToken.None);

        AssertMergeWasCalled(searchClient);
    }

    [Fact]
    public async Task SetErrorAsync_still_writes_the_merge_when_receipt_exists()
    {
        var receiptId = Guid.NewGuid().ToString();
        var searchClient = SubstituteSearchClientWithExistingReceipt(receiptId);
        var store = new ReceiptStore(searchClient, Substitute.For<ILogger<ReceiptStore>>());

        await store.SetErrorAsync(receiptId, CancellationToken.None);

        AssertMergeWasCalled(searchClient);
    }

    private static SearchClient SubstituteSearchClientWithDeletedReceipt(string receiptId)
    {
        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Response<ReceiptDocument>>(
                new RequestFailedException(404, "not found")));
        return searchClient;
    }

    private static SearchClient SubstituteSearchClientWithExistingReceipt(string receiptId)
    {
        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .GetDocumentAsync<ReceiptDocument>(receiptId, Arg.Any<GetDocumentOptions>(), Arg.Any<CancellationToken>())
            .Returns(Response.FromValue(
                new ReceiptDocument { Id = receiptId },
                Substitute.For<Response>()));
        return searchClient;
    }

    private static void AssertNoMergeCall(SearchClient searchClient)
    {
        var mergeCalls = searchClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(SearchClient.MergeOrUploadDocumentsAsync));
        Assert.Empty(mergeCalls);
    }

    private static void AssertMergeWasCalled(SearchClient searchClient)
    {
        var mergeCalls = searchClient.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(SearchClient.MergeOrUploadDocumentsAsync));
        Assert.Single(mergeCalls);
    }
}
