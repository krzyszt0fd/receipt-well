# Infra-boundary Failure Shape Tests (Phase 2 Rollout) — Implementation Plan

## Overview

Add integration tests that prove **Risk #7** from the test plan: a thrown Blob/Queue/Search dependency error surfaces a **clean, honest 5xx** (RFC 7807 Problem Details), and **never a silent success or a dishonest 200 that masks a failed side effect**. The one exception — the intentional fire-and-forget staging cleanup that returns 200 — is documented and pinned so a future regression that accidentally extends "swallow and 200" to a critical step is caught.

This is a **test-only change**. No production code is modified. All tests run offline through the existing `ReceiptWellWebFactory` (`WebApplicationFactory<Program>`) with NSubstitute substitutes for every Azure client. No network call is possible.

## Current State Analysis

- **Error translation is endpoint-scoped, not global.** All three receipt endpoints wrap their body in `try { … } catch (Exception ex) { return Results.Problem(statusCode: 500); }` (`Program.cs:140–215`). There is no `UseExceptionHandler`/`AddProblemDetails`. Phase 2 stays inside these lambdas, so the 500 contract holds for every tested path. `Results.Problem(statusCode: 500)` emits `application/problem+json` with `status: 500` and no exception detail.
- **`POST /receipts/confirm` is a non-atomic 4-step sequence** (`ReceiptConfirmService.cs:35–135`): (1) `SyncCopyFromUriAsync` copies the blob → (2) `MergeOrUploadDocumentsAsync` writes the search doc → (3) `SendMessageAsync` enqueues extraction → (4) `DeleteAsync` cleans up staging. Steps 2 and 3 `catch … log … throw` (→ 500). Step 4 `catch … log` with **no re-throw** (→ 200). Later failures cannot undo earlier commits — the side-effect commit boundary is the oracle.
- **Single-step endpoints carry no partial-state risk.** `POST /receipts/staging-slot` (`ReceiptBlobService.cs:16–48`) does one `UploadAsync`; `GET /receipts` (`ReceiptQueryService`) does one read-only `SearchAsync`. A thrown error from either propagates to the endpoint catch → 500.
- **The confirm flow is expensive to drive to a failing step.** Reaching Step 2/3 requires Step 0–1 to succeed: `GetPropertiesAsync` (allowed content type + non-empty content-disposition + length within limit), `DownloadContentAsync` (first bytes matching the declared type's magic bytes — PNG `89 50 4E 47`), SAS generation, and `SyncCopyFromUriAsync`. The chained-substitute setup + version-sensitive model factories are the real cost of this phase.
- **Harness facts** (`ReceiptWellWebFactory.cs`): `IClassFixture` → one factory per test class; substitute call history **accumulates across methods in a class**. Public substitute properties: `BlobServiceClient`, `SearchClient`, `SearchIndexClient`, `QueueClient`. Identity via `X-Test-Oid: <guid>` header. Existing reusable patterns: `Arg.Do`+`Returns` (`ReceiptQueryScopingTests.cs:26–32`), `DidNotReceiveWithAnyArgs` (`ReceiptConfirmOwnershipTests.cs:51–54`).
- **CI gate is already wired.** `dotnet test src/backend/ReceiptWell.sln` runs in `backend-deploy.yml:29`. New tests in `ReceiptWell.Tests` join the §5 "infra-boundary failure tests" gate automatically — no CI change needed.

## Desired End State

`dotnet test src/backend/ReceiptWell.sln` runs a new set of integration tests that, for each infra-boundary failure, assert **three things**: the HTTP status (500, or 200 for the fire-and-forget case), the **RFC 7807 body shape** (`application/problem+json`, `status: 500`, no leaked exception detail), and the **exact side-effect commit boundary** (which substituted clients did and did not receive calls). The test-plan §3 Phase 2 status reads `complete`, §6 cookbook documents the honest-5xx recipe, and the §5 CI gate is active.

### Key Discoveries:

- The confirm flow's SAS branch can be bypassed in tests by stubbing `CanGenerateSasUri → true` + `GenerateSasUri → Uri` on the staging blob substitute — this avoids the `DelegationTokenProvider`/`GetUserDelegationKeyAsync` base64-signing path entirely (`ReceiptConfirmService.cs:169`, `DelegationTokenProvider.cs:14`). The SAS branch is irrelevant plumbing for Risk #7.
- Azure return types (`BlobProperties`, `BlobDownloadResult`, `BlobCopyInfo`, `UserDelegationKey`, `IndexDocumentsResult`, `SendReceipt`) have no public constructors — build them with the version-sensitive `BlobsModelFactory` / `SearchModelFactory` / `QueuesModelFactory` overloads. Confirm argument lists against `Directory.Packages.props` if a model-factory call fails to compile (mirrors the Phase 1 `SearchModelFactory` lesson, §6.6).
- Exception injection uses `Azure.RequestFailedException` (from `Azure.Core`, transitive — no new package): `.Returns(Task.FromException<TResponse>(new RequestFailedException(500, "Simulated …")))`.
- The container-name → substitute mapping is config-coupled: `ReceiptConfirmService` reads `AzureStorage:StagingContainerName` / `AzureStorage:ReceiptsContainerName` from `IConfiguration`. The harness must key `GetBlobContainerClient(name)` returns off the same configured values (read them from `factory.Services`), or stub per distinct name.

## What We're NOT Doing

- **No production code changes** — not adding global exception middleware (`UseExceptionHandler`/`AddProblemDetails`); that future-hardening item is noted in research §1 but out of scope.
- **No Azure Functions tests** — `SetReadyAsync`/poison-handler silent-failure paths are Phase 4 (research §6; test-plan §3 Phase 4). The most severe gap found in research is recorded there, not here.
- **No staging-slot SAS/delegation-key partial-state test** (research OQ#3) — descoped by decision; the SAS branch is bypassed in the harness.
- **No logging assertions** — test-plan §7 excludes "whether logs physically land"; we assert failure *shape*, not log emission. (The endpoints log via `ILoggerFactory.CreateLogger(name)`, which is not cleanly interceptable anyway.)
- **No new CI wiring** — the existing `dotnet test` step already gates these tests.
- **Stryker mutation run is optional manual verification, not a phase or a CI gate.**

## Implementation Approach

Build the brittle, shared confirm-flow setup once as a test-infrastructure helper, prove it with a positive-control happy-path test, then layer the failure cases on top. Failure cases assert the full honest-5xx contract (status + RFC 7807 body + side-effect boundary). Single-step endpoint cases need no builder. Close with cookbook + test-plan sync.

## Critical Implementation Details

- **Per-class call-history accumulation.** The factory is `IClassFixture` (per class); substitutes are shared singletons whose received-calls accumulate across test methods. Every test method must call `ClearReceivedCalls()` on the substitutes it asserts against and re-configure behaviour at the top — otherwise a `DidNotReceive` assertion can read a prior method's call and a `Returns` from a prior method can bleed in. The `ConfirmFlowHarness` should encapsulate this clear-and-restub preamble.
- **Drive-to-step ordering for confirm.** To make Step N throw, Steps 0…N-1 must return success values (not the substitutes' default `null`/empty). Step 0 needs a `BlobProperties` whose `ContentType` is allowed (`image/png`), `ContentLength` is under 20 MB, and `ContentDisposition` is non-empty; the download stub must return bytes whose first four match the PNG magic number (`89 50 4E 47`). Get these right or the flow exits early as `InvalidBlob` (400) and the test asserts the wrong thing.

## Phase 1: Test infrastructure & positive control

### Overview

Create the shared `ConfirmFlowHarness` that drives the confirm chain to success up to a configurable step, an RFC 7807 assertion helper, and a happy-path confirm integration test that proves the harness wires the real flow correctly (the positive control — without it the failure tests could pass vacuously by never reaching the targeted step).

### Changes Required:

#### 1. Confirm-flow happy-path builder

**File**: `src/backend/ReceiptWell.Tests/Infrastructure/ConfirmFlowHarness.cs` (new)

**Intent**: Encapsulate the ~40 lines of brittle chained-substitute setup that makes a `POST /receipts/confirm` request succeed up to a chosen step, so each failure test declares only *which step throws* rather than repeating the chain. Also owns the `ClearReceivedCalls()` + re-stub preamble.

**Contract**: Given the factory and a caller `oid`, configures the factory's `BlobServiceClient`, `SearchClient`, and `QueueClient` substitutes so the confirm flow runs to success, with a `FailAt(ConfirmStep step)` switch that swaps the chosen step's call for a thrown `RequestFailedException`. `ConfirmStep` enum: `BlobCopy`, `SearchWrite`, `QueueSend`, `StagingDelete`, `Complete`. The chain to wire (staging container/blob → properties → download → SAS bypass → receipts container/blob → copy → search → queue → delete) and the model-factory builders are the non-obvious part:

```csharp
// SAS bypass — avoids DelegationTokenProvider / GetUserDelegationKeyAsync entirely:
stagingBlob.CanGenerateSasUri.Returns(true);
stagingBlob.GenerateSasUri(Arg.Any<BlobSasBuilder>())
    .Returns(new Uri("https://staging.localhost.test/blob?sig=test"));

// Step 0 — properties that pass validation (allowed type, size, disposition):
stagingBlob.GetPropertiesAsync().Returns(Response.FromValue(
    BlobsModelFactory.BlobProperties(
        contentType: "image/png", contentLength: 1024,
        contentDisposition: "attachment; filename=\"r.png\""),
    Substitute.For<Response>()));

// Step 0b — first bytes must match PNG magic (89 50 4E 47):
stagingBlob.DownloadContentAsync(Arg.Any<BlobDownloadOptions>()).Returns(Response.FromValue(
    BlobsModelFactory.BlobDownloadResult(content: new BinaryData(new byte[] {0x89,0x50,0x4E,0x47})),
    Substitute.For<Response>()));
```

Container-name → substitute mapping reads `AzureStorage:StagingContainerName` / `AzureStorage:ReceiptsContainerName` from `factory.Services.GetRequiredService<IConfiguration>()`. Success returns for copy/search/queue/delete use `BlobsModelFactory.BlobCopyInfo(...)`, `SearchModelFactory.IndexDocumentsResult(...)`, `QueuesModelFactory.SendReceipt(...)`, and a substituted `Response`. Failure injection follows research §7. `targetBlob.Uri` must return a valid `Uri` (read into `BlobUrl`).

#### 2. RFC 7807 Problem Details assertion helper

**File**: `src/backend/ReceiptWell.Tests/Infrastructure/ProblemDetailsAssertions.cs` (new)

**Intent**: One place asserting an `HttpResponseMessage` is a well-formed, honest Problem Details 5xx, so every failure test calls it instead of re-implementing body checks.

**Contract**: `static Task AssertHonest500Async(HttpResponseMessage response)` — asserts status `500`, content-type `application/problem+json`, body deserializes to Problem Details with `status == 500`, and the body carries **no** exception/stack-trace detail. Uses `System.Net.Http.Json` + `System.Text.Json` (already referenced).

#### 3. Positive-control happy-path confirm test

**File**: `src/backend/ReceiptWell.Tests/ReceiptConfirmFlowTests.cs` (new)

**Intent**: Prove the harness drives the real confirm flow to a genuine 200 with all side effects committed — the positive control that keeps the Phase 2 failure assertions from being vacuously true.

**Contract**: `IClassFixture<ReceiptWellWebFactory>`. Arrange `ConfirmFlowHarness.FailAt(Complete)`, POST a valid staging blob name owned by the caller (`X-Test-Oid: <oid>`, `stagingBlobName = "<oid>/<guid>"`), assert **200**, and assert all four side effects fired: `SyncCopyFromUriAsync`, `MergeOrUploadDocumentsAsync`, `SendMessageAsync`, `DeleteAsync` each `Received()`.

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- Positive-control test passes: `dotnet test src/backend/ReceiptWell.sln --filter "FullyQualifiedName~ReceiptConfirmFlowTests"`
- Full suite still green: `dotnet test src/backend/ReceiptWell.sln`

#### Manual Verification:

- The harness reaches a real 200 (not a 400 `InvalidBlob` early-exit) — confirms content-type/magic-byte/disposition stubs are correct.
- Builder reads container names from config rather than hardcoding them.

**Implementation Note**: After completing this phase and all automated verification passes, pause for manual confirmation before proceeding.

---

## Phase 2: Failure-shape tests (all endpoints)

### Overview

The Risk #7 core. Confirm-endpoint multi-step cases (Steps 1–4) assert the precise side-effect commit boundary; single-step staging-slot and GET cases complete the coverage surface. Every 500 case asserts status + RFC 7807 body + side effects.

### Changes Required:

#### 1. Confirm-endpoint failure-shape tests

**File**: `src/backend/ReceiptWell.Tests/ReceiptConfirmFailureShapeTests.cs` (new)

**Intent**: Pin the honest-5xx contract and the commit boundary at each step of the non-atomic confirm sequence, so a future regression (e.g. silencing a re-throw to return 200 after a critical write) is caught.

**Contract**: `IClassFixture<ReceiptWellWebFactory>`. Each method clears received calls, configures `ConfirmFlowHarness.FailAt(<step>)`, sends a valid owned confirm request, and asserts:

| Test | `FailAt` | Status | Side-effect oracle |
|------|----------|--------|--------------------|
| Copy fails → nothing committed | `BlobCopy` | honest 500 | `SyncCopyFromUriAsync` received; `MergeOrUploadDocumentsAsync` + `SendMessageAsync` `DidNotReceive` |
| Search write fails → blob copied only | `SearchWrite` | honest 500 | `SyncCopyFromUriAsync` received; `SendMessageAsync` `DidNotReceive` |
| Queue send fails → blob + search committed | `QueueSend` | honest 500 | `SyncCopyFromUriAsync` + `MergeOrUploadDocumentsAsync` received; `SendMessageAsync` received (it was attempted and threw) |
| Cleanup fails → fire-and-forget 200 | `StagingDelete` | **200** | all three critical ops received; `DeleteAsync` received (attempted, failure swallowed) |

The 200 case uses a status+side-effect assertion (not `AssertHonest500Async`) and documents the intentional contract in an XML-doc comment.

#### 2. Single-step endpoint failure tests

**File**: `src/backend/ReceiptWell.Tests/ReceiptEndpointFailureShapeTests.cs` (new)

**Intent**: Prove the staging-slot and list endpoints surface an honest 500 when their single dependency throws, with no spurious side effects.

**Contract**: `IClassFixture<ReceiptWellWebFactory>`, `X-Test-Oid` set. Two tests:
- **Staging-slot upload fails**: stub the staging `BlobClient.UploadAsync(...)` to throw `RequestFailedException`; POST `/receipts/staging-slot`; assert `AssertHonest500Async`. No SAS/delegation setup needed — `UploadAsync` is the first call (`ReceiptBlobService.cs:22`).
- **List search fails**: stub `factory.SearchClient.SearchAsync<ReceiptDocument>(…)` to throw; GET `/receipts`; assert `AssertHonest500Async`; assert no write-side client received any call (read-only path).

### Success Criteria:

#### Automated Verification:

- All Phase 2 tests pass: `dotnet test src/backend/ReceiptWell.sln --filter "FullyQualifiedName~FailureShape"`
- Full suite green with zero warnings: `dotnet test src/backend/ReceiptWell.sln`

#### Manual Verification:

- The Step-3 (queue) test confirms `MergeOrUploadDocumentsAsync` committed before the throw — the partial-state oracle is real, not asserted against an early exit.
- The 200 cleanup test fails if `DeleteAsync`'s catch is changed to re-throw (spot-check by temporarily editing the service locally, optional).

**Implementation Note**: Pause for manual confirmation after automated verification passes.

---

## Phase 3: Docs & sync

### Overview

Record the reusable recipe in the cookbook, flip the rollout status, and confirm the CI gate is active. No code.

### Changes Required:

#### 1. Cookbook — honest-5xx recipe

**File**: `context/foundation/test-plan.md` (§6.2 / new §6.x)

**Intent**: Replace the "Failing-dependency / honest-5xx tests land in §3 Phase 2" placeholder (§6.2) with the actual recipe so the next contributor can add an infra-failure test without re-deriving the harness.

**Contract**: Document: the `ConfirmFlowHarness.FailAt(step)` builder + `ProblemDetailsAssertions.AssertHonest500Async`; the three-part assertion rule (status + RFC 7807 body + side-effect boundary); the `ClearReceivedCalls()` per-method convention for the per-class fixture; the `CanGenerateSasUri → true` SAS bypass; and the model-factory version-sensitivity note.

#### 2. Rollout status + phase note

**File**: `context/foundation/test-plan.md` (§3 table; §6.6)

**Intent**: Move Phase 2 status `researched → complete`; add a 2–3 line §6.6 note capturing anything surprising (SAS bypass, container-name config coupling, model-factory gotchas). Update the header "Last updated" line.

**Contract**: §3 Phase 2 row `Status` cell = `complete`. New §6.6 bullet block "Phase 2 — Infra-boundary failure shape (2026-06-25)".

#### 3. Change identity

**File**: `context/changes/testing-infra-boundary-failure/change.md`

**Intent**: Close out the change.

**Contract**: `status: complete` (or `implemented`), `updated: 2026-06-25`.

### Success Criteria:

#### Automated Verification:

- §3 Phase 2 status reads `complete`: `grep -n "Infra-boundary failure shape" context/foundation/test-plan.md`
- §6.2 no longer contains the "land in §3 Phase 2" placeholder for honest-5xx.

#### Manual Verification:

- Cookbook recipe is followable by someone who wasn't in this session.
- (Optional) Selective Stryker run on the confirm service surfaces no high-value survived mutants: `dotnet stryker --mutate "src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs"` — kill only user/business-meaningful survivors; ignore equivalent/cosmetic mutants (do not chase 100%).

**Implementation Note**: Final phase — confirm docs read well before closing.

---

## Testing Strategy

### Integration Tests (the deliverable):

- Confirm Steps 1–4 failure shapes (copy / search / queue / cleanup) — commit-boundary oracle.
- Staging-slot upload failure and list-search failure — single-step honest 500.
- Positive control: happy-path confirm 200 with all side effects.

### Manual Testing Steps:

1. Run `dotnet test src/backend/ReceiptWell.sln` — all green, zero warnings.
2. Temporarily make `ReceiptConfirmService` swallow the Step-2 re-throw locally and confirm the Step-2 test goes red (proves the test catches the regression it claims to). Revert.
3. (Optional) Run Stryker on `ReceiptConfirmService.cs`; review survived mutants.

## Performance Considerations

None — all tests boot the app offline once per class; no network or disk I/O.

## Migration Notes

None — additive, test-only.

## References

- Research: `context/changes/testing-infra-boundary-failure/research.md`
- Risk #7 + Risk Response Guidance: `context/foundation/test-plan.md` §2
- Confirm service: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:35-135`
- Endpoint catch blocks: `src/backend/ReceiptWell.Web/Program.cs:140-215`
- Harness: `src/backend/ReceiptWell.Tests/Infrastructure/ReceiptWellWebFactory.cs`
- Reference patterns: `ReceiptQueryScopingTests.cs:26-32`, `ReceiptConfirmOwnershipTests.cs:51-54`
- CI gate: `.github/workflows/backend-deploy.yml:29`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Test infrastructure & positive control

#### Automated

- [x] 1.1 Solution builds with zero warnings — 1cf59a6
- [x] 1.2 Positive-control test passes (`ReceiptConfirmFlowTests`) — 1cf59a6
- [x] 1.3 Full suite still green — 1cf59a6

#### Manual

- [x] 1.4 Harness reaches a real 200 (not a 400 InvalidBlob early-exit) — 1cf59a6
- [x] 1.5 Builder reads container names from config — 1cf59a6

### Phase 2: Failure-shape tests (all endpoints)

#### Automated

- [x] 2.1 All Phase 2 tests pass (`~FailureShape`)
- [x] 2.2 Full suite green with zero warnings

#### Manual

- [x] 2.3 Step-3 (queue) test confirms search committed before the throw
- [x] 2.4 200 cleanup test fails if DeleteAsync catch is changed to re-throw (optional spot-check)

### Phase 3: Docs & sync

#### Automated

- [ ] 3.1 §3 Phase 2 status reads `complete`
- [ ] 3.2 §6.2 honest-5xx placeholder replaced

#### Manual

- [ ] 3.3 Cookbook recipe is followable by someone not in this session
- [ ] 3.4 (Optional) Selective Stryker run on ReceiptConfirmService surfaces no high-value survived mutants
