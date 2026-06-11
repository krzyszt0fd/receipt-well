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
}
