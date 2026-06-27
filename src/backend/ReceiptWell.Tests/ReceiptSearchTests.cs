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

        // The term is sent as-is; pl.microsoft analyzes it at query time (e.g. "rowery" → "rower").
        Assert.Equal(term, capturedText);
        Assert.NotNull(capturedOptions);
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
        Assert.Contains("UserId", capturedOptions.Filter);
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
