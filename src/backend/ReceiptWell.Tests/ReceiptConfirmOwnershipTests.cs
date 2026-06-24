using System.Net;
using System.Net.Http.Json;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #1 (IDOR at confirm): a caller must not be able to confirm a blob staged under
/// another user's prefix. The prefix guard (<c>ReceiptConfirmService</c>) short-circuits
/// with <b>403</b> <em>before</em> any Azure client is touched, so the test asserts both
/// the status <b>and</b> the absence of every cross-user side effect (no container access,
/// no index write, no enqueue). Two distinct identities are mandatory — a single-user
/// fixture would hide the leak. The sibling-prefix case (<c>abc</c> vs <c>abcd/…</c>)
/// proves the trailing <c>/</c> in the <c>Ordinal</c> check is load-bearing.
/// </summary>
public class ReceiptConfirmOwnershipTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    /// <summary>(callerOid, blobName-owned-by-someone-else).</summary>
    public static TheoryData<string, string> CrossUserBlobs => new()
    {
        // Plainly a different user's prefix.
        { "user-a", $"user-b/{Guid.NewGuid()}" },
        // Sibling prefix: "abcd/..." must NOT be treated as owned by "abc" — the guard
        // compares against "abc/" so the trailing slash blocks the false match.
        { "abc", $"abcd/{Guid.NewGuid()}" },
    };

    [Theory]
    [MemberData(nameof(CrossUserBlobs))]
    public async Task Confirm_of_another_users_blob_is_403_with_no_side_effects(
        string callerOid, string foreignBlobName)
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/receipts/confirm")
        {
            Content = JsonContent.Create(new
            {
                stagingBlobName = foreignBlobName,
                originalFileName = "receipt.png",
            }),
        };
        request.Headers.Add(TestAuthHandler.OidHeader, callerOid);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // The guard runs before any Azure call — prove the rejected path performed no
        // cross-user work: no blob copy, no index write, no enqueue.
        factory.BlobServiceClient.DidNotReceiveWithAnyArgs().GetBlobContainerClient(default!);
        await factory.SearchClient.DidNotReceiveWithAnyArgs()
            .MergeOrUploadDocumentsAsync<ReceiptDocument>(default!);
        await factory.QueueClient.DidNotReceiveWithAnyArgs().SendMessageAsync(default(string)!);
    }
}
