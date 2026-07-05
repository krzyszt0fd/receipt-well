<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Inline-Editable Receipt Filename (Update / CRUD completion)

- **Plan**: context/changes/change-receipt-title/plan.md
- **Scope**: Full plan (Phase 1 + Phase 2)
- **Date**: 2026-07-05
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 2 warnings, 1 observation

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

## Findings

### F1 — Shared rename subscription can silently orphan a row's update

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: src/frontend/src/app/receipts/list/list.component.ts:56, 252-253
- **Detail**: `saveEdit` keeps a single `renameSubscription` field for all rows (line 56) and unconditionally unsubscribes it before starting a new one (line 252), never awaiting the previous request. If a user renames row A then renames row B before row A's PUT resolves, unsubscribing row A's HttpClient observable aborts that in-flight request. Row A's optimistic filename update is then never confirmed or reverted — it silently diverges from the server until the next poll cycle overwrites it. Delete has the same single-subscription shape but no functional cost since the row is removed either way; the cost here is unique to rename because it performs an optimistic-then-authoritative round trip.
- **Fix**: Key subscriptions per receipt id (e.g. a `Map<string, Subscription>` cleared per id on completion/error), and unsubscribe/clear each id's subscription in `ngOnDestroy` by iterating the map, mirroring how `editingId` already gates re-entrancy per row.
  - Strength: Removes the class of bug entirely; the map pattern is a small, contained change local to this component.
  - Tradeoff: A few extra lines of bookkeeping vs. a single field.
  - Confidence: HIGH — confirmed by reading the exact unsubscribe/subscribe sequence; Angular's HttpClient aborts the underlying request when its subscription is torn down before completion.
  - Blind spot: Requires a user to rename two different rows within one round-trip window (typically well under a second) to trigger — low likelihood in normal single-user use, but real.
- **Decision**: FIXED — switched `renameSubscription` to a per-id `Map<string, Subscription>` in list.component.ts; `ngOnDestroy` unsubscribes all entries, `saveEdit` keys/clears by receipt id.

### F2 — Filename length is validated client-side only

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Web/Program.cs:257-264
- **Detail**: The plan's "empty-name defense in depth" requirement is only half implemented: the endpoint rejects null/whitespace `FileName` but has no upper-bound check. `MAX_FILENAME_LENGTH = 255` exists only in the Angular component (list.component.ts:23) and the HTML `maxlength="255"` attribute — both client-side. Any non-browser caller (curl, the `.http` file, a future client) can push an arbitrarily long name straight into the Search index.
- **Fix**: Add a length check alongside the existing whitespace check in the `MapPut` handler (`Results.ValidationProblem` on `request.FileName.Length > 255`), matching the same defense-in-depth reasoning the plan already applied to emptiness.
- **Decision**: FIXED — added `MaxFileNameLength = 255` check in Program.cs's `MapPut` handler returning `ValidationProblem`; added `Rename_with_a_name_over_255_characters_is_400_with_no_merge` test to ReceiptRenameTests.cs.

### F3 — Validation failure on rename isn't logged

- **Severity**: ℹ️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Web/Program.cs:257-264
- **Detail**: `ReceiptConfirmService` logs every validation rejection via a source-generated `LogValidationFailure` before returning `InvalidBlob` (e.g. ReceiptConfirmService.cs:61,68,74,80). The rename endpoint's empty/whitespace 400 path has no log call at all, an observability gap relative to this codebase's own convention.
- **Fix**: Add an Info-level log call before returning `ValidationProblem` (e.g. via a `[LoggerMessage]` on the endpoint logger), same shape as `LogValidationFailure`.
- **Decision**: FIXED — added `endpointLogger.LogInformation(...)` calls on both the empty-name and over-length validation branches in Program.cs, matching the existing endpoint-level logging style used by the catch-all `LogError` in the same handler.

## Notes

- All 8 planned files/changes verified MATCH via dual sub-agent review (drift-detection + safety/pattern agents): ReceiptRenameService.cs, Program.cs (MapPut + DI), receipt-well.http, ReceiptRenameTests.cs, receipt.service.ts, list.component.ts, list.component.html, list.component.spec.ts.
- Success criteria all green: `dotnet build` (0 warnings), `dotnet test` (39 passed / 1 skipped, unrelated), `npm run build`, `npm run lint`, `npm test` (30 passed). The build's pre-existing initial-bundle budget warning is unrelated to this feature (it's the eagerly-loaded main chunk, not the lazily-loaded list-component chunk this feature touches).
- The known partial-merge bug (clobbering `UserId` via full-POCO merge into Azure AI Search) was already caught during manual testing and fixed in commit 975ebba with a dedicated `ReceiptRenameDocument` DTO — confirmed correct now, and recorded in `context/foundation/lessons.md`. Not re-flagged.
- Focus management (input focus on edit-start, pencil-button focus on exit) is properly implemented via `effect()`s, satisfying the plan's a11y requirement.
