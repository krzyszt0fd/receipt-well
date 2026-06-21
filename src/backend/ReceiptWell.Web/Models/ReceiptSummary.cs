namespace ReceiptWell.Models;

public record ReceiptSummary(
    string Id,
    string FileName,
    long FileSize,
    string Status,
    DateTimeOffset UploadedAt,
    string? StoreName,
    DateTimeOffset? PurchaseDate,
    IReadOnlyList<string> Tags);
