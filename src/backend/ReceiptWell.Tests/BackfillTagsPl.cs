using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using ReceiptWell.Models;

namespace ReceiptWell.Tests;

/// <summary>
/// THROWAWAY one-time backfill — delete after running once per environment.
///
/// The Polish-stemmed <c>TagsPl</c> field is empty on documents indexed before tag-search
/// shipped. This runner copies <c>Tags</c> → <c>TagsPl</c> for every existing document so
/// pre-change receipts become searchable with stemming. New uploads need no backfill —
/// <c>ReceiptStore.SetReadyAsync</c> populates <c>TagsPl</c> going forward.
///
/// Idempotent (merge), safe to re-run. Skipped by default so it never runs in CI; remove the
/// Skip and supply the env vars below to execute against a target environment, then discard
/// this file.
///
///   RW_SEARCH_URI       e.g. https://&lt;service&gt;.search.windows.net
///   RW_SEARCH_API_KEY   an admin key (write access)
///   RW_SEARCH_INDEX     the index name
/// </summary>
public class BackfillTagsPl
{
    [Fact(Skip = "One-time manual backfill — set env vars and remove Skip to run, then delete this file.")]
    public async Task Copy_Tags_into_TagsPl_for_all_existing_documents()
    {
        var uri = RequireEnv("RW_SEARCH_URI");
        var apiKey = RequireEnv("RW_SEARCH_API_KEY");
        var index = RequireEnv("RW_SEARCH_INDEX");

        var client = new SearchClient(new Uri(uri), index, new AzureKeyCredential(apiKey));

        var options = new SearchOptions { Size = 1000 };
        options.Select.Add(nameof(ReceiptDocument.Id));
        options.Select.Add(nameof(ReceiptDocument.Tags));

        var response = await client.SearchAsync<ReceiptDocument>("*", options);

        var batch = new List<TagsPlMerge>();
        await foreach (var result in response.Value.GetResultsAsync())
        {
            var doc = result.Document;
            // Partial doc: only Id + TagsPl are serialized, so merge leaves every other field
            // untouched. Merging a full ReceiptDocument would overwrite Tags/UserId/etc. with
            // empty defaults — never do that here.
            batch.Add(new TagsPlMerge { Id = doc.Id, TagsPl = doc.Tags });
            if (batch.Count == 1000)
            {
                await client.MergeOrUploadDocumentsAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await client.MergeOrUploadDocumentsAsync(batch);
        }
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Backfill requires the {name} environment variable.");

    // Partial merge document — only these two fields are sent to MergeOrUploadDocumentsAsync.
    private sealed class TagsPlMerge
    {
        public string Id { get; set; } = string.Empty;
        public IList<string> TagsPl { get; set; } = [];
    }
}
