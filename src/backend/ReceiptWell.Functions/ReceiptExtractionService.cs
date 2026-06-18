using Microsoft.Extensions.AI;

namespace ReceiptWell.Functions;

public sealed record ReceiptExtractionResult(string? StoreName, DateOnly? PurchaseDate, IReadOnlyList<string> Tags);

internal sealed record ReceiptExtractionDto(string? StoreName, DateOnly? PurchaseDate, string[]? Tags);

public sealed class UnreadableReceiptException() : Exception("Model extracted no fields; image is not a readable receipt.");

public sealed class ReceiptExtractionService(IChatClient chatClient)
{
    private const string SystemPrompt =
        "You are extracting structured data from a photographed retail receipt, usually Polish and " +
        "sometimes faded or low quality. Extract the store name, the purchase date, and a list of " +
        "Polish-normalized tags fully describing the purchased products or services. For each distinct " +
        "item or service on the receipt, produce several tags spanning different levels of generality " +
        "— the specific item, its category, and the broader domain it belongs to — instead of a single " +
        "generic tag. For example, for \"usługa fizjoterapeutyczna\" produce [\"usługa\", \"fizjoterapia\", " +
        "\"zdrowie\"]; for \"chleb żytni\" produce [\"pieczywo\", \"chleb\", \"żytni\"]. If the item is a " +
        "branded product rather than a service, also include separate tags for its kind/category, its " +
        "manufacturer or brand, and its model or variant name when visible on the receipt — e.g. for " +
        "\"Jogurt Danone Activia\" produce [\"jogurt\", \"danone\", \"activia\"]. Favor more descriptive " +
        "tags over fewer generic ones. Only tag distinct purchased products or services that appear as " +
        "their own priced line item. Never produce a tag from receipt boilerplate: price adjustments " +
        "(\"promocja\", \"zniżka\", \"obniżka\", \"rabat\"), delivery/fulfillment fees (\"dostawa\", " +
        "\"kurier\", \"wysyłka\"), or any other non-product text such as payment method, return/exchange " +
        "policy notices, loyalty-program text, store address, or legal/footer disclaimers. If a fragment " +
        "of such boilerplate text is unclear or partially cut off, that is exactly the kind of text to " +
        "drop, not approximate into a tag. Use null for any field you cannot read with confidence. Do " +
        "not invent data that is not visible on the receipt.";

    public async Task<ReceiptExtractionResult> ExtractAsync(
        byte[] imageBytes, string contentType, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User,
            [
                new TextContent("Receipt image:"),
                new DataContent(imageBytes, contentType)
            ])
        ];

        var response = await chatClient.GetResponseAsync<ReceiptExtractionDto>(
            messages, cancellationToken: cancellationToken);

        var data = response.Result;
        if (data.StoreName is null && data.PurchaseDate is null && (data.Tags is null || data.Tags.Length == 0))
        {
            throw new UnreadableReceiptException();
        }

        return new ReceiptExtractionResult(data.StoreName, data.PurchaseDate, data.Tags ?? []);
    }
}
