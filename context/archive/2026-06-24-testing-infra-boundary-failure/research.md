---
date: 2026-06-25T00:00:00+02:00
researcher: Krzysztof Dudzik
git_commit: 40a24413ee9a45b009ed6df41ae526124631ad19
branch: develop
repository: receipt-well
topic: "Infra-boundary failure shape — Risk #7 grounding (Phase 2 rollout)"
tags: [research, testing, infra-boundary, error-handling, azure-clients, integration-tests]
status: complete
last_updated: 2026-06-25
last_updated_by: Krzysztof Dudzik
---

# Research: Infra-boundary failure shape (Phase 2 rollout)

**Date**: 2026-06-25  
**Git Commit**: 40a24413ee9a45b009ed6df41ae526124631ad19  
**Branch**: develop  
**Repository**: receipt-well

## Research Question

Ground Risk #7 from `context/foundation/test-plan.md`:

> A thrown dependency error (Blob/Queue/Search/Function) surfaces a clean, honest 5xx — never a silent success or partial persist without record.

Specifically: locate every Azure client call site, trace how each thrown exception reaches the HTTP response boundary, identify where partial state can commit before a failure, and verify what the Phase 1 test harness can inject.

---

## Summary

The honest-5xx contract is **partially in place but incomplete**.

All three web API endpoints have individual `try/catch` blocks that catch any exception and return `Results.Problem(statusCode: 500)`. For the endpoints targeted by Phase 2 tests, thrown Azure SDK exceptions will produce a well-formed RFC 7807 Problem Details 500 — this is the correct behavior the tests must prove.

Two gaps matter for Risk #7:

1. **POST /receipts/confirm is a non-atomic multi-step sequence** (blob copy → search write → queue enqueue → staging cleanup). If the search write fails, the blob has already been copied to the receipts container. If the queue send fails, both the blob and the search index entry have already been written. The client gets 500 — that is correct — but the side effects are not rolled back. Tests must assert *both* the error status code *and* which side effects committed, so the oracle comes from the code contract, not just the HTTP status.

2. **The staging blob delete is intentionally fire-and-forget** (`catch { log; }` with no re-throw). A failure there returns 200. This is a deliberate design choice (cleanup failure does not make the upload fail), not a Risk #7 violation. Tests should confirm this behaviour is intentional rather than accidentally silencing a real error.

There is **no global exception middleware** (`UseExceptionHandler`, `AddProblemDetails`). Exceptions that escape the endpoint lambda — for example from middleware code running before the handler — would not go through the `Results.Problem` path. Phase 2 tests stay inside the three endpoint lambdas where the catch blocks operate, so this gap does not affect Phase 2 coverage but is noted as a future hardening candidate.

Azure Functions failure paths (SetReadyAsync failing after AI extraction) are noted below but are scoped to **Phase 4** per the test plan (§3, §6.3).

---

## Detailed Findings

### 1. Web API — Middleware pipeline and global error handling

**File**: `src/backend/ReceiptWell.Web/Program.cs:111–139`

Middleware pipeline in order:
```
app.MapOpenApi()             (development only)
app.UseHttpsRedirection()
app.UseCors()
app.UseAuthentication()
app.UseAuthorization()
app.MapHealthChecks(...)
app.MapPost("/receipts/staging-slot", ...)
app.MapPost("/receipts/confirm", ...)
app.MapGet("/receipts", ...)
```

There is no `app.UseExceptionHandler()`, `builder.Services.AddProblemDetails()`, or `app.UseProblemDetails()` anywhere in `Program.cs`. Exception handling for the three receipt endpoints is **inline try/catch only**.

**Impact for Phase 2**: All three endpoints wrap their entire bodies in `try { ... } catch (Exception ex) { return Results.Problem(statusCode: 500); }`. Any Azure SDK exception thrown from a service method will be caught by the endpoint's own catch block and converted to `Results.Problem(statusCode: 500)` — a proper RFC 7807 response:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "Internal Server Error",
  "status": 500
}
```

No exception detail is returned to the client; exceptions are logged server-side only.

---

### 2. POST /receipts/staging-slot — Blob failure

**Service**: `src/backend/ReceiptWell.Web/Services/ReceiptBlobService.cs`  
**Endpoint catch block**: `Program.cs:140–157`

Operation sequence (single-step, no partial-persist risk):

| Step | Line | Operation | Try/Catch |
|------|------|-----------|-----------|
| 1 | 22 | `BlobClient.UploadAsync(...)` — writes empty placeholder to staging container | No — propagates to endpoint catch |

`ReceiptBlobService.CreateStagingSlotAsync` contains no try/catch of its own. Any `RequestFailedException` from the blob call propagates to the endpoint's catch block → 500.

**Oracle**: If the blob upload throws, the client receives 500. No blob is created because the exception fires before the method returns. No other side effects exist.

**Test verification needed**:
- Assert HTTP 500.
- Assert `factory.BlobServiceClient.DidNotReceiveWithAnyArgs()` or confirm no blob methods completed successfully (depending on which NSubstitute call to intercept).

---

### 3. POST /receipts/confirm — Non-atomic multi-step sequence

**Service**: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:35–135`  
**Endpoint catch block**: `Program.cs:159–195`

This is the most complex endpoint. The operation sequence commits side effects in steps, and later-step failures cannot undo earlier commits:

| Step | Lines | Operation | Try/Catch in service | Failure outcome |
|------|-------|-----------|----------------------|-----------------|
| 0 | 50 | `stagingBlob.GetPropertiesAsync()` — validates blob exists | Yes — catches `RequestFailedException`, returns `InvalidBlob` | 400 (not 500); early exit before any write |
| 0b | 65 | `DownloadFirstBytesAsync(stagingBlob, 16)` — magic-byte validation | No — propagates | 500, no write committed |
| **1** | **90** | **`targetBlob.SyncCopyFromUriAsync(stagingReadSasUri)` — copies blob to receipts container** | **No — propagates** | **500, blob IS NOT copied (failure at this step is atomic)** |
| **2** | **106** | **`searchClient.MergeOrUploadDocumentsAsync(receiptDocument)` — creates search index entry** | **Yes — logs and re-throws** | **500, blob WAS already copied (partial state)** |
| **3** | **116** | **`queueClient.SendMessageAsync(receiptId)` — enqueues for async extraction** | **Yes — logs and re-throws** | **500, blob + search WAS committed (partial state)** |
| 4 | 126 | `stagingBlob.DeleteAsync()` — cleanup of staging blob | Yes — logs, **does NOT re-throw** | **200** returned even if cleanup fails (fire-and-forget) |

**Key design facts the tests must encode as oracle (not derive from the code):**

- Failure at Step 1 (blob copy): no state written anywhere. Client gets 500, nothing to clean up.
- Failure at Step 2 (search write): the receipt blob already exists in the receipts container under the target path, but there is no search index entry. Client gets 500. The blob is orphaned until a future retry or manual cleanup. The queue message was never sent so extraction won't run.
- Failure at Step 3 (queue send): the receipt blob AND search index entry both exist. Client gets 500. The receipt will appear in search results but stay in "pending" status forever (no extraction will be triggered).
- Failure at Step 4 (staging cleanup): the three critical operations all succeeded. Client gets **200**. The staging blob leaks but the receipt is fully committed. This is intentional.

These are not defects — they are the current design contract. Phase 2 tests document and assert this contract so any future regression (e.g. accidentally silencing a re-throw at Step 2 to also return 200) is immediately caught.

---

### 4. GET /receipts — Search query failure

**Service**: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:9–38`  
**Endpoint catch block**: `Program.cs:197–215`

Single operation:
- `Program.cs:197`: `searchClient.SearchAsync<ReceiptDocument>(...)` — no try/catch in service.

Any `RequestFailedException` propagates to the endpoint catch → 500. No side effects possible (read-only).

**Note on async enumeration**: `GetReceiptsAsync` materialises the full result set server-side (`await foreach` over results, building a list) before returning. The endpoint returns `Results.Ok(summaries)` only after the method completes. If the enumeration throws mid-stream, the exception propagates to the service method before `Results.Ok` is ever called, and the endpoint catch converts it to 500. There is no risk of a partial response being sent.

---

### 5. Health checks — not in scope for Phase 2

All three health check services (`BlobStorageHealthCheck`, `QueueHealthCheck`, `SearchHealthCheck`) catch exceptions and return `HealthCheckResult.Unhealthy(ex.Message)`. They never throw. The `/health` endpoint correctly surfaces unhealthy status rather than a 5xx. This is correct behaviour and not a Risk #7 target.

---

### 6. Azure Functions failure surface — scoped to Phase 4

**Files**:
- `src/backend/ReceiptWell.Functions/ExtractReceiptFunction.cs`
- `src/backend/ReceiptWell.Functions/ExtractReceiptPoisonFunction.cs`
- `src/backend/ReceiptWell.Functions/host.json` — `maxDequeueCount: 5`

**Key finding**: `ReceiptStore.SetReadyAsync()` at `ExtractReceiptFunction.cs:84` is called after successful AI extraction with no try/catch. If the search index write fails, the function throws → runtime retries up to 5× → moves message to poison queue. The poison handler (`ExtractReceiptPoisonFunction`) calls `SetErrorAsync()` also without a try/catch. If the poison handler exhausts its 5 retries, the receipt stays at "pending" status indefinitely (true silent failure: no 5xx surfaces to a user, no error written to the index).

This is the most severe infra-boundary gap in the project, but it is **Functions-internal and not testable through the web API integration harness**. Per test-plan §6.3 and §3 Phase 4, Functions tests use direct handler invocation with faked bindings. This finding should inform Phase 4 planning — note it in the open questions.

---

### 7. Phase 1 test harness — failure injection recipe

**Factory**: `src/backend/ReceiptWell.Tests/Infrastructure/ReceiptWellWebFactory.cs`

Public substitute properties (set up via `AddSingleton` in `ConfigureTestServices`):
```csharp
public BlobServiceClient BlobServiceClient { get; }     // Substitute.For<BlobServiceClient>()
public SearchClient SearchClient { get; }               // Substitute.For<SearchClient>()
public SearchIndexClient SearchIndexClient { get; }     // Substitute.For<SearchIndexClient>()
public QueueClient QueueClient { get; }                 // Substitute.For<QueueClient>()
```

**Lifecycle**: `IClassFixture<ReceiptWellWebFactory>` — one factory instance per test class. Substitutes are singletons within the factory; call history **accumulates across test methods** in the same class. Configure substitute behaviour inside each test method (not in a shared constructor/field) to avoid cross-test bleed.

**Exception injection pattern** (NSubstitute — `Azure.Core.RequestFailedException`):

```csharp
// Make a method throw when called
factory.SearchClient
    .MergeOrUploadDocumentsAsync(Arg.Any<IndexDocumentsBatch<ReceiptDocument>>(), Arg.Any<IndexDocumentsOptions>(), Arg.Any<CancellationToken>())
    .Returns(Task.FromException<Response<IndexDocumentsResult>>(
        new Azure.RequestFailedException(500, "Simulated Search failure")));
```

For methods that return `void`/`Task` with no generic result:
```csharp
factory.QueueClient
    .SendMessageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
    .Returns(Task.FromException<Response<SendReceipt>>(
        new Azure.RequestFailedException(500, "Simulated Queue failure")));
```

**Existing patterns to follow**:
- `ReceiptQueryScopingTests.cs:26–32` — `Arg.Do<T>` to capture arguments + `.Returns(...)` for response shaping
- `ReceiptConfirmOwnershipTests.cs:51–54` — `DidNotReceiveWithAnyArgs()` to assert no call was made

**NuGet packages providing exception types**: `Azure.Core` (transitive from `Azure.Storage.Blobs`, `Azure.Storage.Queues`, `Azure.Search.Documents`). Use `Azure.RequestFailedException`. No additional package references needed.

**Auth in new tests**: same pattern as existing — inject `X-Test-Oid: <some-guid>` header to authenticate with a real oid claim. The confirm-ownership tests use a two-identity setup; single-identity is sufficient for 5xx tests.

---

## Code References

| File | Lines | What's there |
|------|-------|--------------|
| `src/backend/ReceiptWell.Web/Program.cs` | 113–139 | Full middleware pipeline (no global exception handler) |
| `src/backend/ReceiptWell.Web/Program.cs` | 140–157 | POST /receipts/staging-slot — endpoint catch block |
| `src/backend/ReceiptWell.Web/Program.cs` | 159–195 | POST /receipts/confirm — endpoint catch block |
| `src/backend/ReceiptWell.Web/Program.cs` | 197–215 | GET /receipts — endpoint catch block |
| `src/backend/ReceiptWell.Web/Services/ReceiptBlobService.cs` | 16–48 | CreateStagingSlotAsync — unguarded blob call |
| `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs` | 48–57 | GetPropertiesAsync catch — safe, returns InvalidBlob |
| `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs` | 90 | SyncCopyFromUriAsync — Step 1 commit, no catch |
| `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs` | 104–112 | MergeOrUploadDocumentsAsync catch — logs and re-throws |
| `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs` | 114–122 | SendMessageAsync catch — logs and re-throws |
| `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs` | 124–131 | DeleteAsync catch — swallows, 200 returned |
| `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs` | 9–38 | GetReceiptsAsync — unguarded search call |
| `src/backend/ReceiptWell.Tests/Infrastructure/ReceiptWellWebFactory.cs` | 35–38 | Public substitute properties |
| `src/backend/ReceiptWell.Tests/Infrastructure/ReceiptWellWebFactory.cs` | 71–74 | AddSingleton wiring for substitutes |
| `src/backend/ReceiptWell.Tests/ReceiptQueryScopingTests.cs` | 26–32 | Arg.Do + Returns pattern |
| `src/backend/ReceiptWell.Tests/ReceiptConfirmOwnershipTests.cs` | 51–54 | DidNotReceiveWithAnyArgs pattern |
| `src/backend/ReceiptWell.Functions/ExtractReceiptFunction.cs` | 84 | SetReadyAsync — no try/catch (Phase 4 risk) |
| `src/backend/ReceiptWell.Functions/ExtractReceiptPoisonFunction.cs` | 16 | SetErrorAsync — no try/catch (Phase 4 risk) |
| `src/backend/ReceiptWell.Functions/host.json` | 6 | `maxDequeueCount: 5` |

---

## Architecture Insights

**Error-translation contract is endpoint-scoped, not global.** All three endpoints implement `try { ... } catch (Exception ex) { Results.Problem(statusCode: 500); }`. This works for Phase 2 test targets. The absence of global middleware is a future hardening concern but does not create gaps in the tested paths.

**The confirm endpoint's non-atomicity is the highest-value Phase 2 target.** Three distinct side effects can commit independently. The tests that assert which clients received calls (and which didn't) after each step's failure give the highest signal: they would catch a future regression where an exception is swallowed and a 200 is returned despite a partial commit.

**The fire-and-forget staging cleanup is correct by design.** The staging delete at `ReceiptConfirmService.cs:124–131` intentionally returns 200 after swallowing a delete failure. Tests should confirm this contract (200 returned, delete attempted, log emitted) rather than flag it as a bug.

**NSubstitute substitute isolation note.** The factory is per test-class, and substitute call history accumulates. For the Step 2 and Step 3 failure tests (where earlier steps succeed), the substitutes for Step 1 (BlobClient methods) must return success values, not their default `null`/empty. Set up the full chain for each test method independently.

---

## Historical Context

- `context/changes/testing-access-control/` — Phase 1 established the `ReceiptWellWebFactory` harness with NSubstitute substitutes. The `DidNotReceiveWithAnyArgs` and `Arg.Do` patterns there are directly reusable for Phase 2.
- `context/archive/receipt-upload-confirm/` — The confirm-upload flow is the source of the multi-step sequence. Understanding the original design intent (blob first, then index, then queue) is documented in that archive.

---

## Open Questions

1. **Should the Phase 4 Functions tests cover `SetReadyAsync` exhausting all retries?** The poison handler failing silently is the highest-severity gap found in this research. Recommend Phase 4 planning explicitly includes: (a) SetReadyAsync throws → retries exhaust → poison handler invoked, and (b) poison handler SetErrorAsync throws → all retries fail → message dead-lettered, receipt stays "pending". The test harness for Functions (direct handler invocation, faked bindings) needs to simulate both paths.

2. **Should the `SyncCopyFromUriAsync` step (Step 1) failure be tested?** It is the cleanest case (no prior state) but the NSubstitute setup is more complex because `SyncCopyFromUriAsync` is called on a `BlobClient` instance obtained by chaining `factory.BlobServiceClient.GetBlobContainerClient(...)  .GetBlobClient(...)`. The substitute must be configured to return a `BlobContainerClient` substitute whose `GetBlobClient` also returns a substitute whose `SyncCopyFromUriAsync` throws. This chain setup is feasible but verbose — confirm the existing pattern in Phase 1 tests already handles chained container/blob client resolution, or decide to skip this scenario as lower priority (no prior state = no partial-persist risk).

3. **DelegationTokenProvider (`GetUserDelegationKeyAsync`) failure in staging-slot.** The blob SAS URI generation path calls `DelegationTokenProvider.GetOrFetchAsync()` which calls `blobServiceClient.GetUserDelegationKeyAsync()`. This happens AFTER the placeholder blob upload. If the SAS generation fails, the client gets 500 but an empty placeholder blob was already written to staging. This is a minor partial-state scenario — decide in planning whether it warrants a dedicated test case.

---

## Plan input: recommended Phase 2 test cases

Ordered by signal value (cost × signal from test plan §1):

| # | Scenario | Endpoint | Step that fails | Expected status | Side effects to assert |
|---|----------|----------|-----------------|-----------------|------------------------|
| 1 | Search write fails after blob copy | POST /confirm | Step 2: `MergeOrUploadDocumentsAsync` throws | 500 | Blob copy DID complete; queue send did NOT occur |
| 2 | Queue send fails after blob + search | POST /confirm | Step 3: `SendMessageAsync` throws | 500 | Blob copy + search write DID complete; queue message NOT sent |
| 3 | Search read fails on list | GET /receipts | `SearchAsync` throws | 500 | No side effects (read-only) |
| 4 | Blob upload fails on staging slot | POST /staging-slot | `UploadAsync` throws | 500 | No blob written |
| 5 | Staging cleanup fails (fire-and-forget) | POST /confirm | Step 4: `DeleteAsync` throws | **200** | Main operations DID succeed; cleanup failure logged, not surfaced |

Test case #1 and #2 together prove the most critical contract: the multi-step sequence correctly surfaces 5xx on failure and does not silently return 200 when a critical write fails. Cases #3 and #4 are simpler but complete the coverage surface. Case #5 documents the intentional fire-and-forget cleanup contract.

**Layer**: integration only — all five cases use `ReceiptWellWebFactory` + NSubstitute. No hermetic stubs or e2e needed.
