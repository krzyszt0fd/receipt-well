---
change_id: ai-extraction-and-enrichment
doc: vision-model-research
created: 2026-06-14
updated: 2026-06-14
method: external research (exa.ai web search, 2026 benchmarks)
status: draft
---

# Vision model research — S-03 AI extraction & enrichment

> External research feeding `/10x-plan ai-extraction-and-enrichment`.
> Question: which AI model to use for extracting `sklep` / `data` / `tagi` from
> photographed, often faded Polish receipts, async in an Azure Function.

## Recommendation

**Default: GPT-4o vision via Azure OpenAI**, with **Claude Sonnet 4.6 as the
fallback to A/B test** against real Polish receipts. Treat the model as a
swappable component behind an interface.

Rationale is driven as much by **stack fit** as by raw accuracy — the stack is
Azure App Service + Azure Functions + .NET (`context/foundation/tech-stack.md`),
and `tech-stack.md` left the SDK open as "Anthropic or OpenAI SDK for .NET".

## Why GPT-4o on Azure OpenAI is the default

| Factor | Evidence |
| --- | --- |
| Stack-native | `Azure.AI.OpenAI` .NET SDK, managed identity (no keys in config — matches security guardrails), EU-region data residency for receipt PII. Microsoft ships first-party .NET samples doing exactly this (GPT-4o vision + Structured Outputs → invoice/receipt JSON). |
| Degraded-scan OCR | The roadmap's top risk is "zbladnięte i niskiej jakości paragony." 2026 benchmarks put GPT-4o/GPT-5 vision ahead on degraded/scanned OCR (~97.3% vs ~93.5% Claude, ~94% Gemini on faded receipts). |
| Structured output | Azure OpenAI Structured Outputs enforces a JSON schema at the API level (~99% schema compliance vs ~91% prompt-only). Define a C# DTO → `OpenAIJsonSchema.For` → strict `{ store, date, tags[] }`. |
| Native image input | Benchmarks consistent: feeding the image directly beats OCR-to-markdown-then-extract (87% vs 47% on scanned receipts). Skips a separate OCR stage — one call per receipt in the Function. |
| Polish tags | Polish is Latin-script and well-resourced; both GPT and Claude handle it. GPT rated marginally stronger on multilingual extraction in invoice benchmarks. |

## Why Claude Sonnet 4.6 is the alternative worth measuring

The roadmap's roadmap-wide Unknown is whether extraction quality is good enough
that tag search (S-04) is useful. That's exactly where Claude's edge matters:

- **Lowest hallucination rate** (0.09% vs GPT 0.15%) — a hallucinated tag
  pollutes search and breaks user trust silently, worse than a missing tag.
- **Highest structured-field extraction accuracy** (~97.6%) and best on
  complex/nested layouts.
- Available on Azure too (Azure AI Foundry model catalog), so it doesn't leave
  the stack.
- Trade: raw OCR on faded-paper edge cases a few points behind GPT.

## Models considered (2026 benchmark snapshot)

| Model | Field extraction | OCR (degraded scans) | Hallucination | Notes |
| --- | --- | --- | --- | --- |
| GPT-4o / GPT-5.x vision | ~95.2% | **~97.3% (best)** | 0.15% | Native on Azure OpenAI; best degraded OCR; Structured Outputs |
| Claude Sonnet 4.6 | **~97.6% (best)** | ~93.5% | **0.09% (best)** | On Azure AI Foundry; lowest hallucination; strong multilingual |
| Gemini 2.5/3.1 Pro | ~93.8% | ~94.1% | — | Strongest on bulk/large docs but ties to Google Cloud — off-stack |

Numbers are third-party 2026 benchmarks on invoice/receipt corpora, **not**
Polish thermal-paper receipts. They only justify which two models are worth
testing; the validation below is the decision-maker.

## What to do for S-03

1. Build the pipeline against **Azure OpenAI GPT-4o** first — lowest-friction
   path with the most .NET reference code. Resolves the open SDK question in
   `tech-stack.md` toward Azure OpenAI rather than raw Anthropic SDK.
2. Keep the model **swappable behind an interface**. Run the roadmap's required
   5–10 real Polish receipts through **both GPT-4o and Claude Sonnet 4.6**; pick
   on observed hallucination + tag usefulness, not benchmark numbers.
3. Avoid a dedicated-OCR pre-pass (Tesseract / Document Intelligence) unless
   step 2 shows GPT-4o failing on the specific faded receipts — native vision is
   simpler and benchmarks favor it.

## SDK architecture — keep the model swappable (verified via Context7)

`Azure.AI.OpenAI` alone is **OpenAI-only** — it speaks to Azure OpenAI
endpoints, not Claude. Claude on Azure AI Foundry is a different API surface, so
a future switch would mean rewriting the client. To honor the "swappable behind
an interface" decision, code the extraction service against
**`Microsoft.Extensions.AI` (`IChatClient`)** and plug a concrete provider
underneath.

- **Abstraction:** `Microsoft.Extensions.AI` — provider-agnostic `IChatClient`,
  supports image content + typed structured output. Swap GPT-4o → Claude later
  with no change to extraction logic.
- **Concrete provider (today):** `Microsoft.Extensions.AI.OpenAI` over
  `Azure.AI.OpenAI`. Wire GPT-4o via `AsIChatClient()`.
- **Concrete provider (later):** an Anthropic/Foundry-backed `IChatClient`.

### Wiring GPT-4o behind IChatClient (keyless / managed identity)

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

// Managed identity — no keys in config (matches security guardrails)
IChatClient chatClient =
    new AzureOpenAIClient(
            new Uri(endpoint),                 // from config, not git-tracked
            new DefaultAzureCredential())
        .GetChatClient("gpt-4o")               // Azure deployment name
        .AsIChatClient();
```

### Vision input + typed Structured Output

```csharp
// Strongly-typed result the planner can map to the Receipt entity
sealed record ReceiptExtraction(string Store, DateOnly? Date, string[] Tags);

var messages = new List<ChatMessage>
{
    new(ChatRole.System,
        "Extract the store, issue date, and Polish-normalized product tags " +
        "from this receipt photo. Use null for fields you cannot read."),
    new(ChatRole.User,
    [
        new TextContent("Receipt image:"),
        new DataContent(imageBytes, "image/jpeg"),   // blob bytes from S-01
    ]),
};

// GetResponseAsync<T> builds the JSON schema from the record and parses .Result
ChatResponse<ReceiptExtraction> response =
    await chatClient.GetResponseAsync<ReceiptExtraction>(messages);

ReceiptExtraction data = response.Result;
```

Notes from the docs:
- `GetResponseAsync<T>` / `ChatResponseFormat.ForJsonSchema<T>` generate the
  schema from the type; M.E.AI applies OpenAI **strict-mode** transforms
  (`DisallowAdditionalProperties`, `RequireAllProperties`) automatically. Some
  JSON-schema keywords (`pattern`, `format`, `minLength`, …) are unsupported in
  strict mode and get folded into descriptions — don't rely on them for
  validation; validate in C# after parse.
- Non-object root schemas are auto-wrapped in an object; a `record` root is fine.
- Provider-specific lever if ever needed: raw `Azure.AI.OpenAI` uses
  `ChatResponseFormat.CreateJsonSchemaFormat(name, schema, jsonSchemaIsStrict: true)`
  and image parts via `ChatMessageContentPart.CreateImagePart(...)`. Prefer the
  M.E.AI abstraction above to stay portable.

## Open follow-up

- Confirm Azure OpenAI GPT-4o (and Claude on Foundry) availability in the target
  Azure region for EU data residency.
- Verify the exact `Microsoft.Extensions.AI` / `Azure.AI.OpenAI` package
  versions at implement time (APIs above are from current Context7 docs but were
  recently pre-GA in places).

## Sources

- arxiv 2509.04469 — native image vs markdown parsing for invoice extraction
- TokenMix vision/document benchmarks (2026)
- CodeSOTA / DeployBase / Unprompted Mind — Claude vs GPT OCR comparisons (2026)
- Azure-Samples/azure-ai-document-processing-samples — .NET GPT-4o vision +
  Structured Outputs receipt/invoice extraction
- Azure/ai-document-processing-pipeline — serverless Functions + GPT-4o pipeline
