# AI Extraction & Enrichment (S-03) Implementation Plan

## Overview

When a receipt is confirmed it lands in the Azure AI Search index with `Status = "pending"` and empty enrichment fields. This change adds the asynchronous pipeline that fills those fields: the confirm endpoint enqueues the `receiptId` to an Azure Storage Queue, and an out-of-process Azure Function (Consumption) consumes it, calls a GPT-4o vision model behind `Microsoft.Extensions.AI`'s `IChatClient`, extracts `StoreName` / `PurchaseDate` / Polish-normalized `Tags`, merges them into the existing `ReceiptDocument`, and flips `Status` `pending → ready` (or `→ error` on unrecoverable failure). It also drops GIF support from the upload path entirely and extracts the shared `ReceiptDocument` model into a `ReceiptWell.Core` library so the API and the Function share one frozen schema.

## Current State Analysis

- **The data contract is pre-negotiated.** `src/backend/Models/ReceiptDocument.cs:28-35` already declares nullable `StoreName` (`string?`), `PurchaseDate` (`DateTimeOffset?`), and `Tags` (`IList<string>`). S-01 froze this schema for S-03 — no schema migration is needed; `Status = "error"` is just a new field *value*, not a new field.
- **The write-back mechanism already exists.** `src/backend/Services/ReceiptConfirmService.cs:104` writes the pending document via `searchClient.MergeOrUploadDocumentsAsync`. The Function reuses the identical call to merge the enrichment fields and the new status — `MergeOrUpload` is idempotent, which underpins the redelivery and retry decisions below.
- **The confirm endpoint emits nothing.** `src/backend/Program.cs:127-164` writes the pending doc synchronously and returns. There is no queue, no event, no downstream trigger today — the entire producer side is net-new.
- **No async out-of-process worker exists.** The only async-worker precedent is the in-process `IHostedService` `SearchIndexInitializer` (`src/backend/Program.cs:68`). There is no Azure Functions project anywhere under `src/`.
- **API project lives flat at `src/backend/`.** `ReceiptWell.csproj` sits directly at `src/backend/ReceiptWell.csproj` with no project subdirectory. It must be moved to `src/backend/ReceiptWell.Web/` before Core and Functions siblings are added, to avoid cross-sibling path churn. `Directory.Build.props` and `Directory.Packages.props` remain at `src/backend/` and propagate to all children via MSBuild directory traversal.
- **The index is built from the model by the API.** `SearchIndexInitializer` calls `new FieldBuilder().Build(typeof(ReceiptDocument))`. After the model moves to `ReceiptWell.Core`, the API keeps ownership of index creation; the Function only reads/merges.
- **Auth posture is mixed.** Blob + Key Vault use managed identity (`Program.cs:55,24`); Azure Search uses an API key (`Program.cs:59`, `infra/search.tf:16` `local_authentication_enabled = true`). The storage clients use the empty-connection-string → MI switch (`Program.cs:52`).
- **Not provisioned (must be scoped here per `lessons.md`):** no `azurerm_cognitive_account` (Azure OpenAI), no Storage Queue, no Function App in `infra/`. `infra/role_assignments.tf` grants the API's MI only `Key Vault Secrets User` and `Storage Blob Data Contributor`.
- **GIF is currently accepted.** Backend allowlist `ReceiptConfirmService.cs:25` and magic-bytes `:141` include `image/gif`; frontend `upload.component.ts:40,53` and `upload.component.html:49,67` advertise and accept `.gif`.
- **Conventions:** Central Package Management (`Directory.Packages.props`, lock files), source-generated `[LoggerMessage]` contextual logging, zero-warning build, config strictly from `IConfiguration`, no resource names/secrets in git-tracked files.

## Desired End State

A receipt uploaded and confirmed becomes fully searchable without any manual input:

1. Confirm writes the pending doc, then enqueues `receiptId`. If the enqueue fails, confirm returns 500 so the client retries (no silently-stuck receipts).
2. The Function dequeues, loads the doc, downloads the blob, calls GPT-4o, and within seconds-to-minutes the receipt shows `StoreName`, `PurchaseDate`, and Polish `Tags` with `Status = "ready"` — visible on the S-02 list.
3. An unreadable receipt, a content-filter block, or retries exhausted leaves the receipt at `Status = "error"` with its photo and any partial fields intact — never perpetually "pending".
4. GIF files are rejected at upload (client) and at confirm (server). Allowlist is PNG / JPEG / WEBP.
5. Local development runs the whole loop against Azurite + `func start` with zero coupling to the Azure account.

**Verification:** confirm a real receipt locally (Azurite + `func start` + a provisioned Azure OpenAI endpoint via ApiKey), watch `Status` flip to `ready` with populated fields; force a garbage image and watch it land at `error`; attempt a `.gif` upload and see it rejected client-side and (if bypassed) server-side.

### Key Discoveries:

- `ReceiptDocument.cs:28-35` — enrichment fields already exist and are nullable; S-03 only fills them.
- `ReceiptConfirmService.cs:91-104` — the exact `MergeOrUpload` pattern the Function reuses; idempotent.
- `Program.cs:52` — the empty-connection-string → `DefaultAzureCredential` switch the queue producer and OpenAI client mirror.
- `SearchIndexInitializer.cs` — index built from `typeof(ReceiptDocument)`; stays with the API after the model moves to Core.
- `infra/role_assignments.tf:5-19` — system-assigned MI + role-grant pattern to copy for the Function's identity.
- `vision-model-research.md:89-141` — verified `Microsoft.Extensions.AI` `IChatClient` wiring for GPT-4o vision + typed structured output (`GetResponseAsync<T>`).

## What We're NOT Doing

- **No Event Grid / System Topic** — Queue trigger was chosen over Event Grid (research.md DECISION 2026-06-14) to drop System Topic infra and keep a working local loop.
- **No in-process `IHostedService` worker** — D1 has no always-on; an in-process worker stalls when the app idles.
- **No automated test project** — validation is manual on 5–10 real Polish receipts (user decision). This slice does not stand up `ReceiptWell.Tests`.
- **No schema migration** and **no new index fields** — `Status = "error"` is a value only.
- **No price / product-name persistence** — FR-003 names price and product name, but the frozen index schema stores only `StoreName` / `PurchaseDate` / `Tags`; product descriptors surface as tags. The roadmap S-03 outcome ("sklep, datę, tagi") is authoritative.
- **No fixed tag taxonomy** — PRD requires unbounded tags; normalization is prompt + light post-processing only.
- **No animated-GIF frame inspection** — moot, GIF is removed from the allowlist entirely.
- **No change to Azure Search auth** — Search stays on its API key; only Azure OpenAI uses the ApiKey-or-MI switch.
- **No manual-retry UI for `error` receipts** — the `error` state is rendered by S-02; a user-triggered reprocess is out of scope for S-03.
- **No custom (loosened) Azure OpenAI content-filter policy** — the deployment keeps the default filter; loosening needs Microsoft Limited Access approval and the `error` fallback already covers blocks. Deferred to post-MVP (see Critical Implementation Details → Content filter).

## Implementation Approach

Six phases, each independently buildable/verifiable. Azure OpenAI is provisioned **ahead of** the Function code so the Function's local validation has a real GPT-4o endpoint to call (via ApiKey):

1. **Foundation** — carve out `ReceiptWell.Core` (shared `ReceiptDocument` + a `.sln`) and remove GIF, so both later deployables compile against one schema and the upload contract is correct before the pipeline exists.
2. **Producer** — the API enqueues `receiptId` after the pending-doc write, failing the confirm on enqueue error.
3. **Azure OpenAI provisioning** — Terraform for the Azure OpenAI account + GPT-4o deployment + custom content-filter policy, so a reachable endpoint exists before the Function is built.
4. **Consumer** — the Function project: queue trigger → GPT-4o extraction → normalize → merge → status transition, fully runnable locally on Azurite + the provisioned Azure OpenAI (ApiKey).
5. **Remaining infrastructure** — Terraform for the Storage Queue, the Function App, identities, role assignments, and app-settings wiring for both deployables.
6. **Delivery** — a second CI/CD workflow for the Function, then end-to-end manual validation on real receipts.

Local/Azure isolation is achieved by the **Azurite emulator** (a physically separate endpoint), not by distinct queue names: the same queue name is used in both environments, and the connection is selected by config exactly as the existing blob client already does.

## Critical Implementation Details

- **Timing & lifecycle (producer ordering).** The enqueue must sit in a specific window: **after the target-receipts-blob copy (`:88`) and the pending-doc write (`:104`), but before the staging-blob delete (`:114`).** Two distinct ordering constraints justify this:
  - *Enqueue after the blob copy + doc write* — the Function reads the **target receipts blob** (`BlobUrl`) and the search doc by `receiptId`; both must exist before a message can be processed, or the Function dequeues and finds nothing to merge into. (Note: the Function never touches the *staging* blob.)
  - *Enqueue before the staging-blob delete* — on enqueue failure the endpoint returns 500 and the client retries by re-calling confirm with the same `stagingBlobName`. That retry only works if the staging blob still exists; if it was already deleted, confirm fails validation with a 400 ("staging blob not found"), which is non-retriable (`lessons.md`) and leaves the receipt silently stuck `pending` — the exact failure Desired End State #1 forbids.

  On enqueue failure the doc is already `pending` and the staging blob is intact; returning 500 lets the idempotent confirm retry re-copy, re-write (`MergeOrUpload`), and re-enqueue.
- **Idempotency / redelivery.** Azure Queue is at-least-once. The Function loads the doc by `receiptId` and **short-circuits if `Status == "ready"`** to avoid a duplicate LLM call; otherwise it recomputes — `MergeOrUpload` makes a re-run safe.
- **Queue lease — do not look for a "lease extension" setting; there isn't one for Storage Queues.** The Functions Storage-queue trigger holds a message invisible for ~**10 minutes** while the function runs and, unlike the Service Bus trigger, has **no automatic lock renewal** (Service Bus has `maxAutoRenewDuration`; Storage Queues do not). A single-image GPT-4o extraction is seconds-to-tens-of-seconds — far under 10 min — so the default is ample; the rare redelivery (e.g. host crash mid-run) is neutralized by the `ready` short-circuit + idempotent merge above. Note that `host.json` → `extensions.queues.visibilityTimeout` (default `00:00:00`) is **not** the processing lease — it is the delay before a *failed* message is retried. The knob that matters is **`maxDequeueCount` (default 5)**, which drives the poison → `Status = "error"` path. If extraction ever genuinely needed >10 min (it won't at MVP), the real fix is a Service Bus trigger or Durable Functions, not a queue setting.
- **Failure classification.** Distinguish transient (network/5xx/throttling → let the queue retry) from terminal (content-filter block, unreadable image, malformed model output → set `Status = "error"` immediately, do not throw, so the message is not retried). Only retry-exhaustion (poison) and explicit terminal failures reach `error`.
- **Function host model.** .NET 9 on Azure Functions requires the **isolated worker** model (in-process is .NET 8 max). The project targets `Microsoft.Azure.Functions.Worker`.
- **Content filter.** Azure OpenAI can silently block receipts with certain product categories (alcohol, tobacco, pharmacy, etc. — `infrastructure.md` risk). For MVP the deployment keeps Azure OpenAI's **default** content filter; a block surfaces as a terminal `error` (handled by the failure-classification path), not a hang. A **custom** content-filter policy that loosens the default would reduce false `error`s, but lowering filter thresholds requires Microsoft's Limited Access ("modified content filters") approval — out of scope for this slice, deferred to post-MVP once Phase 6 reveals the real block frequency.

## Phase 1: Shared Core Library + Drop GIF

### Overview

Extract the shared index model into `ReceiptWell.Core`, introduce a solution file, and remove GIF from both the backend and frontend upload contracts. The phase opens with moving the API project into its own subdirectory (`ReceiptWell.Web`) — a gated step that must produce a clean `dotnet build` before anything else starts.

### Changes Required:

#### 1. Move and rename the API project to ReceiptWell.Web

**File**: `src/backend/ReceiptWell.Web/ReceiptWell.Web.csproj` (moved from `src/backend/ReceiptWell.csproj`)

**Intent**: Move the API project into a dedicated subdirectory and rename it to `ReceiptWell.Web`, so `src/backend/` becomes a clean parent that holds all project siblings (Core, Web, Functions) at the same level. Changes 2–5 only begin after `dotnet build` passes at the new path.

**Contract**: Use `git mv` to preserve file history. The following files remain at `src/backend/` and are NOT moved: `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `.gitignore`, `README.md`. These propagate to child projects via MSBuild directory traversal — nothing to copy. `UserSecretsId` (`receipt-well-api`) stays in the renamed `.csproj` unchanged — no user-secrets migration. `RootNamespace` (`ReceiptWell`) is inherited from `Directory.Build.props` — no namespace changes. Also update `.github/workflows/backend-deploy.yml` lines 25–28: replace `src/backend/ReceiptWell.csproj` with `src/backend/ReceiptWell.Web/ReceiptWell.Web.csproj`.

**Scaffolding** — run from `src/backend/`:

```bash
mkdir ReceiptWell.Web
git mv ReceiptWell.csproj ReceiptWell.Web/ReceiptWell.Web.csproj
git mv Program.cs ReceiptWell.Web/
git mv appsettings.json ReceiptWell.Web/
git mv appsettings.Development.json ReceiptWell.Web/
git mv Services ReceiptWell.Web/
git mv Models ReceiptWell.Web/
dotnet build ReceiptWell.Web/ReceiptWell.Web.csproj   # gate: must pass before continuing
```

#### 2. New shared class library

**File**: `src/backend/ReceiptWell.Core/ReceiptWell.Core.csproj` (new), `src/backend/ReceiptWell.Core/ReceiptDocument.cs` (moved from `src/backend/ReceiptWell.Web/Models/`)

**Intent**: Create a minimal class library holding `ReceiptDocument` (and any status constants) so the API and the Function depend on one definition of the frozen schema. Move `ReceiptDocument` out of `src/backend/ReceiptWell.Web/Models/`.

**Contract**: `ReceiptWell.Core` targets `net9.0`, references `Azure.Search.Documents` (for the `[SimpleField]`/`[SearchableField]` attributes), participates in Central Package Management. Namespace remains `ReceiptWell.Models` (or `ReceiptWell.Core`) — pick one and update both consumers. Introduce a `ReceiptStatus` constants holder (`Pending`/`Ready`/`Error`) used by both the API and the Function instead of string literals.

**Scaffolding** — create the project with the `dotnet` CLI, do not hand-author the `.csproj`:

```bash
cd src/backend
dotnet new classlib -n ReceiptWell.Core -o ReceiptWell.Core -f net9.0
rm ReceiptWell.Core/Class1.cs                      # drop the template type
git mv ReceiptWell.Web/Models/ReceiptDocument.cs ReceiptWell.Core/ReceiptDocument.cs
dotnet add ReceiptWell.Core/ReceiptWell.Core.csproj package Azure.Search.Documents
# CPM: strip the Version attribute the CLI injects into the .csproj — the
# version already lives in Directory.Packages.props. Then refresh the lock file.
dotnet restore
```

#### 3. API references Core; solution file

**File**: `src/backend/ReceiptWell.Web/ReceiptWell.Web.csproj`, `src/backend/ReceiptWell.sln` (new)

**Intent**: Reference `ReceiptWell.Core` from the API and add a `.sln` tying the projects together for local builds and CI.

**Contract**: `<ProjectReference Include="../ReceiptWell.Core/ReceiptWell.Core.csproj" />`; `SearchIndexInitializer` and `ReceiptConfirmService` use the relocated type. Index ownership stays in the API (`FieldBuilder().Build(typeof(ReceiptDocument))`).

**Scaffolding** — create the solution and wire references via the `dotnet` CLI:

```bash
cd src/backend
dotnet new sln -n ReceiptWell
dotnet sln ReceiptWell.sln add ReceiptWell.Web/ReceiptWell.Web.csproj ReceiptWell.Core/ReceiptWell.Core.csproj
dotnet add ReceiptWell.Web/ReceiptWell.Web.csproj reference ReceiptWell.Core/ReceiptWell.Core.csproj
dotnet build ReceiptWell.sln          # confirm zero-warning build after the model move
```

#### 4. Remove GIF from backend allowlist

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs`

**Intent**: Reject GIF server-side. Remove `image/gif` from `AllowedContentTypes` and delete the `image/gif` branch in `MatchesMagicBytes`.

**Contract**: `AllowedContentTypes` = `{ image/png, image/jpeg, image/webp }`. The magic-bytes switch loses its `image/gif` case (returns the `_ => false` default for GIF).

#### 5. Remove GIF from frontend upload

**File**: `src/frontend/src/app/receipts/upload/upload.component.ts`, `upload.component.html`

**Intent**: Stop advertising and accepting GIF in the picker.

**Contract**: `ALLOWED_TYPES` drops `image/gif` (`upload.component.ts:40`); the validation message drops "GIF" (`:53`); `accept=".png,.jpg,.jpeg,.webp"` (`upload.component.html:67`); subtitle reads `PNG · JPEG · WEBP · Max 20 MB` (`:49`).

#### 6. Update backend README

**File**: `src/backend/README.md`

**Intent**: After the solution builds cleanly, rewrite the README so the commands section reflects the new multi-project layout. The current README references bare `dotnet build` (no path) and a `dotnet test` line for a test project that does not exist.

**Contract**: The README stays at `src/backend/` and is written after Change 3 (solution file) succeeds. Replace the Commands section with:

- `dotnet build ReceiptWell.sln` — build the whole solution
- `dotnet run --project ReceiptWell.Web/ReceiptWell.Web.csproj -lp "https"` — run the API (`http://localhost:5191` / `https://localhost:7028`)
- `dotnet restore ReceiptWell.sln` — restore packages and refresh lock files

Add a brief Projects section listing:
- `ReceiptWell.Web/` — ASP.NET Core 9.0 minimal API
- `ReceiptWell.Core/` — shared model library (`ReceiptDocument`, `ReceiptStatus`)

Drop the `dotnet test` line — no test project exists yet.

### Success Criteria:

#### Automated Verification:

- Move/rename gate: `dotnet build src/backend/ReceiptWell.Web/ReceiptWell.Web.csproj` passes with zero warnings (prerequisite to changes 2–5)
- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- NuGet lock files refreshed: `dotnet restore src/backend/ReceiptWell.sln`
- Frontend builds: `npm --prefix src/frontend run build`
- Frontend unit tests pass: `npm --prefix src/frontend test`

#### Manual Verification:

- Existing receipt upload/confirm flow still works end-to-end (no regression from the model move).
- A `.gif` file is rejected by the file picker with the updated message.
- `src/backend/README.md` commands match the new solution structure and `dotnet run` starts the API correctly.

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation before proceeding.

---

## Phase 2: Queue Producer in the API

### Overview

After the pending document is written, the confirm endpoint enqueues the `receiptId`; an enqueue failure fails the confirm with 500.

### Changes Required:

#### 1. Queue client registration

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Register a `QueueClient` (or `QueueServiceClient`) using the same empty-connection-string → `DefaultAzureCredential` switch already used for blobs.

**Contract**: New config keys `AzureStorage:QueueServiceUri` and `AzureStorage:ExtractionQueueName`; reuse `AzureStorage:ConnectionString`. When the connection string is set (Azurite locally) use it; when empty (Azure) construct the queue client from the queue service URI + `DefaultAzureCredential`. Ensure the queue exists at startup (`CreateIfNotExists`).

#### 2. Enqueue on confirm

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs`

**Intent**: After `MergeOrUploadDocumentsAsync` succeeds, enqueue the `receiptId`. If the enqueue throws, log Error and propagate so the endpoint returns 500.

**Contract**: Message body is the bare `receiptId` string (the consumer loads everything else from the index). Enqueue happens after the pending-doc write (`:104`) and before the staging-blob delete (`:114`) — see "Timing & lifecycle" above. A new `[LoggerMessage]` at Error level for enqueue failure (contextual: userId + receiptId). The `ReceiptConfirmResult.Success` path is unchanged on success; on enqueue failure the existing `catch (Exception)` in `Program.cs:159` returns `Results.Problem(statusCode: 500)`.

**Encoding (must match the consumer).** `Azure.Storage.Queues` v12 sends the body as raw UTF-8 by default, but the Functions Storage-queue trigger defaults to **base64** (`messageEncoding`). Left unaligned, every message fails to decode on the consumer → poison → receipts stuck pending. Align both ends: set `QueueClientOptions.MessageEncoding = QueueMessageEncoding.Base64` on the producer (matches the Functions default), and confirm the Function leaves `extensions.queues.messageEncoding` at its `base64` default (see Phase 4 #2). Pick this one direction and keep both sides consistent.

#### 3. Config defaults + example collection

**File**: `src/backend/ReceiptWell.Web/appsettings.json`, `src/backend/ReceiptWell.Web/appsettings.Development.json`, `receipt-well.http`

**Intent**: Add structural (empty/localhost) defaults for the new queue keys and keep the request collection accurate.

**Contract**: `appsettings*.json` get empty `AzureStorage:QueueServiceUri` / `AzureStorage:ExtractionQueueName` defaults (no resource names). `receipt-well.http` confirm request is unchanged in contract; no new endpoint. Local queue name set via user-secrets/Development config pointing at Azurite.

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- `dotnet restore` succeeds (lock files updated for the new `Azure.Storage.Queues` package)

#### Manual Verification:

- With Azurite running, confirming a receipt places exactly one message (the `receiptId`) on the local queue (verify with Azure Storage Explorer / `az storage message peek` against Azurite).
- Simulating an enqueue failure (e.g. stop Azurite after the doc write) makes confirm return 500 and logs an Error; the pending doc exists and a retry re-enqueues.

**Implementation Note**: Pause for manual confirmation before proceeding.

---

## Phase 3: Azure OpenAI Provisioning

### Overview

Provision the Azure OpenAI account and a GPT-4o deployment ahead of the Function code, so Phase 4's local validation has a real endpoint to call via ApiKey. This is the OpenAI-only slice carved out of the broader infrastructure work; the queue, Function App, and role assignments land in Phase 5. The deployment uses Azure OpenAI's **default** content filter — a custom loosened policy is deferred (see Critical Implementation Details → Content filter; it needs Microsoft Limited Access approval and the `error` fallback already covers blocks).

### Changes Required:

#### 1. Azure OpenAI account + deployment

**File**: `infra/openai.tf` (new), `infra/variables.tf`, `infra/outputs.tf`

**Intent**: Provision a `Cognitive Services` account (kind `OpenAI`) in West Europe and a GPT-4o model deployment. The deployment keeps the default content filter for MVP (a custom loosened policy is deferred — see Overview / Critical Implementation Details).

**Contract**: `azurerm_cognitive_account` (kind `OpenAI`, West Europe), `azurerm_cognitive_deployment` for GPT-4o. Variables for account name / deployment name (no resource names committed outside `terraform.tfvars`). Output the endpoint for app-settings wiring (Phase 5) and for local-dev config. No custom content-filter policy in this slice.

#### 2. Local-dev access

**File**: (no code change — operational)

**Intent**: Make the provisioned endpoint usable from the local Function via the ApiKey path, keeping MI for Azure.

**Contract**: After apply, the developer sets `AzureOpenAI:Endpoint`, `AzureOpenAI:DeploymentName`, and `AzureOpenAI:ApiKey` in the Function's local settings / user-secrets (key never committed). In Azure the key is empty → MI (role assignment lands in Phase 5).

### Success Criteria:

#### Automated Verification:

- `terraform fmt -check` passes in `infra/`
- `terraform validate` passes in `infra/`
- `terraform plan` shows only the new Azure OpenAI account + deployment, no destructive changes to existing resources

#### Manual Verification:

- `terraform apply` provisions the account + GPT-4o deployment cleanly; the deployment is reachable with a key (smoke call returns a completion).
- The deployment uses the default content filter; no custom RAI policy is committed (deferred — see Critical Implementation Details → Content filter).
- No resource names or secrets were written into git-tracked `.tf` files (only `terraform.tfvars`, gitignored).

**Implementation Note**: Pause for manual confirmation before proceeding. This phase exists so Phase 4 can validate the extraction loop locally against a live model.

---

## Phase 4: Azure Functions Extraction Worker

### Overview

A new isolated-worker Function project consumes the queue, calls GPT-4o behind `IChatClient`, normalizes tags, merges results, and transitions status. It validates locally against Azurite + the Phase 3 Azure OpenAI endpoint (ApiKey).

### Changes Required:

#### 1. Function project scaffold

**File**: `src/backend/ReceiptWell.Functions/` (new project), added to `ReceiptWell.sln`

**Intent**: Create a .NET 9 isolated-worker Functions project that references `ReceiptWell.Core` and participates in Central Package Management.

**Contract**: `Microsoft.Azure.Functions.Worker` + `Microsoft.Azure.Functions.Worker.Sdk` host; `ProjectReference` to `ReceiptWell.Core`; `host.json`, `local.settings.json` (gitignored, `AzureWebJobsStorage = "UseDevelopmentStorage=true"` for Azurite). New packages (`Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `Azure.AI.OpenAI`, `Azure.Search.Documents`, `Azure.Storage.Blobs`) added without `Version`; versions go in `Directory.Packages.props`; verify exact versions at implement time (`vision-model-research.md:147`).

**Location**: the project lives at `src/backend/ReceiptWell.Functions/` — **inside** the `src/backend/` tree, not a `src/functions/` sibling — so it inherits the existing `Directory.Packages.props` (Central Package Management resolves via the nearest one walking up the tree) and joins the existing `ReceiptWell.sln`. A sibling at `src/functions/` would fall outside CPM's reach and fail to resolve versions.

**Scaffolding** — create the project **and the function** with the Azure Functions Core Tools (`func`), then wire it into the solution with the `dotnet` CLI. `func init` generates the worker-correct `host.json`, `local.settings.json`, `.gitignore`, and `Program.cs`; `func new` generates the queue-trigger stub (this replaces hand-writing `ExtractReceiptFunction.cs` in change #2 — that step becomes "flesh out the generated stub"):

```bash
cd src/backend
# 1. Project scaffold (creates src/backend/ReceiptWell.Functions/ with host.json,
#    local.settings.json, .gitignore, Program.cs).
func init ReceiptWell.Functions --worker-runtime dotnet-isolated --target-framework net9.0

# 2. The queue-trigger function stub (run from inside the project).
cd ReceiptWell.Functions
func new --name ExtractReceiptFunction --template "Queue trigger"
cd ..

# 3. Wire into the existing solution + Core reference (dotnet CLI).
dotnet sln ReceiptWell.sln add ReceiptWell.Functions/ReceiptWell.Functions.csproj
dotnet add ReceiptWell.Functions/ReceiptWell.Functions.csproj \
  reference ReceiptWell.Core/ReceiptWell.Core.csproj

# 4. Add the AI/Azure packages, then strip the Version attributes the CLI injects
#    (CPM) and set versions in Directory.Packages.props before restoring.
dotnet add ReceiptWell.Functions/ReceiptWell.Functions.csproj package Microsoft.Extensions.AI
dotnet add ReceiptWell.Functions/ReceiptWell.Functions.csproj package Microsoft.Extensions.AI.OpenAI
dotnet add ReceiptWell.Functions/ReceiptWell.Functions.csproj package Azure.AI.OpenAI
dotnet add ReceiptWell.Functions/ReceiptWell.Functions.csproj package Azure.Storage.Queues
dotnet restore
```

Notes: requires the Core Tools installed (`func --version`, v4.x). `func init`/`func new` leave the connection setting name as `AzureWebJobsStorage` on the generated `[QueueTrigger]` — keep it (Azurite locally, identity-based in Azure). `Azure.Search.Documents` comes transitively via the `ReceiptWell.Core` reference. Because the project sits under `src/backend/`, it inherits `Directory.Packages.props` automatically.

#### 2. Queue-triggered extraction function

**File**: `src/backend/ReceiptWell.Functions/ExtractReceiptFunction.cs` (the `func new` stub from change #1 — flesh it out, don't recreate)

**Intent**: Flesh out the generated `[QueueTrigger]` stub: load the doc by `receiptId`; short-circuit if already `ready`; download the receipt blob; call GPT-4o; normalize; `MergeOrUpload` fields + `Status = ready`. On terminal failure set `Status = error` and return without throwing; on transient failure throw to let the queue retry to its poison threshold. Point the trigger at the extraction queue name and apply the logging conventions (the template's default name/connection/logging are placeholders).

**Contract**: Trigger connection `AzureWebJobsStorage` (Azurite locally; identity-based `AzureWebJobsStorage__queueServiceUri` + MI in Azure). Queue name matches the producer's `ExtractionQueueName`. **Message encoding** stays at the Functions default `base64` (`host.json` → `extensions.queues.messageEncoding`); this must match the producer, which base64-encodes via `QueueMessageEncoding.Base64` (see Phase 2 #2) — mismatched encoding poisons every message. `host.json` sets `maxDequeueCount` (poison threshold, e.g. 5). When retries exhaust, the host moves the message to the `<ExtractionQueueName>-poison` queue and does **not** re-invoke the extraction function — so a second, dedicated **poison-queue-triggered function** (`[QueueTrigger("<ExtractionQueueName>-poison")]`) loads the receipt by `receiptId` and sets `Status = error`, guaranteeing no receipt is left `pending` after retry-exhaustion. This is the committed design (decoupled from `maxDequeueCount`, the standard Azure Functions poison pattern); the extraction function's transient path simply throws and lets the host handle retries. The poison handler needs the same queue-read role grant as the trigger (Phase 5 #3). Uses the shared `ReceiptStatus` constants. Contextual `[LoggerMessage]` logging (receiptId), per backend logging conventions.

#### 3. Extraction service behind IChatClient

**File**: `src/backend/ReceiptWell.Functions/ReceiptExtractionService.cs` (new), `Program.cs` (DI wiring)

**Intent**: Encapsulate the LLM call so the model stays swappable. Build an `IChatClient` over Azure OpenAI GPT-4o using the ApiKey-or-MI switch; send a system prompt + the image bytes; request typed structured output.

**Contract**: Config keys `AzureOpenAI:Endpoint`, `AzureOpenAI:DeploymentName`, `AzureOpenAI:ApiKey`. Auth switch mirrors storage: if `AzureOpenAI:ApiKey` is set → key credential (local dev); if empty → `AzureOpenAIClient(endpoint, DefaultAzureCredential()).GetChatClient(deployment).AsIChatClient()`. Typed result record maps to the **frozen schema types**, not the research sample — `StoreName (string?)`, `PurchaseDate (DateTimeOffset?)`, `Tags`. Use `GetResponseAsync<T>` for schema-enforced structured output; the system prompt instructs Polish-normalized tags and `null` for unreadable fields. **Date handling: use `DateOnly?` consistently through the extraction/domain layer** — the LLM DTO field is `DateOnly?` (a receipt date is a calendar date; this gives the model a natural target and avoids fabricated time/offset components). The **only** place it becomes `DateTimeOffset` is the index merge (Phase 4 #5): convert the parsed `DateOnly` to `DateTimeOffset` at **midnight UTC** when building the `ReceiptDocument`, because the frozen index schema requires `DateTimeOffset?` (Azure Search `Edm.DateTimeOffset`) and that type cannot change. Strict-mode JSON-schema keywords are unsupported — validate in C# after parse (`vision-model-research.md:130-137`).

> Wiring reference (verified, `vision-model-research.md:89-141`):
> ```csharp
> IChatClient chat = new AzureOpenAIClient(new Uri(endpoint), credential)
>     .GetChatClient(deploymentName).AsIChatClient();
> ```
> where `credential` is `AzureKeyCredential(apiKey)` when the key is set, else `DefaultAzureCredential()`.

#### 4. Tag normalization

**File**: `src/backend/ReceiptWell.Functions/TagNormalizer.cs` (new)

**Intent**: Guarantee search-safe tags regardless of model output: the prompt requests Polish; C# then trims, lowercases (invariant), removes empties, and deduplicates while preserving order.

**Contract**: `IReadOnlyList<string> Normalize(IEnumerable<string>? raw)` → trimmed, lowercased, distinct, empty-dropped. No count cap, no fixed vocabulary (PRD: unbounded tags). Mis-translations are accepted (validated manually on real receipts).

#### 5. Search merge + status transition

**File**: `src/backend/ReceiptWell.Functions/` (within the extraction function / a small store helper)

**Intent**: Reuse the index write path: `MergeOrUploadDocumentsAsync` with a `ReceiptDocument` carrying `Id`, the extracted fields, and the new `Status`.

**Contract**: Search via API key (`AzureSearch:ServiceUri` / `IndexName` / `ApiKey`), matching the API. Only `Id` + changed fields + `Status` are set on the merge document. This is the single boundary where the domain `DateOnly?` becomes the index's `DateTimeOffset?` — convert to midnight UTC here (see Phase 4 #3). Success → `ready`; terminal failure → `error` with whatever partial fields were parsed (possibly none).

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- `dotnet restore` succeeds with updated lock files
- Function host starts locally: `func start` in `src/backend/ReceiptWell.Functions` (against Azurite)

#### Manual Verification:

- End-to-end local loop: confirm a real receipt → message appears on Azurite queue → Function processes it → index doc shows `StoreName`/`PurchaseDate`/`Tags` and `Status = ready`.
- A deliberately unreadable image lands at `Status = error` (no throw loop, no perpetual pending).
- A redelivered message (manually re-enqueued) on an already-`ready` receipt is short-circuited (no second LLM call).
- Retry-exhaustion: a message that throws repeatedly past `maxDequeueCount` lands on the `-poison` queue and the poison-queue handler sets `Status = error` (no perpetual pending).
- Tags are lowercased, trimmed, deduplicated, and in Polish.

**Implementation Note**: Pause for manual confirmation before proceeding. Local validation calls the Azure OpenAI endpoint provisioned in Phase 3 via ApiKey; queue + Function App infra (Phase 5) is not yet required.

---

## Phase 5: Remaining Infrastructure (Terraform)

### Overview

Provision the Storage Queue, the Function App (Consumption) with its managed identity, role assignments (including `Cognitive Services OpenAI User` on the Phase 3 account), and app-settings wiring for both deployables. Azure OpenAI itself is already provisioned in Phase 3.

### Changes Required:

#### 1. Storage queue

**File**: `infra/storage.tf`

**Intent**: Add the extraction queue to the existing storage account.

**Contract**: `azurerm_storage_queue` (e.g. name from a new `extraction_queue_name` variable) on `azurerm_storage_account.main`. Same account as blobs — keeps one identity surface.

#### 2. Function App + identity

**File**: `infra/functions.tf` (new), `infra/variables.tf`

**Intent**: Provision a Consumption Function App (Windows, .NET 9 isolated) with a system-assigned identity, wired to the storage account for `AzureWebJobsStorage` via identity-based connection.

**Contract**: `azurerm_service_plan` (Consumption, Y1) or reuse pattern; `azurerm_windows_function_app` with `identity { type = "SystemAssigned" }`, runtime dotnet-isolated v9. `AzureWebJobsStorage__queueServiceUri` / `__blobServiceUri` identity-based settings (no connection string). App settings: `AzureOpenAI__Endpoint`, `AzureOpenAI__DeploymentName`, `AzureOpenAI__ApiKey` (empty → MI), `AzureSearch__ServiceUri`/`IndexName`/`ApiKey`, `AzureStorage__*` for blob reads, `ExtractionQueueName`.

#### 3. Role assignments for the Function identity

**File**: `infra/role_assignments.tf`

**Intent**: Grant the Function's MI the access it needs, mirroring the existing API MI grants.

**Contract**: Grant the Function MI, all on `azurerm_storage_account.main` unless noted, with `principal_type = "ServicePrincipal"` (pattern from `role_assignments.tf:13`):

- **`Storage Blob Data Owner`** — required by the Functions host for the identity-based `AzureWebJobsStorage` connection (host state, leases, blob-trigger receipts). `Reader` is insufficient for the host; Owner is the documented requirement for identity-based `AzureWebJobsStorage`.
- **`Storage Queue Data Contributor`** — required by the host/trigger for the identity-based `AzureWebJobsStorage` queue operations, and it also covers dequeue for the extraction + poison triggers (so a separate `Storage Queue Data Message Processor` grant is not needed once this is in place).
- On the **Azure OpenAI** account — **`Cognitive Services OpenAI User`**.

Search stays key-based (no role). Note the deployment-storage arrangement: this Function uses identity-based `AzureWebJobsStorage` (`__blobServiceUri`/`__queueServiceUri`, Phase 5 #2) rather than a connection string, so the roles above — not `AzureWebJobsStorage` secrets — are what the host relies on; confirm the package-deploy path (`WEBSITE_RUN_FROM_PACKAGE`) is consistent with that on the Consumption plan.

#### 4. API app-settings for the queue producer

**File**: `infra/app_service.tf`

**Intent**: Wire the API to enqueue in Azure via MI.

**Contract**: Add `AzureStorage__QueueServiceUri = "https://${var.storage_account_name}.queue.core.windows.net"` and `AzureStorage__ExtractionQueueName`. Grant the API's existing MI `Storage Queue Data Message Sender` on the storage account (new role assignment in `role_assignments.tf`).

### Success Criteria:

#### Automated Verification:

- `terraform fmt -check` passes in `infra/`
- `terraform validate` passes in `infra/`
- `terraform plan` shows only the intended new resources (queue, Function App, role assignments, app settings) with no destructive changes to existing resources

#### Manual Verification:

- `terraform apply` provisions cleanly; the queue exists and the Function App is up.
- The Function App and API managed identities show the expected role assignments in the portal (Function: `Cognitive Services OpenAI User`, queue/blob roles; API: `Storage Queue Data Message Sender`).
- No resource names, account names, or secrets were written into git-tracked `.tf` files (only into `terraform.tfvars`, which is gitignored).

**Implementation Note**: Pause for manual confirmation before proceeding.

---

## Phase 6: CI/CD + End-to-End Validation

### Overview

Add a path-filtered GitHub Actions workflow to build and deploy the Function via OIDC, then validate the full pipeline on real receipts.

### Changes Required:

#### 1. Function deploy workflow

**File**: `.github/workflows/functions-deploy.yml` (new)

**Intent**: On push to `develop` touching the Function or Core, build/publish the isolated-worker Function and deploy it to the Function App via OIDC — mirroring `backend-deploy.yml`.

**Contract**: `paths: ['src/backend/ReceiptWell.Functions/**', 'src/backend/ReceiptWell.Core/**', '.github/workflows/functions-deploy.yml']`; `permissions: id-token: write`; `azure/login@v2` (OIDC, existing secrets); `dotnet publish src/backend/ReceiptWell.Functions/ReceiptWell.Functions.csproj`; `Azure/functions-action@v1` targeting the Function App. The Function App name and resource group are read from **GitHub Actions secrets/vars** (e.g. `vars.FUNCTION_APP_NAME`, `vars.AZURE_RESOURCE_GROUP`), **not hardcoded** — git-tracked workflows must not carry hosting infra names per the project security guardrails. (Note: the existing `backend-deploy.yml` hardcodes `app-name`/`resource-group-name`/hostname; that is a pre-existing guardrail gap to retrofit separately, not to copy here.) Smoke step optional (Functions have no public health route by default).

#### 2. Backend workflow path filter

**File**: `.github/workflows/backend-deploy.yml`

**Intent**: Stop the API workflow from redeploying on Function-only changes, now that the Function lives under `src/backend/`. The existing `src/backend/**` glob already covers Core, so a shared-schema change still rebuilds the API — only the Function subtree needs excluding.

**Contract**: Add a negative path filter so the API workflow ignores the Function subtree:

```yaml
paths:
  - 'src/backend/**'
  - '!src/backend/ReceiptWell.Functions/**'
  - '.github/workflows/backend-deploy.yml'
```

A Core change (`src/backend/ReceiptWell.Core/**`) still matches `src/backend/**` → both workflows run. A Function-only change runs the Function workflow only.

### Success Criteria:

#### Automated Verification:

- Function workflow YAML is valid (parses; `actionlint` if available)
- A push touching only `src/backend/ReceiptWell.Functions/**` triggers the Function workflow but NOT the backend workflow; a push touching only `src/backend/Program.cs` triggers the backend workflow but NOT the Function workflow; a push touching `src/backend/ReceiptWell.Core/**` triggers both
- Both workflows publish without build/restore errors in CI

#### Manual Verification:

- Deployed Function processes a receipt confirmed against the live API end-to-end (`pending → ready`).
- **Real-receipt validation (roadmap requirement):** run 5–10 real Polish receipts (including faded/low-quality) through the deployed pipeline; assess hallucination and tag usefulness. Record whether GPT-4o quality is sufficient or whether the Claude-on-Foundry A/B is worth running.
- A receipt that triggers the content filter or is unreadable lands at `Status = error`, visible on the S-02 list.

**Implementation Note**: This is the final phase — after validation, the slice is complete.

---

## Testing Strategy

No automated test project is added (user decision). Verification is build-time (zero-warning compile, `terraform validate/plan`) plus manual:

### Manual Testing Steps:

1. Local loop (Azurite + `func start` + provisioned Azure OpenAI via ApiKey): confirm a receipt, watch `pending → ready` with populated fields.
2. Failure path: confirm a garbage/unreadable image → `Status = error`, no retry loop, photo retained.
3. Redelivery: re-enqueue a message for an already-`ready` receipt → short-circuited, no second LLM call.
4. GIF rejection: attempt `.gif` at the picker (rejected) and, if bypassed, at confirm (400/invalid).
5. Enqueue-failure: stop Azurite after the doc write → confirm returns 500; retry succeeds.
6. Production real-receipt batch: 5–10 Polish receipts; record hallucination + tag usefulness.

## Performance Considerations

- **Consumption cold starts** (8–12 s for .NET, `infrastructure.md`) add latency to the first extraction after idle — acceptable because processing is async and the list communicates "w trakcie".
- **One LLM call per receipt** (native vision, no separate OCR pass) keeps the Function fast and cheap; the already-`ready` short-circuit avoids duplicate calls on redelivery.
- **One extra index read** per message (load-by-id) is negligible.

## Migration Notes

- No data migration. Existing `pending` receipts (uploaded before this slice) have no queued work; they remain `pending` until re-confirmed or backfilled. A one-off backfill (enqueue all `pending` ids) is optional and out of scope.
- Schema is unchanged; `Status = "error"` is a new value the S-02 list must render (coordinated with S-02).

## References

- Internal research: `context/changes/ai-extraction-and-enrichment/research.md`
- External research: `context/changes/ai-extraction-and-enrichment/vision-model-research.md`
- Write-back pattern: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:91-104` (post-Phase-1 path)
- MI/connection switch: `src/backend/ReceiptWell.Web/Program.cs:52-56` (post-Phase-1 path)
- Frozen schema: `src/backend/ReceiptWell.Core/ReceiptDocument.cs` (moved to Core in Phase 1)
- Role-assignment pattern: `infra/role_assignments.tf:5-19`
- Infra plan & risks: `context/foundation/infrastructure.md`
- Lessons (verify resources exist; 400 vs transient): `context/foundation/lessons.md:33-45`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Shared Core Library + Drop GIF

#### Automated

- [x] 1.0 Move/rename gate — `dotnet build src/backend/ReceiptWell.Web/ReceiptWell.Web.csproj` passes with zero warnings — d731c2d
- [x] 1.1 Solution builds with zero warnings (`dotnet build src/backend/ReceiptWell.sln`) — d731c2d
- [x] 1.2 NuGet lock files refreshed (`dotnet restore`) — d731c2d
- [x] 1.3 Frontend builds (`npm --prefix src/frontend run build`) — d731c2d
- [x] 1.4 Frontend unit tests pass (`npm --prefix src/frontend test`) — d731c2d

#### Manual

- [x] 1.5 Existing upload/confirm flow still works (no regression from model move) — d731c2d
- [x] 1.6 `.gif` rejected by the file picker with updated message — d731c2d
- [x] 1.7 `src/backend/README.md` commands match new solution structure; `dotnet run` starts the API — d731c2d

### Phase 2: Queue Producer in the API

#### Automated

- [x] 2.1 Solution builds with zero warnings — 91a9bf9
- [x] 2.2 `dotnet restore` succeeds (Azure.Storage.Queues lock files updated) — 91a9bf9

#### Manual

- [x] 2.3 Confirm places exactly one `receiptId` message on the Azurite queue — 91a9bf9
- [x] 2.4 Simulated enqueue failure makes confirm return 500; pending doc exists; retry re-enqueues — 91a9bf9

### Phase 3: Azure OpenAI Provisioning

#### Automated

- [x] 3.1 `terraform fmt -check` passes
- [x] 3.2 `terraform validate` passes
- [x] 3.3 `terraform plan` shows only the new OpenAI account + deployment, no destructive changes

#### Manual

- [x] 3.4 `terraform apply` provisions account + GPT-4o deployment; smoke call returns a completion
- [x] 3.5 Deployment uses default content filter (no custom policy committed)
- [x] 3.6 No resource names/secrets written to git-tracked `.tf` files

### Phase 4: Azure Functions Extraction Worker

#### Automated

- [ ] 4.1 Solution builds with zero warnings
- [ ] 4.2 `dotnet restore` succeeds with updated lock files
- [ ] 4.3 Function host starts locally (`func start` against Azurite)

#### Manual

- [ ] 4.4 End-to-end local loop fills fields and flips `Status = ready`
- [ ] 4.5 Unreadable image lands at `Status = error` (no throw loop, no perpetual pending)
- [ ] 4.6 Redelivered message on an already-`ready` receipt is short-circuited
- [ ] 4.7 Tags are lowercased, trimmed, deduplicated, Polish
- [ ] 4.8 Retry-exhaustion routes to `-poison` queue; poison handler sets `Status = error`

### Phase 5: Remaining Infrastructure (Terraform)

#### Automated

- [ ] 5.1 `terraform fmt -check` passes
- [ ] 5.2 `terraform validate` passes
- [ ] 5.3 `terraform plan` shows only intended additions (queue, Function App, roles, app settings), no destructive changes

#### Manual

- [ ] 5.4 `terraform apply` provisions queue + Function App cleanly
- [ ] 5.5 Function App and API identities show expected role assignments
- [ ] 5.6 No resource names/secrets written to git-tracked `.tf` files

### Phase 6: CI/CD + End-to-End Validation

#### Automated

- [ ] 6.1 Function workflow YAML valid
- [ ] 6.2 Path filters trigger correctly (functions push triggers; backend-only push does not)
- [ ] 6.3 Both workflows publish without build/restore errors in CI

#### Manual

- [ ] 6.4 Deployed Function processes a receipt end-to-end (`pending → ready`)
- [ ] 6.5 Real-receipt batch (5–10 Polish receipts) assessed for hallucination + tag usefulness
- [ ] 6.6 Content-filter/unreadable receipt lands at `Status = error`, visible on S-02 list
