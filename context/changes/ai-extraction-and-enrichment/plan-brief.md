# AI Extraction & Enrichment (S-03) — Plan Brief

> Full plan: `context/changes/ai-extraction-and-enrichment/plan.md`
> Research: `context/changes/ai-extraction-and-enrichment/research.md`
> External research: `context/changes/ai-extraction-and-enrichment/vision-model-research.md`

## What & Why

ReceiptWell's core promise is "photograph a receipt and instantly get a searchable entry — no manual typing." Today a confirmed receipt sits in the Search index as `pending` with empty fields. This change adds the async pipeline that fills them: an Azure Function calls GPT-4o vision to extract store name, purchase date, and Polish-normalized product tags, then writes them back and marks the receipt `ready`. Without it, S-02's list shows everything stuck at "w trakcie" and tag search (S-04) has nothing to search.

## Starting Point

S-01 froze the `ReceiptDocument` index schema with nullable `StoreName`/`PurchaseDate`/`Tags` specifically so S-03 fills them with no migration, and tied the upload allowlist to a GPT-class vision model. The confirm endpoint writes the pending doc synchronously and emits nothing downstream. There is no Azure Functions project, no Storage Queue, and no Azure OpenAI resource yet. Auth is mixed: blob/Key Vault use managed identity, Azure Search uses an API key.

## Desired End State

A confirmed receipt is enqueued, picked up by an out-of-process Function, enriched via GPT-4o, and shown on the list with store/date/tags and `Status = ready` — within seconds-to-minutes, no user action. Unreadable receipts, content-filter blocks, or exhausted retries land at a terminal `error` state (photo + partial data kept), never perpetual "pending". GIF is rejected at upload. The whole loop runs locally against Azurite with zero cloud coupling.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Trigger / hosting | Azure Functions (Consumption) + Storage Queue trigger | Survives D1's "no always-on" and keeps a local Azurite dev loop without Event Grid infra | Research |
| Model + SDK | GPT-4o on Azure OpenAI behind `Microsoft.Extensions.AI` `IChatClient` | Best degraded-OCR scores, stack-native, kept swappable for a later Claude A/B | Research |
| Code sharing | Extract `ReceiptDocument` into `ReceiptWell.Core` | One source of truth for the frozen schema; no drift between API and Function | Plan |
| Failure status | Terminal `Status = "error"`, visible on the list | Honors PRD fallback and gives S-02 a real state; no infinite reprocessing | Plan |
| OpenAI auth | ApiKey-or-MI switch (key set → key; empty → managed identity) | Mirrors the existing blob `ConnectionString` pattern; MI in Azure, key for local dev | Plan |
| Search auth | Keep the existing API key | Matches S-01; avoids scope-creep into Search RBAC | Plan |
| Tag normalization | Prompt (Polish) + C# trim/lowercase/dedupe, no taxonomy | Prevents search-breaking inconsistencies while honoring PRD's unbounded tags | Plan |
| Queue message | `receiptId` only; idempotent via `MergeOrUpload` + `ready` short-circuit | Smallest message, index is single source of truth, redelivery is safe | Plan |
| Enqueue failure | Fail confirm with 500 so the client retries | No silently-stuck-pending receipts; reuses idempotent re-write | Plan |
| GIF support | Removed from upload entirely (client + server); allowlist PNG/JPEG/WEBP | User decision; supersedes S-01's deferred animated-GIF inspection | Plan |
| Testing | Manual validation on 5–10 real receipts; no test project | User decision; LLM accuracy is inherently a manual judgment | Plan |

## Scope

**In scope:** shared `ReceiptWell.Core` library; GIF removal (backend + frontend); queue producer in the API; isolated-worker Function with GPT-4o extraction, Polish tag normalization, status transitions, and failure handling; Terraform for Azure OpenAI + queue + Function App + identities/roles; a second CI/CD workflow; manual real-receipt validation.

**Out of scope:** automated test project; Event Grid; in-process worker; schema/index changes; price & product-name persistence (tags only); fixed tag taxonomy; animated-GIF frame inspection; Search auth changes; manual-retry UI for `error` receipts; backfill of pre-existing `pending` receipts.

## Architecture / Approach

`POST /receipts/confirm` → writes pending `ReceiptDocument` → enqueues `receiptId` to a Storage Queue. A `[QueueTrigger]` Azure Function (isolated worker, Consumption) loads the doc, short-circuits if already `ready`, downloads the blob, calls GPT-4o via `IChatClient` with typed structured output, normalizes tags in C#, and `MergeOrUpload`s the fields + `Status`. Local/Azure isolation comes from the Azurite emulator (a physically separate endpoint), not distinct queue names — connection is selected by config exactly as the existing blob client does.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Core lib + drop GIF | `ReceiptWell.Core` + `.sln`; GIF removed client+server | Model-move regression in API/index build |
| 2. Queue producer | Confirm enqueues `receiptId`, 500 on enqueue failure | Producer/doc-write ordering race |
| 3. Azure OpenAI provisioning | OpenAI account + GPT-4o deployment + content filter | Region/model availability; content-filter policy |
| 4. Function worker | Queue → GPT-4o → normalize → merge → status | LLM wiring (pre-GA package versions); poison→error mapping |
| 5. Remaining infrastructure | Queue + Function App + identities + roles + app-settings | Identity-based `AzureWebJobsStorage` roles |
| 6. CI/CD + validation | Function deploy workflow; real-receipt batch | Extraction quality insufficient on faded receipts |

**Prerequisites:** F-01 (auth gate) and S-01 (upload/confirm) are complete; an Azure subscription with the existing infra applied. Azure OpenAI is provisioned in Phase 3, ahead of the Function code, so Phase 4's local validation has a live endpoint (via ApiKey). Local tooling for Phases 4–5: .NET 9 SDK, the Azure Functions Core Tools v4 (`func` — used to scaffold the project/function and run `func start`; bundles its own templates), and Azurite (the Storage emulator) for the queue loop.
**Estimated effort:** ~4–5 focused sessions across the 6 phases (Function + Terraform are the heaviest).

## Open Risks & Assumptions

- **Extraction quality on faded Polish thermal receipts is unproven** — the roadmap's standing Unknown. Benchmarks favor GPT-4o, but real-receipt validation (Phase 5) is the decider; the `error` fallback and swappable `IChatClient` (Claude A/B) are the mitigations.
- **Consumption cold starts** (8–12 s) delay the first extraction after idle — acceptable because processing is async and the list shows "w trakcie".
- **Pre-GA package versions** for `Microsoft.Extensions.AI` / `Azure.AI.OpenAI` — pin exact versions at implement time.
- **S-02 coordination** — the list must render the new `error` status.

## Success Criteria (Summary)

- A confirmed receipt becomes `ready` with store/date/Polish tags and is findable by tag (unlocks S-04), with no manual input.
- Unreadable / filtered / retry-exhausted receipts land at `error` with photo + partial data retained — never stuck "pending".
- GIF is rejected at upload; the full loop runs locally on Azurite isolated from Azure.
