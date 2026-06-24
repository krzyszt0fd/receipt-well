using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using ReceiptWell.Models;
using ReceiptWell.Services;

namespace ReceiptWell.Tests;

/// <summary>
/// Risk #1 (list scoping): <c>GET /receipts</c> must build a Search query scoped to the
/// caller's identity, not rely on C# post-filtering. This is a direct unit on
/// <see cref="ReceiptQueryService"/> with a substituted <see cref="SearchClient"/> that
/// captures the <see cref="SearchOptions.Filter"/>. The assertion is requirement-derived
/// — the filter must reference the <c>UserId</c> field and carry the caller's id — rather
/// than an exact-string equality that would merely mirror the implementation.
/// </summary>
public class ReceiptQueryScopingTests
{
    [Fact]
    public async Task GetReceiptsAsync_scopes_the_search_filter_to_the_caller()
    {
        const string callerId = "caller-oid-42";
        SearchOptions? capturedOptions = null;

        var searchClient = Substitute.For<SearchClient>();
        searchClient
            .SearchAsync<ReceiptDocument>(
                Arg.Any<string>(),
                Arg.Do<SearchOptions>(o => capturedOptions = o),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(EmptyResults()));

        var service = new ReceiptQueryService(
            searchClient, Substitute.For<ILogger<ReceiptQueryService>>());

        await service.GetReceiptsAsync(callerId, CancellationToken.None);

        Assert.NotNull(capturedOptions);
        // Requirement: the query is scoped by the caller's identity field. We assert the
        // filter references the UserId field AND contains the caller's id — NOT the exact
        // string $"UserId eq '{userId}'", which would mirror the implementation.
        Assert.Contains("UserId", capturedOptions!.Filter);
        Assert.Contains(callerId, capturedOptions.Filter);
    }

    private static Response<SearchResults<ReceiptDocument>> EmptyResults()
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
