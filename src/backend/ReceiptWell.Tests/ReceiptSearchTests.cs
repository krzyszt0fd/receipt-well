using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using ReceiptWell.Models;
using ReceiptWell.Services;

namespace ReceiptWell.Tests;

/// <summary>
/// Combined-search backend contract on <see cref="ReceiptQueryService"/>: a non-blank query
/// term must be sent as a single boosted <c>QueryType.Full</c> query that unions an analyzed
/// lemma clause against <c>TagsPl</c> with a prefix wildcard clause against <c>Tags</c>, while
/// a blank/absent term preserves browse-all behavior. In both branches the query stays scoped
/// to the caller's identity. Assertions are requirement-derived (clause/field substrings +
/// caller id), never an exact copy of the query string. Polish lemmatization itself runs in the
/// real Search service and is covered by manual verification — the substitute only proves the
/// right query was sent.
/// </summary>
public class ReceiptSearchTests
{
    [Fact]
    public async Task GetReceiptsAsync_with_a_term_sends_a_combined_boosted_full_query_scoped_to_the_caller()
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

        Assert.NotNull(capturedText);
        // Lemma clause: analyzed, boosted above the prefix clause.
        Assert.Contains($"TagsPl:{term}^3", capturedText);
        // Prefix clause: raw wildcard against the non-lemmatized field.
        Assert.Contains($"Tags:{term}*", capturedText);
        Assert.NotNull(capturedOptions);
        // Field scoping now lives in the query string, not SearchFields.
        Assert.Empty(capturedOptions.SearchFields);
        Assert.Equal(SearchQueryType.Full, capturedOptions.QueryType);
        // Scoping is term-independent: still filtered by the caller's UserId.
        Assert.Contains("UserId", capturedOptions.Filter);
        Assert.Contains(callerId, capturedOptions.Filter);
    }

    [Fact]
    public async Task GetReceiptsAsync_escapes_lucene_special_characters_in_the_term()
    {
        const string callerId = "caller-oid-8";
        const string term = "foo:bar+baz";
        string? capturedText = null;

        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .SearchAsync<ReceiptDocument>(
                Arg.Do<string>(t => capturedText = t),
                Arg.Any<SearchOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResults()));

        var service = new ReceiptQueryService(
            searchClient, Substitute.For<ILogger<ReceiptQueryService>>());

        await service.GetReceiptsAsync(callerId, term, CancellationToken.None);

        Assert.NotNull(capturedText);
        // Special characters must be backslash-escaped before the wildcard/boost operators
        // are appended, so a term containing Lucene syntax can never inject query structure.
        Assert.Contains(@"foo\:bar\+baz", capturedText);
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
