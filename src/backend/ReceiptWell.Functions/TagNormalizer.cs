namespace ReceiptWell.Functions;

public sealed class TagNormalizer
{
    public IReadOnlyList<string> Normalize(IEnumerable<string>? rawTags)
    {
        if (rawTags is null) return [];

        return rawTags
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Where(tag => tag.Length > 0)
            .Distinct()
            .ToList();
    }
}
