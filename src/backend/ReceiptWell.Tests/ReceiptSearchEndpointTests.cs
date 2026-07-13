using System.Net;
using System.Net.Http.Json;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// End-to-end contract for the <c>?q=</c> search parameter on <c>GET /receipts</c>, booted
/// through <see cref="ReceiptWellWebFactory"/> with the substituted <see cref="SearchClient"/>.
/// Proves the term threads through routing + auth into the Search call (scoped to the caller's
/// oid) and that a zero-match search returns an empty 200 — never a 400 (non-retriable).
/// </summary>
public class ReceiptSearchEndpointTests(ReceiptWellWebFactory factory)
    : IClassFixture<ReceiptWellWebFactory>
{
    [Fact]
    public async Task GET_receipts_with_q_forwards_the_term_to_Search_scoped_to_the_caller()
    {
        const string callerId = "endpoint-caller-9";
        string? capturedText = null;
        SearchOptions? capturedOptions = null;
        ConfigureSearch(t => capturedText = t, o => capturedOptions = o);

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/receipts?q=rower");
        request.Headers.Add(TestAuthHandler.OidHeader, callerId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capturedText);
        Assert.Contains("TagsPl:rower^3", capturedText);
        Assert.Contains("Tags:rower*", capturedText);
        Assert.NotNull(capturedOptions);
        Assert.Contains(callerId, capturedOptions!.Filter);
    }

    [Fact]
    public async Task GET_receipts_with_zero_matches_returns_an_empty_200_not_400()
    {
        ConfigureSearch(_ => { }, _ => { });

        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/receipts?q=nieistniejacy");
        request.Headers.Add(TestAuthHandler.OidHeader, "endpoint-caller-9");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ReceiptSummary[]>();
        Assert.NotNull(body);
        Assert.Empty(body!);
    }

    private void ConfigureSearch(Action<string> onText, Action<SearchOptions> onOptions)
    {
        factory.SearchClient
            .SearchAsync<ReceiptDocument>(
                Arg.Do<string>(onText),
                Arg.Do<SearchOptions>(onOptions),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReceiptSearchTests.EmptyResults()));
    }
}
