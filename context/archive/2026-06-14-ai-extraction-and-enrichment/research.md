---
date: 2026-06-14T12:45:43Z
researcher: Krzysztof Dudzik
git_commit: e6d74a1e540924272b2720ba95a7366d20631b37
branch: develop
repository: receipt-well
topic: "Is vision-model-research.md compatible with the existing codebase?"
tags: [research, codebase, ai-extraction, azure-openai, azure-search, background-jobs]
status: complete
last_updated: 2026-06-14
last_updated_by: Krzysztof Dudzik
---

# Research: Is `vision-model-research.md` compatible with the existing codebase?

**Date**: 2026-06-14T12:45:43Z
**Researcher**: Krzysztof Dudzik
**Git Commit**: e6d74a1e540924272b2720ba95a7366d20631b37
**Branch**: develop
**Repository**: receipt-well

## Research Question

Review the codebase for S-03 (`ai-extraction-and-enrichment`) and verify whether
`context/changes/ai-extraction-and-enrichment/vision-model-research.md` is
compatible with it.

## Summary

**Verdict: mostly compatible on the model and data-write story, but the research
doc makes two architectural assumptions the codebase contradicts, and omits three
project conventions that are load-bearing.** None are fatal — they are corrections
the plan must absorb before implementation.

Compatible (confirmed by code):
- **GPT-4o is already an implicit project decision.** S-01 deliberately set the
  upload allowlist (PNG/JPEG/WEBP/GIF, ≤ 20 MB) *because* "GPT image input accepts
  only" those types (`receipt-upload-confirm/plan.md:271`). The research doc's
  GPT-4o-first recommendation ratifies a constraint already shipped in code.
- **Keyless / managed-identity access** for Azure OpenAI matches the existing blob
  pattern (`Program.cs:55`, `:24` use `DefaultAzureCredential`).
- **The enrichment output fields already exist** on `ReceiptDocument`
  (`StoreName`, `PurchaseDate`, `Tags`) — the index schema was frozen in S-01
  specifically so S-03 fills them with no migration (`ReceiptDocument.cs:28-35`,
  `receipt-upload-confirm/plan.md:129,439`).

Confirmed / settled (user + `infrastructure.md`, 2026-06-14):
1. **No "Receipt entity" and no document DB — by design.** The system of record for
   receipt metadata is the **Azure AI Search index** itself; `ReceiptDocument` is the
   entity, updated via `SearchClient.MergeOrUploadDocumentsAsync`. The research doc's
   "Receipt entity" wording should read "`ReceiptDocument` in the Azure AI Search
   index, merged via MergeOrUpload". Mechanism settled, not open.

Needs correction / decision:
2. **No Azure Functions project or resource exists yet, and the trigger model is
   under review.** `infrastructure.md` planned **Azure Functions (Consumption) +
   Event Grid trigger** on blob upload. The backend today is a single ASP.NET Core
   minimal-API app whose only async-worker precedent is `IHostedService`
   (`SearchIndexInitializer`); nothing emits an event on confirm. User is weighing
   keeping Functions vs. an in-process `IHostedService` to avoid complicating the MVP
   — see Open Question #1 for the trade-off (the D1 "no always-on" constraint is the
   crux).
3. **Azure OpenAI is planned but not provisioned.** `infrastructure.md` names Azure
   OpenAI (West Europe) and a `Cognitive Services OpenAI User` MI role, but `infra/`
   has no `azurerm_cognitive_account`. Per `lessons.md` ("verify resources exist
   before calling them out-of-scope"), the plan must scope the Terraform: account +
   GPT-4o deployment + app-settings wiring + the role assignment. Also configure a
   **custom content-filter policy** — `infrastructure.md` flags Azure OpenAI may
   silently drop receipts with certain product categories.

Omitted conventions the plan must honor:
- **Central Package Management** — versions go in `Directory.Packages.props`, not
  the `.csproj`; NuGet lock files require `dotnet restore` after changes.
- **Schema type mismatch** — research sample uses `record ReceiptExtraction(string
  Store, DateOnly? Date, …)`; the index uses `StoreName (string?)`,
  `PurchaseDate (DateTimeOffset?)`, `Tags (IList<string>)`.
- **Source-generated, contextual logging** + zero-warning policy (backend CLAUDE.md).

## Detailed Findings

### Data model & write-back — Azure Search IS the database

- `ReceiptDocument` is an Azure AI Search index document, decorated with
  `[SimpleField]` / `[SearchableField]` (`src/backend/Models/ReceiptDocument.cs:5-36`).
  There is **no Cosmos/SQL/EF** layer — consistent with root `CLAUDE.md` ("No SQL
  database, no EF Core").
- The S-03 enrichment fields already exist and are nullable:
  `StoreName` (`:28-29`), `PurchaseDate` `DateTimeOffset?` (`:31-32`),
  `Tags` `IList<string>` (`:34-35`).
- S-01 already writes the document with `Status = "pending"` and the base fields
  via `SearchClient.MergeOrUploadDocumentsAsync`
  (`src/backend/Services/ReceiptConfirmService.cs:91-104`). S-03 re-uses the exact
  same call to merge `StoreName` / `PurchaseDate` / `Tags` and flip `Status` to
  `"ready"`. No schema migration needed (`receipt-upload-confirm/plan.md:439`).
- **Correction for the doc**: replace "Receipt entity" language with
  "`ReceiptDocument` in the Azure AI Search index, updated via MergeOrUpload".

### Async / trigger — the biggest gap

- **No Azure Functions project** anywhere under `src/` (the only `function`/`host.json`
  hits are in `src/frontend/node_modules`). Backend is one project: `ReceiptWell.csproj`
  (`Microsoft.NET.Sdk.Web`).
- The confirm endpoint writes the pending doc **synchronously** and returns
  (`Program.cs:127-164`); **nothing emits an event/queue message** for downstream
  processing. So S-03 has no trigger source today.
- Existing async-worker precedent is in-process `IHostedService`
  (`SearchIndexInitializer`, `Program.cs:68`).
- **Decisive constraint: the D1 Shared App Service has no always-on**
  (`infrastructure.md:11,118`). The web app idles after inactivity. An **in-process**
  background worker therefore only makes progress *while the app is awake* — a receipt
  uploaded right before idle stays `pending` until the next request wakes the app.
  An **out-of-process Function** (woken by Event Grid / Queue / Blob) is independent of
  the web app's idle state. This is why `infrastructure.md` chose Functions, and it is
  the main argument against a pure `IHostedService`.
- **DECISION (2026-06-14): Functions + Queue trigger** (option b below). Confirm
  endpoint enqueues `receiptId` to an Azure Storage Queue; a `[QueueTrigger]` Function
  (Consumption) downloads the blob, calls GPT-4o, and `MergeOrUpload`s `StoreName` /
  `PurchaseDate` / `Tags` while flipping `Status` `pending → ready`. Chosen over Event
  Grid to drop System Topic/subscription infra and keep a working local dev loop
  (Azurite + `func start`); chosen over in-process to survive D1 "no always-on".
- **Options considered:**
  - (a) **Functions + Event Grid** — as planned in `infrastructure.md`. Fires within
    seconds; survives app idle; but most infra (System Topic + subscription) and the
    hardest local-dev loop. Event-grid misconfig can silently drop events
    (`infrastructure.md:110`).
  - (b) **Functions + Queue trigger** (recommended MVP middle ground) — confirm
    endpoint enqueues `receiptId` to an Azure Storage Queue; `[QueueTrigger]` Function
    consumes it. Survives app idle, fires in seconds, built-in poison-queue retry, and
    trivially testable locally with Azurite — without Event Grid/System Topic wiring.
  - (c) **Functions + Blob trigger** — simplest Functions infra but up-to-10-min
    polling delay (the reason `infrastructure.md` rejected it).
  - (d) **In-process `IHostedService`** + index `Status` as durable work list +
    startup reconciliation sweep — single deployable, lowest friction, but breaks on
    "no always-on" (above) and puts AI CPU on the shared D1 tier.
  - `tech-stack.md` + `infrastructure.md` assume Functions; the existing code leans to
    (d). Resolve explicitly — it drives Terraform, CI/CD, and the deploy story.

### Model access & infrastructure

- **Not provisioned**: no `azurerm_cognitive_account` / Azure OpenAI in `infra/`
  (`infra/*.tf` = app_service, key_vault, main, oidc, outputs, resource_group,
  role_assignments, search, static_web_app, storage, variables). Plan must add it
  (account + model deployment + app-settings wiring + role assignment).
- **Managed identity**: App Service has a system-assigned identity with role
  assignments for `Key Vault Secrets User` and `Storage Blob Data Contributor`
  (`infra/role_assignments.tf:5-19`). Azure OpenAI via MI (research doc's keyless
  `AzureOpenAIClient(endpoint, DefaultAzureCredential())`) would need a new
  `Cognitive Services OpenAI User` role assignment on the same principal.
- **Mixed auth precedent**: Azure Search uses an **API key**, not MI
  (`Program.cs:59`, `infra/app_service.tf:53`, `infra/search.tf:16`
  `local_authentication_enabled = true`). So MI is the blob precedent, not universal;
  the research doc's MI choice is the better pattern but not the only one in the repo.
- App Service is a **Windows** web app (`azurerm_windows_web_app.api`) — relevant if
  option (a) Functions hosting is chosen.

### Package & code conventions (omitted by the research doc)

- **Central Package Management is ON** (`src/backend/Directory.Packages.props`,
  `ManagePackageVersionsCentrally=true`). New packages (`Microsoft.Extensions.AI`,
  `Microsoft.Extensions.AI.OpenAI`, `Azure.AI.OpenAI`) must be added **without** a
  `Version` in the `.csproj`; the version goes in `Directory.Packages.props`. Run
  `dotnet restore` to refresh the lock file (backend `CLAUDE.md`).
- Current package set has **no AI SDK yet** — all three packages from the research
  doc are net-new (`ReceiptWell.csproj:7-14`).
- Logging: source-generated `[LoggerMessage]`, contextual (log identity id + entity
  id), validation→Info, fallback→Warning, broken job→Error; zero-warning build.
  The research doc's sample code omits this.

### Config pattern (compatible)

- The research doc's "endpoint from config, not git-tracked" matches the project
  rule: structural keys only in `appsettings*.json`, real values via App Service app
  settings / user-secrets (`Program.cs:42-65` reads everything from `IConfiguration`;
  backend `CLAUDE.md`). S-03's Azure OpenAI endpoint/deployment name follow the same
  path (e.g. `AzureOpenAI:Endpoint`, `AzureOpenAI:DeploymentName`).

## Code References

- `src/backend/Models/ReceiptDocument.cs:28-35` — `StoreName`/`PurchaseDate`/`Tags`
  already declared; S-03 fills them
- `src/backend/Services/ReceiptConfirmService.cs:91-104` — pending doc write via
  `MergeOrUploadDocumentsAsync` (the write-back mechanism S-03 reuses)
- `src/backend/Program.cs:127-164` — confirm endpoint; synchronous, no event emitted
- `src/backend/Program.cs:55,24` — `DefaultAzureCredential` (MI) precedent
- `src/backend/Program.cs:59` — Azure Search via API key (not MI)
- `src/backend/Program.cs:68` — `IHostedService` async-worker precedent
- `src/backend/Directory.Packages.props` — Central Package Management; no AI SDK yet
- `infra/role_assignments.tf:5-19` — system-assigned MI + existing role grants
- `infra/search.tf:16` — `local_authentication_enabled = true` (Search key auth)
- `context/changes/receipt-upload-confirm/plan.md:271` — allowlist set by GPT image
  constraints; `:129,439` — index schema frozen for S-03; `:279` — animated-GIF
  rejection deferred to S-03

## Architecture Insights

- The product is built **Search-index-first**: the Azure AI Search document is both
  the queryable surface (S-04) and the system of record. S-03 is an *update* step on
  an existing document, not a create.
- S-01 was authored with S-03 in mind (frozen schema, GPT-driven allowlist), so the
  *data contract* for S-03 is effectively pre-negotiated. The genuinely open piece is
  **how extraction is triggered and hosted**, not what it writes.
- The repo's keyless-MI posture (blob, KV) makes Azure OpenAI-with-MI the natural
  choice, but Search's API-key exception shows the team will use keys when simpler.

## Historical Context (from prior changes)

- `context/changes/receipt-upload-confirm/plan.md` — froze the `ReceiptDocument`
  schema for S-01–S-04, defined `Status` lifecycle `pending → ready`, and tied the
  upload allowlist to the S-03 vision model. Strongest evidence the codebase already
  assumed a GPT-class vision model.
- `context/foundation/lessons.md:33-38` — "verify resources exist before calling them
  out-of-scope in a plan" — directly applicable: Azure OpenAI + any Functions
  resource are NOT provisioned and must be scoped explicitly.
- `context/foundation/lessons.md:40-45` — 400-vs-transient error handling — relevant
  to how the pipeline surfaces extraction failures (PRD fallback: partial data + photo).

## Related Research

- `context/changes/ai-extraction-and-enrichment/vision-model-research.md` — external
  research (model choice, SDK syntax) that this document validates against the codebase.
- `context/foundation/infrastructure.md` — platform/infra plan: Azure Functions
  (Consumption) + Event Grid trigger, Azure OpenAI (West Europe), D1 Shared (no
  always-on), MI roles incl. `Cognitive Services OpenAI User` / `Search Index Data
  Contributor`, Azure OpenAI content-filter caveat.

## Open Questions

1. ~~Trigger/hosting model for async extraction~~ — **DECIDED 2026-06-14:
   Functions + Queue trigger** (Azure Storage Queue enqueued by the confirm endpoint).
   Crux was D1's "no always-on", which rules out a pure in-process worker. Drives the
   Terraform (Function App + Storage Queue) and a second CI/CD deployable.
2. **Azure OpenAI provisioning** — which region (EU data residency for receipt PII),
   which GPT-4o deployment, and the `Cognitive Services OpenAI User` role assignment.
3. **MI vs API key for Azure OpenAI** — follow the blob/KV MI precedent, or the Search
   API-key exception?
4. **Animated-GIF rejection** — S-01 deferred frame inspection to S-03
   (`plan.md:279`); confirm S-03 owns it.
5. **Failure-path status** — PRD fallback keeps the receipt with partial data + photo;
   decide the `Status` value for failed extraction (e.g. `error` vs remain `pending`)
   and whether it's retriable.
