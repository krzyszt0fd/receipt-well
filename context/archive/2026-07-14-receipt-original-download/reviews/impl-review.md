<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Original Receipt Download Implementation Plan

- **Plan**: context/changes/receipt-original-download/plan.md
- **Scope**: Phase 1-2 of 3
- **Date**: 2026-07-29
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 3 warnings, 2 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | WARNING |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

## Findings

### F1 — SAS-generation Azure boundary has no error handling/logging

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptDownloadService.cs:71-76
- **Detail**: `GenerateReadSasAsync`'s `blob.GenerateSasUri(sasBuilder)` and `delegationTokenProvider.GetOrFetchAsync()` calls are unwrapped — any failure surfaces only as the endpoint's generic 500 catch-all, with no receiptId-scoped log entry. Every other Azure boundary call in this file and its siblings (`ReceiptDeleteService.cs:44-65`, two try/catch blocks with `[LoggerMessage(Level = LogLevel.Error)]` before rethrow) follows a consistent "log at Error, then rethrow" convention. This is the one boundary call in the new service that skips it.
- **Fix**: Wrap the two calls in `GenerateReadSasAsync` in a try/catch mirroring `ReceiptDeleteService.cs:54-65` — log `[LoggerMessage(Level = LogLevel.Error, Message = "SAS generation failed for receiptId {ReceiptId}")]` with receiptId, then rethrow.
- **Decision**: FIXED

### F2 — No test for a SAS-generation failure returning an honest 500

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Tests/ReceiptDownloadTests.cs
- **Detail**: The plan explicitly asked this test file to follow `ReceiptDeleteTests.cs` conventions. That file has a fourth test — `BlobDelete_fails_after_successful_search_delete_returns_honest_500` (`ReceiptDeleteTests.cs:112-155`) — asserting the Azure-boundary failure shape via `ProblemDetailsAssertions.AssertHonest500Async`. `ReceiptDownloadTests.cs` only covers owner/non-owner/not-found/no-auth; the equivalent "Azure Storage throws during SAS generation → honest 500" case is untested. Pairs with F1 — the fix in F1 has no test proving it works.
- **Fix**: Add a test that makes `BlobServiceClient`/`DelegationTokenProvider` throw during SAS generation for an owned receipt, and assert `ProblemDetailsAssertions.AssertHonest500Async(response)`, mirroring `ReceiptDeleteTests.cs:112-155`.
- **Decision**: FIXED

### F3 — Unplanned icon-button spacing change (`.receipt-row__actions`)

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: src/frontend/src/app/receipts/list/list.component.html:89,106; list.component.scss:94-107
- **Detail**: Phase 2's plan didn't mention row-button spacing. During manual verification the user asked for tighter icon spacing; the fix wrapped rename/download/delete (and save/cancel) buttons in a `.receipt-row__actions` span and forced their box size down with `!important` overrides. EXTRA relative to the plan — not harmful (narrowly scoped selector; a sub-agent confirmed Angular Material's touch-target overlay stays at 48px independent of the visual box, so accessibility is preserved) — but the plan text doesn't reflect it.
- **Fix**: Add a one-line addendum note under Phase 2's "Changes Required" recording the spacing tweak, so the plan stays an accurate record of what shipped.
- **Decision**: FIXED

### F4 — Plan's Phase 2 contract text is stale (`window.location.href`)

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Plan Adherence
- **Location**: context/changes/receipt-original-download/plan.md:203-205
- **Detail**: The plan's Phase 2 contract literally says the download is triggered by "assigning the URI to `window.location.href`". During manual testing, same-tab navigation was found to replace the entire SPA with the browser's native error page when the blob URL is unreachable — there's no response to intercept via Content-Disposition when the request never completes. The user approved switching to a hidden `<a target="_blank" rel="noopener">` anchor instead (`list.component.ts:downloadReceipt`/`triggerDownload`), already committed (2653f9d). Only the plan's prose is now inaccurate for a future reader.
- **Fix**: Update plan.md's Phase 2 "Contract" bullet to describe the new-tab-anchor approach and the reason for it.
- **Decision**: FIXED (resolved together with F3's addendum edit)

### F5 — RFC 5987 encoding gap in Content-Disposition filenames (pre-existing)

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptDownloadService.cs:59 (same gap pre-exists at ReceiptConfirmService.cs:157-175 and receipt.service.ts:54)
- **Detail**: `Uri.EscapeDataString` doesn't encode `! * ' ( )`, which RFC 5987's `attr-char` set requires encoded. A filename containing one of those (e.g. `receipt (copy).png`) produces a technically non-conformant `filename*=UTF-8''...` value — most browsers tolerate it, but it's not correct. Not a regression from this phase: the new code faithfully copies an encoding pattern that already existed at 2 other call sites before this plan started.
- **Fix A**: Fix now across all 3 call sites with a stricter percent-encoder.
  - Strength: Closes the gap everywhere at once instead of leaving 3 divergent copies.
  - Tradeoff: Touches 3 files (2 backend, 1 frontend) for an edge case outside this phase's actual scope.
  - Confidence: MED — mechanical fix, but real-world filenames hitting exactly `! * ' ( )` are rare.
  - Blind spot: Haven't checked whether any existing receipts already have filenames containing these characters.
- **Fix B ⭐ Recommended**: Leave as-is for this change; record as a lesson for a future dedicated fix.
  - Strength: Keeps this feature's diff scoped to the download action; doesn't block Phase 3.
  - Tradeoff: The gap persists a little longer across all 3 sites.
  - Confidence: HIGH — pre-existing, shared, low-severity, unrelated to the new work.
  - Blind spot: None significant.
- **Decision**: SKIPPED
