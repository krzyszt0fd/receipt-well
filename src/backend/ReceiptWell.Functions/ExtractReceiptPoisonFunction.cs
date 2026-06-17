using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ReceiptWell.Functions;

// Fires when the Functions host moves a message to the auto-created "<queue>-poison" queue after
// maxDequeueCount retries are exhausted (host.json). The host does not re-invoke ExtractReceiptFunction
// for poisoned messages, so this dedicated handler guarantees no receipt is left perpetually "pending".
public partial class ExtractReceiptPoisonFunction(ReceiptStore receiptStore, ILogger<ExtractReceiptPoisonFunction> logger)
{
    [Function(nameof(ExtractReceiptPoisonFunction))]
    public async Task Run(
        [QueueTrigger("%ExtractionQueueName%-poison", Connection = "AzureWebJobsStorage")] string receiptId,
        CancellationToken cancellationToken)
    {
        await receiptStore.SetErrorAsync(receiptId, cancellationToken);
        LogPoisonHandled(logger, receiptId);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Retry-exhausted receiptId {ReceiptId} routed to poison queue; marked as error")]
    private static partial void LogPoisonHandled(ILogger logger, string receiptId);
}
