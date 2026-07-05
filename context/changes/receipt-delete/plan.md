# Receipt Delete Implementation Plan

## Overview

Add a user-initiated hard-delete for receipts, closing the CRUD gap identified in the MVP technical review (Create and Read exist; Update and Delete do not). A user can delete a receipt they own; the receipt's Search index document and its Blob Storage image are both permanently removed. The change also closes a race condition the delete surface introduces in the AI extraction pipeline, and adds frontend UI + confirmation for the action.

## Current State Analysis

- The core item, `ReceiptDocument` (`src/backend/ReceiptWell.Core/ReceiptDocument.cs`), lives as a document in an Azure AI Search index — the sole data store, per its own code comment. The receipt image lives separately in Blob Storage at `{userId}/{receiptId}` in the `receipts` container.
- Only Create (`POST /receipts/staging-slot` + `POST /receipts/confirm`, `ReceiptConfirmService.cs`) and Read (`GET /receipts`, `ReceiptQueryService.cs`) exist today. No `MapDelete`/`MapPut` exists anywhere in `Program.cs`.
- Ownership is enforced two different ways depending on the operation: at confirm time via a blob-name prefix check (`stagingBlobName.StartsWith($"{userId}/")`, `ReceiptConfirmService.cs:38`); at query time via a Search filter (`UserId eq '{userId}'`, `ReceiptQueryService.cs:17`). Delete needs a third variant: fetch-then-compare, since the route only carries a receipt id, not a user-prefixed path.
- The project has an established "honest failure shape" contract for multi-store operations: `ReceiptConfirmService.ConfirmUploadAsync` performs a non-atomic sequence (blob copy → Search write → queue send → staging cleanup) and `ReceiptConfirmFailureShapeTests.cs` / `ProblemDetailsAssertions.AssertHonest500Async` pin that a failure at any step returns a real 500 (RFC 7807 body, no leaked exception detail) rather than a silent success.
- `ReceiptStore` (`src/backend/ReceiptWell.Functions/ReceiptStore.cs`) is the AI extraction pipeline's write path. `SetReadyAsync`/`SetErrorAsync` call `MergeOrUploadDocumentsAsync` unconditionally — if the target document no longer exists (because it was deleted while extraction was in flight), `MergeOrUpload` falls back to Upload semantics and creates a new, mostly-empty document (`UserId`, `BlobUrl`, `FileName`, `FileSize`, `UploadedAt` are absent from the partial merge types `ReceiptEnrichmentDocument`/`ReceiptStatusDocument` and so are written as defaults). This document is invisible to every real user (no `UserId` matches any query filter) but is a permanent, un-cleanable orphan in the index.
- `src/frontend/src/app/receipts/list/list.component.html` renders each receipt in a `receipt-row` with no per-row action affordance today (no edit/delete button exists).
- `@angular/material` is already a frontend dependency; `MatDialogModule`/`MatSnackBarModule` are available with no new package needed. No dialog component exists yet in this codebase — this introduces the first one.
- `ReceiptWell.Tests.csproj` references only `ReceiptWell.Web`, not `ReceiptWell.Functions`. Testing the `ReceiptStore` fix requires adding that project reference; it does not require building out the full Functions test harness that `test-plan.md` §3 Phase 4 defers.
- `context/foundation/test-plan.md`'s Risk Map (§2) has no entry for deletion. Per its own §1 principle 2 ("user concerns are first-class evidence") and its precedent of tracking every IDOR-relevant surface (Risk #1) and every honest-5xx surface (Risk #7), a new risk entry belongs here.

## Desired End State

A signed-in user can delete a receipt they own from the list view, after confirming in a dialog. The receipt disappears from their list immediately and its blob image and Search document are both gone. A user cannot delete another user's receipt (403). Deleting a receipt still being processed by AI extraction is allowed and does not resurrect a ghost document afterward. `test-plan.md` documents this as a tracked risk with its own protecting tests.

Verify via:
- `dotnet test src/backend/ReceiptWell.sln` passes, including the new ownership/failure-shape/not-found and ghost-document-prevention tests.
- `ng test` (frontend) passes, including the new delete-flow component spec.
- Manual: upload a receipt, delete it via the UI, confirm it's gone from the list and a direct blob URL 404s.

### Key Discoveries:

- `ReceiptConfirmOwnershipTests.cs` and `ReceiptConfirmFailureShapeTests.cs` are the reference patterns for the new backend tests — two-identity ownership tests and honest-500 failure-shape tests are non-negotiable in this codebase, not optional additions.
- `ReceiptStore.GetByIdAsync` (`ReceiptWell.Functions/ReceiptStore.cs:10`) already exists and is exactly what `SetReadyAsync`/`SetErrorAsync` need to call before merging, to detect a deleted receipt.
- Backend logging convention (`src/backend/.claude/CLAUDE.md`): exceptions returned to the API client are logged at Error, not Warning.
- Per project convention (`src/backend/.claude/CLAUDE.md`), the example HTTP request collection at `receipt-well.http` must be updated whenever an endpoint is added.

## What We're NOT Doing

- Soft delete / recoverable delete / trash-and-restore — out of scope per the delete-semantics decision (hard delete only).
- Bulk/multi-select delete — single-receipt delete only.
- Update (editing store name/date/tags after upload) — a separate CRUD gap, not part of this change.
- Blocking or queuing deletion of a "pending" receipt — deletion is allowed at any status.
- A cleanup job for orphaned blobs left behind by a failed post-index-delete blob deletion — logged as an error for manual/future follow-up, not built here.
- A full `ReceiptWell.Functions` test harness (`WebApplicationFactory`-equivalent for isolated-worker Functions) — only the minimal project reference needed for the one new `ReceiptStore` unit test is added; `test-plan.md` §3 Phase 4 still owns the broader Functions test buildout.
- Adding this change to `context/foundation/roadmap.md` — the roadmap tracks user-visible product slices (S-01…S-05); this is a technical-completeness fix identified outside that sequence.

## Implementation Approach

Mirror the two conventions this codebase already enforces everywhere else: ownership-guard-before-any-side-effect (like `ConfirmUploadAsync`), and honest-5xx-on-partial-failure across non-atomic multi-store writes (like the confirm flow). The delete endpoint fetches the Search document first (to get its `UserId` for the ownership check — the id-only route can't carry a prefix the way the staging-blob path does), deletes the Search document, then deletes the blob; a blob-delete failure after a successful Search-document delete is a real 500, not swallowed, and is logged at Error for a human to reconcile the orphaned blob later. The extraction pipeline gets a narrow existence-check guard rather than a broader redesign, since the race window it closes is real but rare (delete during the few seconds an in-flight extraction is running).

## Phase 1: Backend delete endpoint

### Overview

Add `DELETE /receipts/{id}`, backed by a new `ReceiptDeleteService`, following the ownership-guard and honest-5xx conventions already established by `ReceiptConfirmService`.

### Changes Required:

#### 1. Delete service

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptDeleteService.cs`

**Intent**: Given a receipt id and the caller's `userId`, verify the receipt exists and is owned by the caller, then delete the Search document and the blob, in that order, surfacing any blob-delete failure as a thrown exception.

**Contract**: New partial class `ReceiptDeleteService(SearchClient searchClient, BlobServiceClient blobServiceClient, IConfiguration configuration, ILogger<ReceiptDeleteService> logger)`, with `Task<ReceiptDeleteResult> DeleteAsync(string receiptId, string userId)`. `ReceiptDeleteResult` is a closed discriminated union (`Success`, `NotFound`, `Forbidden`), matching the shape of `ReceiptConfirmResult`.

- Fetch the document via `searchClient.GetDocumentAsync<ReceiptDocument>(receiptId)`; a `RequestFailedException` with `Status == 404` maps to `NotFound` (caught before any ownership or deletion logic runs).
- If the fetched document's `UserId` does not equal the caller's `userId`, log an ownership violation (mirroring `ReceiptConfirmService.LogOwnershipViolation`'s pattern and level) and return `Forbidden` — no deletion is attempted.
- Delete the Search document via `searchClient.DeleteDocumentsAsync(new[] { new ReceiptDocument { Id = receiptId } })` (key-only delete). Let any `RequestFailedException` here propagate after an Error-level log — this is the first side effect; if it fails, nothing has changed yet.
- Delete the blob via `blobServiceClient.GetBlobContainerClient(_receiptsContainerName).GetBlobClient($"{userId}/{receiptId}").DeleteIfExistsAsync()` (idempotent — the Search document is already gone even if the blob was already missing). Let any exception here propagate after an Error-level log noting the receipt is now index-deleted but the blob delete failed (orphaned blob, needs manual reconciliation).
- Read `_receiptsContainerName` from `IConfiguration["AzureStorage:ReceiptsContainerName"]`, matching `ReceiptConfirmService`'s existing pattern — never hardcode the container name.

#### 2. Endpoint wiring

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Expose the delete service over HTTP, following the same shape as the existing `POST /receipts/confirm` handler (switch on result type, catch-all 500 on unhandled exceptions).

**Contract**: `app.MapDelete("/receipts/{id}", async (HttpContext httpContext, string id, ReceiptDeleteService deleteService, ILoggerFactory loggerFactory) => { ... })`, registered alongside the other `/receipts/*` routes. Register `builder.Services.AddScoped<ReceiptDeleteService>();` next to the other scoped services. Result mapping: `Success` → `Results.NoContent()` (204); `NotFound` → `Results.NotFound()`; `Forbidden` → `Results.Forbid()`; any caught exception → `Results.Problem(statusCode: 500)`, logged at Error with the receipt id and caller's `userId`, matching the existing endpoints' catch-block shape.

#### 3. Example HTTP request collection

**File**: `receipt-well.http`

**Intent**: Keep the manual-testing collection in sync with the new endpoint, per this project's own convention.

**Contract**: Add a `DELETE {{hostAddress}}/receipts/{id}` block (with `Authorization: Bearer {{token}}`) after the existing `GET {{hostAddress}}/receipts?q=rower` block, following the file's existing `###`-separated request format.

### Success Criteria:

#### Automated Verification:

- [ ] Backend build succeeds: `dotnet build src/backend/ReceiptWell.sln`
- [ ] Backend test suite passes: `dotnet test src/backend/ReceiptWell.sln`
- [ ] New ownership test passes: cross-user delete attempt returns 403 with no Search-delete or blob-delete side effects (mirrors `ReceiptConfirmOwnershipTests`)
- [ ] New not-found test passes: deleting a non-existent receipt id returns 404
- [ ] New failure-shape test passes: a blob-delete failure after a successful Search-document delete returns an honest 500 (via `ProblemDetailsAssertions.AssertHonest500Async`), with the Search delete call confirmed to have happened

#### Manual Verification:

- [ ] Using `receipt-well.http`, confirm, list, then delete a receipt against a local run; a subsequent `GET /receipts` no longer lists it
- [ ] Confirm the underlying blob is gone (e.g. via Azurite Storage Explorer or a direct blob-URL request returning 404)

---

## Phase 2: Ghost-document prevention in the extraction pipeline

### Overview

Close the race where an in-flight AI extraction completes after its receipt has been deleted, which would otherwise recreate an orphaned, mostly-empty Search document.

### Changes Required:

#### 1. Existence guard before merge

**File**: `src/backend/ReceiptWell.Functions/ReceiptStore.cs`

**Intent**: `SetReadyAsync` and `SetErrorAsync` must not write to a document that no longer exists — checking first prevents `MergeOrUploadDocumentsAsync` from silently falling back to Upload semantics and creating an orphan.

**Contract**: Both methods call `GetByIdAsync(receiptId, cancellationToken)` first; if it returns `null`, log at Information level (this is an expected outcome of a user deleting mid-extraction, not a warning-worthy anomaly) and return without calling `MergeOrUploadDocumentsAsync`. This narrows the race window but does not eliminate it (a delete between the existence check and the merge call is still possible and accepted as a residual, rare risk — consistent with the MVP's cost-vs-signal testing principle).

#### 2. Test project reference

**File**: `src/backend/ReceiptWell.Tests/ReceiptWell.Tests.csproj`

**Intent**: Enable a unit test against `ReceiptStore`, which lives in `ReceiptWell.Functions`.

**Contract**: Add `<ProjectReference Include="..\ReceiptWell.Functions\ReceiptWell.Functions.csproj" />` alongside the existing `ReceiptWell.Web` reference. This is the only Functions-test-harness change made in this plan — the broader Functions test buildout stays `test-plan.md` §3 Phase 4's responsibility.

#### 3. Ghost-document unit test

**File**: `src/backend/ReceiptWell.Tests/ReceiptStoreGhostDocumentTests.cs`

**Intent**: Prove `SetReadyAsync` (and `SetErrorAsync`) skip the merge when the receipt has been deleted, using a pure unit test against a substituted `SearchClient` — no `WebApplicationFactory` needed, matching the pure-unit pattern in `ReceiptQueryScopingTests.cs`.

**Contract**: Substitute `SearchClient.GetDocumentAsync<ReceiptDocument>` to throw `new RequestFailedException(404, "not found")`; call `SetReadyAsync`/`SetErrorAsync`; assert `MergeOrUploadDocumentsAsync` was never called via `DidNotReceiveWithAnyArgs()`.

### Success Criteria:

#### Automated Verification:

- [ ] Backend build succeeds: `dotnet build src/backend/ReceiptWell.sln`
- [ ] Backend test suite passes: `dotnet test src/backend/ReceiptWell.sln`
- [ ] New ghost-document test passes for both `SetReadyAsync` and `SetErrorAsync`

#### Manual Verification:

- [ ] N/A — this phase has no user-visible surface; automated coverage is the verification

---

## Phase 3: Frontend delete UI

### Overview

Let a user delete a receipt from the list view, with a confirmation dialog and immediate local-list update on success.

### Changes Required:

#### 1. Service method

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Add the HTTP call for the new endpoint, following the existing methods' style.

**Contract**: `deleteReceipt(id: string): Observable<void>` calling `this.http.delete<void>(\`${environment.apiUrl}/receipts/${id}\`)`.

#### 2. Confirmation dialog component

**File**: `src/frontend/src/app/receipts/delete-confirm-dialog/delete-confirm-dialog.component.ts` (+ inline template per project convention for small components)

**Intent**: A small, reusable Material dialog asking the user to confirm an irreversible delete.

**Contract**: Standalone component using `MatDialogModule`/`MatButtonModule`, `ChangeDetectionStrategy.OnPush`, injected via Angular's `MatDialogRef` to close with `true`/`false`. Takes the receipt's file name (via `MAT_DIALOG_DATA`) to render in the confirmation text (e.g. "Delete receipt.jpg? This can't be undone."), with Cancel and Delete actions. Must satisfy the project's WCAG AA / AXE requirements (Material dialogs provide focus trap and ESC-to-close by default).

#### 3. List component wiring

**File**: `src/frontend/src/app/receipts/list/list.component.ts`

**Intent**: Add a per-row delete affordance that opens the confirmation dialog, calls the service on confirm, and removes the row from local state on success without a full reload.

**Contract**: Inject `MatDialog` and `MatSnackBar`. New method `deleteReceipt(receipt: ReceiptSummary): void` opens `DeleteConfirmDialogComponent` with the receipt's `fileName` as data; on `afterClosed()` emitting `true`, calls `receiptService.deleteReceipt(receipt.id)`, and on success removes the item from the `receipts` signal by id (`this.receipts.update(list => list.filter(r => r.id !== receipt.id))`); on error, shows a `MatSnackBar` message ("Failed to delete receipt. Please try again.") and leaves the list unchanged. Add `MatDialogModule` is not needed in the component's own `imports` (dialogs are opened programmatically); add `MatSnackBarModule` is likewise not needed for `MatSnackBar` injection. Deletion is allowed regardless of the receipt's `status` (no disabling for `pending`).

#### 4. List template

**File**: `src/frontend/src/app/receipts/list/list.component.html`

**Intent**: Add a delete button to each `receipt-row`.

**Contract**: A `mat-icon-button` with `aria-label="Delete {{ receipt.fileName }}"` and a `delete` icon, calling `(click)="deleteReceipt(receipt)"`, placed within the existing `receipt-row__primary` action area.

### Success Criteria:

#### Automated Verification:

- [ ] Frontend lint passes: `npm run lint` (in `src/frontend/`)
- [ ] Frontend unit tests pass: `npm test` (in `src/frontend/`)
- [ ] New `list.component.spec.ts` cases pass: delete button opens the confirmation dialog; confirming calls `ReceiptService.deleteReceipt` and removes the row; cancelling calls neither
- [ ] New dialog component spec passes (if warranted by its logic — a pure presentational dialog with `MatDialogRef.close(true/false)` may fold into the list component's spec instead of a standalone file)

#### Manual Verification:

- [ ] In a running local app, upload a receipt, click delete, confirm — the row disappears without a page reload
- [ ] Click delete, then cancel — the row remains
- [ ] Trigger a delete failure (e.g. stop the backend mid-request) and confirm a snackbar error appears and the row remains
- [ ] Run an AXE accessibility check against the dialog (focus trap, ESC close, labeled actions)

---

## Phase 4: Documentation

### Overview

Track the new delete surface as a risk in the project's living test plan, consistent with how every other IDOR-relevant and honest-5xx-relevant surface is already tracked.

### Changes Required:

#### 1. Risk Map entry

**File**: `context/foundation/test-plan.md`

**Intent**: Add a Risk Map row for the delete surface, covering both the ownership dimension and the honest-failure-shape dimension, following the existing table's columns and sourcing convention.

**Contract**: New row `#8` in the §2 Risk Map table: "A user can delete another user's receipt (IDOR at delete), or a delete does not fully remove data from both stores, leaving an orphaned blob or a resurrected ghost document" — Impact: High, Likelihood: Medium, Source: this change (`receipt-delete`) + precedent from Risk #1 (IDOR) and Risk #7 (honest-5xx). Add a corresponding row to the §2 Risk Response Guidance table describing what would prove protection (two-identity ownership test + honest-500 on blob-delete failure + ghost-document-prevention test) and the anti-pattern to avoid (single-user fixture; asserting only the happy path).

#### 2. Phase note

**File**: `context/foundation/test-plan.md`

**Intent**: Record this change's rollout under §6.6, per the project's own "append a 2-3 line note after each phase lands" convention.

**Contract**: A new dated entry under §6.6 (e.g. "**Receipt delete (2026-07-01):**") summarizing: the fetch-then-compare ownership pattern (distinct from the prefix-check and filter-based patterns already documented), the Search-then-blob delete ordering with honest-500 on blob failure, and the ghost-document guard added to `ReceiptStore`.

### Success Criteria:

#### Automated Verification:

- [ ] N/A — documentation-only phase

#### Manual Verification:

- [ ] `test-plan.md` renders correctly and the new Risk #8 row is consistent in format with rows #1-#7

---

## Testing Strategy

### Unit Tests:

- `ReceiptStoreGhostDocumentTests.cs`: `SetReadyAsync`/`SetErrorAsync` skip the merge when the target document is gone (Phase 2).

### Integration Tests:

- Cross-user delete attempt → 403, no Search-delete/blob-delete side effects (Phase 1, mirrors `ReceiptConfirmOwnershipTests`).
- Delete of a non-existent receipt id → 404 (Phase 1).
- Blob-delete failure after a successful Search-document delete → honest 500 via `ProblemDetailsAssertions.AssertHonest500Async`, with the Search delete confirmed to have already happened (Phase 1, mirrors `ReceiptConfirmFailureShapeTests`).
- Happy-path delete → 204, both Search-delete and blob-delete confirmed called with the right identifiers (Phase 1).

### Manual Testing Steps:

1. Upload a receipt, confirm it appears in the list, delete it, confirm it disappears and the blob URL 404s.
2. Attempt to delete a receipt while it is still "pending" — confirm it deletes cleanly and no ghost row appears later in the list once the (now-orphaned) extraction message would have completed.
3. Cancel a delete confirmation and confirm nothing changes.
4. Force a delete failure and confirm the snackbar error appears with the row intact.

## Performance Considerations

None beyond existing patterns — this is a single-document delete plus a single blob delete, both already bounded operations elsewhere in the codebase (comparable cost to the existing confirm flow's writes).

## Migration Notes

No data migration needed. Existing receipts are unaffected; the new endpoint operates on the current schema with no field additions.

## References

- Prior MVP technical review (this conversation) identified the CRUD gap this change closes.
- Reference ownership pattern: `src/backend/ReceiptWell.Tests/ReceiptConfirmOwnershipTests.cs`
- Reference failure-shape pattern: `src/backend/ReceiptWell.Tests/ReceiptConfirmFailureShapeTests.cs`, `src/backend/ReceiptWell.Tests/Infrastructure/ProblemDetailsAssertions.cs`
- Reference pure-unit pattern: `src/backend/ReceiptWell.Tests/ReceiptQueryScopingTests.cs`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend delete endpoint

#### Automated

- [x] 1.1 Backend build succeeds — 1dab80e
- [x] 1.2 Backend test suite passes — 1dab80e
- [x] 1.3 New ownership test passes — 1dab80e
- [x] 1.4 New not-found test passes — 1dab80e
- [x] 1.5 New failure-shape test passes — 1dab80e

#### Manual

- [x] 1.6 `.http`-driven confirm/list/delete round-trip against a local run — 1dab80e
- [x] 1.7 Confirm underlying blob is gone after delete — 1dab80e

### Phase 2: Ghost-document prevention in the extraction pipeline

#### Automated

- [x] 2.1 Backend build succeeds — c284673
- [x] 2.2 Backend test suite passes — c284673
- [x] 2.3 New ghost-document test passes for `SetReadyAsync` and `SetErrorAsync` — c284673

### Phase 3: Frontend delete UI

#### Automated

- [x] 3.1 Frontend lint passes — f7074d3
- [x] 3.2 Frontend unit tests pass — f7074d3
- [x] 3.3 New `list.component.spec.ts` delete-flow cases pass — f7074d3
- [x] 3.4 New dialog component spec passes (or is folded into the list spec) — f7074d3

#### Manual

- [x] 3.5 Delete + confirm removes the row without a page reload — f7074d3
- [x] 3.6 Delete + cancel leaves the row — f7074d3
- [x] 3.7 Delete failure shows a snackbar error and leaves the row — f7074d3
- [x] 3.8 AXE accessibility check on the dialog — f7074d3

### Phase 4: Documentation

#### Manual

- [x] 4.1 `test-plan.md` Risk #8 row is consistent in format with rows #1-#7 — 628f515
