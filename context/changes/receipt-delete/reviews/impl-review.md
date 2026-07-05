<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Receipt Delete Implementation Plan

- **Plan**: context/changes/receipt-delete/plan.md
- **Scope**: Full plan (Phases 1-4 of 4)
- **Date**: 2026-07-05
- **Verdict**: APPROVED
- **Findings**: 0 critical, 2 warnings, 3 observations

## Note on scope creep caught mid-review

The drift-detection pass caught `examples/example1.jpg` — a 2.9MB test
image committed in Phase 1 (`1dab80e`, pre-rebase) with no mention in
the plan and undisclosed content (a real receipt photo, potential
PII concern per this project's security guardrail). Before this
report finalized, the user independently ran an interactive rebase
to drop the file from history and add `examples/**` to `.gitignore`.
This is why commit SHAs shifted across the whole plan (see F2) and
why this isn't listed as an open finding below — it was already
fixed.

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | WARNING |

## Automated verification (re-run on final rebased tree)

- `dotnet build src/backend/ReceiptWell.sln` — pass
- `dotnet test src/backend/ReceiptWell.sln` — 34 passed, 1 skipped
- `npm run lint` (src/frontend) — pass
- `npm test` (src/frontend) — 24 passed

## Findings

### F1 — No failure-shape test for the Search-delete boundary

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Tests/ReceiptDeleteTests.cs
- **Detail**: `ReceiptDeleteService.cs` has two non-atomic side effects with matching try/catch/log/rethrow blocks: Search-delete (lines 43-51) and blob-delete (lines 53-64). `ReceiptDeleteTests.cs` only exercises the blob-delete failure path (`BlobDelete_fails_after_successful_search_delete_returns_honest_500`). The sibling convention this plan explicitly named as its reference — `ReceiptConfirmFailureShapeTests` — tests every non-atomic step, not just one. The Search-delete failure path is currently unverified.
- **Fix**: Add a `Search_delete_fails_returns_500_with_nothing_committed` test mirroring the existing one: stub `DeleteDocumentsAsync` to throw `RequestFailedException(500, ...)`, assert `AssertHonest500Async`, and assert `blobServiceClient.GetBlobContainerClient(...)` was never called (`DidNotReceiveWithAnyArgs`) to prove the blob step didn't run.
- **Decision**: SKIPPED

### F2 — Progress section commit SHAs are stale after the rebase

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Success Criteria
- **Location**: context/changes/receipt-delete/plan.md:287-320
- **Detail**: The interactive rebase rewrote every commit from Phase 1 onward, so the SHAs recorded in the plan's Progress section no longer exist in history: 1dab80e→5cb3ed9 (Phase 1), c284673→7c73574 (Phase 2), f7074d3→a62cbe1 (Phase 3), 628f515→45c1de2 (Phase 4). The epilogue commit (previously 6f0bf99) is now 7f3aaa5. The plan file itself is otherwise accurate — this is a bookkeeping mismatch only.
- **Fix**: Update the four SHA suffixes in Progress to the new hashes (5cb3ed9, 7c73574, a62cbe1, 45c1de2) in a follow-up commit.
- **Decision**: FIXED

### F3 — NotFound branch logs nothing

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptDeleteService.cs:32-35
- **Detail**: Every other rejected/failed branch in this service logs (Warning on ownership violation, Error on the two delete failures, Info on success). The NotFound branch is silent. `ReceiptConfirmService` logs its rejected branches too, so this is a minor parity gap versus the project's own convention rather than a plan violation.
- **Fix**: Add an Information-level `LoggerMessage` for the not-found case, consistent with the backend logging convention (input-not-found is not a validation failure worth Warning, but worth a record).
- **Decision**: FIXED

### F4 — Delete's nested subscriptions aren't cleaned up in ngOnDestroy

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/frontend/src/app/receipts/list/list.component.ts:160-173
- **Detail**: This component tracks `fetchSubscription`/`searchSubscription` as fields and unsubscribes both in `ngOnDestroy` (lines 77-81). `deleteReceipt()`'s two nested `.subscribe()` calls (dialog `afterClosed()`, then the delete HTTP call) aren't stored or cleaned up the same way. Practical risk is low — both are one-shot, self-completing observables — but it diverges from the component's own established convention.
- **Fix**: Store the outer subscription in a field and unsubscribe it in `ngOnDestroy`, matching `fetchSubscription`/`searchSubscription`.
- **Decision**: FIXED

### F5 — Non-404 Search errors on a malformed id fall through to a generic 500

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptDeleteService.cs:32
- **Detail**: The route's `{id}` is an unconstrained string. If a caller sends an id that Azure AI Search rejects as an invalid key (not merely "not found"), `GetDocumentAsync` throws a `RequestFailedException` whose `Status` isn't 404, so the `when (ex.Status == 404)` filter doesn't catch it — it falls through to `Program.cs`'s generic catch and returns a 500 (logged Error) instead of a clean 400/404. Not exploitable (no data-safety impact — the request just fails loudly instead of quietly), just noisier error telemetry than necessary.
- **Fix**: Optional — widen the catch to map any 4xx `RequestFailedException` from the initial fetch to `NotFound`, if this shows up in real error logs. Low priority; not required for MVP.
- **Decision**: SKIPPED
