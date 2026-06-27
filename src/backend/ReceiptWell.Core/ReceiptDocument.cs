using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace ReceiptWell.Models;

public class ReceiptDocument
{
    [SimpleField(IsKey = true)]
    public string Id { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true)]
    public string UserId { get; set; } = string.Empty;

    [SimpleField]
    public string BlobUrl { get; set; } = string.Empty;

    [SimpleField]
    public string FileName { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true)]
    public long FileSize { get; set; }

    [SimpleField(IsFilterable = true)]
    public string Status { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public DateTimeOffset UploadedAt { get; set; }

    [SearchableField]
    public string? StoreName { get; set; }

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public DateTimeOffset? PurchaseDate { get; set; }

    [SearchableField(IsFilterable = true)]
    public IList<string> Tags { get; set; } = [];

    // Polish-stemmed mirror of Tags. The pl.microsoft analyzer lemmatizes both at index and
    // query time so inflected queries ("rowery") match the stored lemma ("rower"). Search-only:
    // not filterable, not the display source (ReceiptSummary reads Tags). Added as a NEW field
    // so the existing Tags analyzer is never mutated (which would require dropping the index —
    // the sole data store). Populated on enrichment; existing docs backfilled once.
    [SearchableField(AnalyzerName = LexicalAnalyzerName.Values.PlMicrosoft)]
    public IList<string> TagsPl { get; set; } = [];
}
