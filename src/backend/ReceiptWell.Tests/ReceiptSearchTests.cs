using System.Net;
using System.Net.Http.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using ReceiptWell.Models;
using ReceiptWell.Services;

namespace ReceiptWell.Tests;

/// <summary>
/// Tag-search (S-04) backend contract on <see cref="ReceiptQueryService"/>: a non-blank
/// query term must be applied as the Search <c>searchText</c> against the Polish-stemmed
/// <c>TagsPl</c> field, while a blank/absent term preserves browse-all behavior. In both
/// branches the query stays scoped to the caller's identity. Assertions are
/// requirement-derived (field names + caller id), never an exact copy of the filter string.
/// Polish lemmatization itself runs in the real Search service and is covered by manual
/// verification — the substitute only proves the right options were sent.
/// </summary>
public class ReceiptSearchTests
{
    [Fact]
    public async Task GetReceiptsAsync_with_a_term_searches_TagsPl_and_stays_scoped_to_the_caller()
    {
        const string callerId = "caller-oid-7";
        const string term = "rower";
        string? capturedText = null;
        SearchOptions? capturedOptions = null;

        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .SearchAsync<ReceiptDocument>(
                Arg.Do<string>(t => capturedText = t),
                Arg.Do<SearchOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResults()));

        var service = new ReceiptQueryService(
            searchClient, Substitute.For<ILogger<ReceiptQueryService>>());

        await service.GetReceiptsAsync(callerId, term, CancellationToken.None);

        // Prefix wildcard: term is appended with * so "rower" → "rower*" matches "rowery" etc.
        Assert.Equal(term + "*", capturedText);
        Assert.NotNull(capturedOptions);
        // Lucene full syntax is required for the wildcard to be interpreted.
        Assert.Equal(SearchQueryType.Full, capturedOptions!.QueryType);
        // The term must be matched against the Polish-analyzed field, not the raw Tags field.
        Assert.Contains("TagsPl", capturedOptions.SearchFields);
        // Scoping is term-independent: still filtered by the caller's UserId.
        Assert.Contains("UserId", capturedOptions.Filter);
        Assert.Contains(callerId, capturedOptions.Filter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetReceiptsAsync_with_a_blank_term_browses_all_newest_first(string? blankQuery)
    {
        const string callerId = "caller-oid-7";
        string? capturedText = null;
        SearchOptions? capturedOptions = null;

        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .SearchAsync<ReceiptDocument>(
                Arg.Do<string>(t => capturedText = t),
                Arg.Do<SearchOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResults()));

        var service = new ReceiptQueryService(
            searchClient, Substitute.For<ILogger<ReceiptQueryService>>());

        await service.GetReceiptsAsync(callerId, blankQuery, CancellationToken.None);

        // Browse-all: match-everything search text and recency ordering, no term restriction.
        Assert.Equal("*", capturedText);
        Assert.NotNull(capturedOptions);
        Assert.Contains("UploadedAt desc", capturedOptions!.OrderBy);
        Assert.Empty(capturedOptions.SearchFields);
        // Scoping still applies on the browse path.
        Assert.Contains(callerId, capturedOptions.Filter);
    }

    internal static Response<SearchResults<ReceiptDocument>> EmptyResults()
    {
        var rawResponse = Substitute.For<Response>();
        var results = SearchModelFactory.SearchResults(
            values: Array.Empty<SearchResult<ReceiptDocument>>(),
            totalCount: 0,
            facets: null,
            coverage: null,
            rawResponse: rawResponse);
        return Response.FromValue(results, rawResponse);
    }
}

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
        Assert.Equal("rower*", capturedText);
        Assert.NotNull(capturedOptions);
        Assert.Equal(SearchQueryType.Full, capturedOptions!.QueryType);
        Assert.Contains("TagsPl", capturedOptions.SearchFields);
        Assert.Contains(callerId, capturedOptions.Filter);
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
