# Combined Linguistic + Prefix Tag Search Implementation Plan

## Overview

Restore prefix (as-you-type) tag matching without losing the linguistic/lemmatized
matching that `tag-search` introduced. Today `ReceiptQueryService.GetReceiptsAsync`
queries only the Polish-stemmed `TagsPl` field with a plain analyzed term, so "rower"
matches "rowery" (lemma) but nothing matches until a full word is typed (no prefix).
We rewrite the single search branch into **one `QueryType.Full` query** that unions a
prefix clause on the raw `Tags` field with a boosted analyzed clause on `TagsPl`, so
both behaviors coexist in one relevance-ranked round trip. To keep 1-character prefixes
from returning the whole catalog mid-typing, the frontend also gains a **2-character
minimum** before a search fires (empty still reverts to browse-all).

## Current State Analysis

- `ReceiptQueryService.GetReceiptsAsync` (`src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:9-50`)
  is the entire search surface. For a non-blank term it sets
  `options.SearchFields.Add("TagsPl")` and sends `searchText = term` (Simple query type,
  the default). The `pl.microsoft` analyzer lemmatizes both index and query tokens →
  inflection works, prefix does not.
- The prior `tag-search` build used `term + "*"` with `QueryType.Full` (prefix, no lemma);
  its impl-review swapped it for the plain analyzed term (lemma, no prefix) to fix a
  "only exact match" regression. This change deliberately reopens that tradeoff — now with
  both fields queried at once.
- Both index fields already exist and are populated from the same normalized tag list
  (`src/backend/ReceiptWell.Core/ReceiptDocument.cs:35,43-44`):
  - `Tags` — `[SearchableField(IsFilterable = true)]`, standard analyzer → raw surface
    forms (`rower`, `rowery` stored as-is). Correct target for prefix.
  - `TagsPl` — `pl.microsoft` analyzer → lemmas (`rowery` stored as `rower`). Correct
    target for whole-word inflection. Search-only; not the display source.
- Unit contract at `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs:21-83` asserts the
  current lemma-only shape (`searchText == term`, `SearchFields` contains `TagsPl`). This
  is the guardrail that caught the original regression (F1) and must be updated to the new
  contract.
- The load-bearing constraint (`context/foundation/lessons.md:54-59`): a Lucene **wildcard**
  term bypasses the field analyzer, but a **plain** term in the same `QueryType.Full` query
  *is* analyzed. So one Full query can carry an analyzed clause and a wildcard clause side
  by side — the basis for Option A.
- Frontend (`src/frontend/src/app/receipts/list/list.component.ts`) already debounces the
  search box 300ms and threads the term through every fetch including background polls
  (`list.component.ts:99-105,170-175`). Its `valueChanges` subscription today fires a fetch
  for **any** value change, including a single character — there is no minimum-length gate.
  The combined query itself needs no frontend change; the 2-char minimum is an added gate in
  this same subscription. Its Vitest suite has a dedicated `ReceiptListComponent search`
  describe block (`list.component.spec.ts:190-280`) that is the home for the gate's test.

## Desired End State

Typing into the receipts search box returns results that combine both matching styles in a
single query, ranked with precise lemma matches above noisy prefix matches:

- Type `rowe` → `Tags:rowe*` matches both `rower` and `rowery` (as-you-type prefix).
- Type `rowery` → `TagsPl:rowery` lemmatizes to `rower` and matches a `rower`-tagged receipt
  that prefix alone would miss (e.g. `myszy` → lemma `mysz`, where `myszy*` would not match
  the stored `mysz`).
- When a document matches both ways, the lemma/exact hit ranks above the prefix hit.
- A search fires only once the trimmed term reaches **2 characters**. A single character
  triggers no fetch (the current view is held); clearing the box back to empty still reverts
  to browse-all.
- Browse-all (blank term) is unchanged: `searchText = "*"`, `OrderBy UploadedAt desc`.
- Every query — search and browse — stays scoped to `UserId eq '{userId}'`.

**Verification of end state:** unit tests assert the new query string shape and `QueryType.Full`;
a mandatory live `?q=rowery` (lemma still works) and `?q=rowe` (prefix now works) check against
the real Search index confirms the analyzer-under-Full assumption the mocks cannot prove.

### Key Discoveries:

- One `QueryType.Full` query can mix an analyzed plain clause and a wildcard clause —
  `context/foundation/lessons.md:54-59`, research `research.md:84-86`.
- Prefix must target `Tags`, not `TagsPl`: a `rowery*` prefix would miss the stored lemma
  `rower` on `TagsPl` (`ReceiptDocument.cs:43-44`, research `research.md:88-101`).
- Both fields already populated — no migration, no index rebuild (`research.md:104-113`).
- Field scoping moves from `options.SearchFields` into the query string; the `UserId` filter
  must stay on `options.Filter` and never touch the search term.
- Tests must be requirement-derived, not exact-string copies (backend `CLAUDE.md` testing
  rules; existing test doc comment `ReceiptSearchTests.cs:10-18`).

## What We're NOT Doing

- No index schema change, no new field, no custom analyzer (edge n-gram / Option C), no
  index rebuild or backfill — both fields already exist and are populated.
- No suggester / Autocomplete API and no autocomplete-dropdown UX (Option D) — this combines
  the two existing methods; it does not change the list-filtering interaction model.
- No two-call merge (Option B) — Option A chosen for a single ranked result set with boosting.
- No template/HTML/SCSS change and no change to the debounce or term-through-poll wiring —
  the only frontend edit is adding a length gate inside the existing `valueChanges` pipe.
- No server-side minimum-length gate — the 2-char minimum is a frontend UX concern; the
  backend query stays correct for any term length if called directly.
- No new integration/E2E test harness — no local Search emulator exists; the guard is
  unit-contract + a manual live check.
- No change to the browse-all (blank term) branch beyond leaving it exactly as-is.

## Implementation Approach

Rewrite only the search branch of `GetReceiptsAsync`. For a non-blank term:

1. **Escape** the trimmed term against Lucene special characters
   (`+ - && || ! ( ) { } [ ] ^ " ~ * ? : \ /`) before it enters the query string, so user
   input can never inject query syntax.
2. Build the combined search text: an analyzed, boosted lemma clause OR a prefix clause,
   e.g. `TagsPl:{escaped}^3 OR Tags:{escaped}*`. The `^3` boost ranks lemma/exact hits above
   prefix hits; the `TagsPl:{escaped}` clause is plain (no wildcard) so it stays analyzed
   under Full; the `Tags:{escaped}*` clause is the prefix.
3. Set `options.QueryType = SearchQueryType.Full`. Do **not** add `SearchFields` — field
   scoping now lives in the query string.
4. Leave `Filter`, `Size`, and the browse-all branch untouched.

Then update the unit contract to assert this new shape and add the live verification step to
the phase's manual gate.

## Critical Implementation Details

- **Analyzer-under-Full is the load-bearing, mock-invisible assumption.** NSubstitute captures
  the query string but never runs `pl.microsoft`. The unit tests prove the *right query was
  sent*; only the live `?q=` check proves lemmatization still fires under `QueryType.Full`.
  This is exactly the gap through which the original regression slipped — the live check is
  mandatory, not optional.
- **Escaping ordering:** escape the term *before* interpolating clauses, and append the `*`
  and `^3` operators *after* escaping — otherwise the escape step would neutralize the very
  wildcard/boost operators the query depends on.

## Phase 1: Combined boosted query + contract tests

### Overview

Rewrite the search branch of `GetReceiptsAsync` to emit one escaped, boosted `QueryType.Full`
query across `Tags` (prefix) and `TagsPl` (lemma), and update `ReceiptSearchTests` to lock the
new contract while preserving the browse-all and caller-scoping guarantees.

### Changes Required:

#### 1. Combined query construction

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs`

**Intent**: Replace the lemma-only search branch (`SearchFields.Add("TagsPl")` +
`searchText = term`) with a single combined query so prefix and inflection both work. Escape
the user term, build a boosted OR of an analyzed `TagsPl` clause and a prefix `Tags` clause,
and switch the query type to `Full`. The browse-all branch and the `UserId` filter are
untouched.

**Contract**: For a non-blank term, `searchText` becomes `TagsPl:{escaped}^3 OR Tags:{escaped}*`
and `options.QueryType = SearchQueryType.Full`; `options.SearchFields` is no longer set on the
search path. `{escaped}` is the trimmed term with Lucene special characters
(`+ - && || ! ( ) { } [ ] ^ " ~ * ? : \ /`) escaped *before* the `*`/`^3` operators are
appended. Blank term stays `searchText = "*"` with `OrderBy UploadedAt desc` and no
`SearchFields`. `Filter = "UserId eq '{userId}'"` and `Size = 1000` unchanged on both paths.

#### 2. Contract tests for the combined query

**File**: `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs`

**Intent**: Update the term-search test so it asserts the new combined query shape instead of
the retired lemma-only shape, keeping assertions requirement-derived (substring checks, never
an exact-string copy). Leave the blank-term browse-all test unchanged. Update the class summary
comment to describe the combined contract.

**Contract**: The term test asserts `capturedText` contains a `TagsPl:` clause, a `Tags:`
clause, the `*` prefix operator, and the `^3` boost; asserts `capturedOptions.QueryType ==
SearchQueryType.Full`; asserts `SearchFields` is empty on the search path; and retains the
`UserId` + `callerId` filter-scoping assertions. The three `[InlineData(null/""/"   ")]`
browse-all cases keep asserting `searchText == "*"`, `UploadedAt desc`, empty `SearchFields`,
and caller scoping.

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- Unit tests pass: `dotnet test src/backend/ReceiptWell.sln`
- The updated term test asserts both clauses (`TagsPl:`, `Tags:`), the `*` prefix operator,
  the `^3` boost, and `QueryType.Full`
- The browse-all test still passes unchanged (`searchText == "*"`, `UploadedAt desc`, empty
  `SearchFields`, caller scoping)

#### Manual Verification:

- Against the live Search index, `?q=rowery` still returns the `rower`-tagged receipt
  (lemmatization confirmed under `QueryType.Full`)
- `?q=rowe` returns both `rower` and `rowery` receipts (prefix as-you-type confirmed)
- A term containing a Lucene special character (e.g. `?q=a+b` or a stray `:`) does not error
  and returns a sensible/empty result (escaping confirmed)
- When a receipt matches both ways, the lemma/exact hit ranks at or above prefix-only hits
  (boost confirmed)
- No regression in browse-all: blank search lists all receipts newest-first, scoped to the
  caller

**Implementation Note**: After completing this phase and all automated verification passes,
pause here for manual confirmation from the human that the live-index testing was successful
before considering the change done. The manual `?q=` checks are the mandatory guard against
re-drifting — they exercise the `pl.microsoft` analyzer that the mocks cannot.

---

## Phase 2: Frontend 2-character minimum search gate

### Overview

Add a minimum-length gate to the search box so a search fires only once the trimmed term
reaches 2 characters, damping the broad result sets a 1-character prefix would return
mid-typing. The gate lives inside the existing `valueChanges` subscription — no template,
debounce, or poll-wiring change.

### Changes Required:

#### 1. Minimum-length gate in the search subscription

**File**: `src/frontend/src/app/receipts/list/list.component.ts`

**Intent**: Introduce a `MIN_SEARCH_LENGTH = 2` constant (alongside `SEARCH_DEBOUNCE_MS`) and
gate the `searchControl.valueChanges` handler so it proceeds only when the trimmed term is
empty (revert to browse-all) or at least 2 characters. A trimmed length of exactly 1 is a
no-op: neither the `query` signal nor a fetch is updated, so the current view is held and no
1-char search reaches the backend or the background poll.

**Contract**: In the `ngOnInit` subscription pipe (`list.component.ts:99-105`), the debounced
handler acts only when `term.trim().length === 0 || term.trim().length >= MIN_SEARCH_LENGTH`;
a trimmed length of 1 returns early without touching `query` or calling `refreshReceipts()`.
`debounceTime(SEARCH_DEBOUNCE_MS)`, `distinctUntilChanged()`, `clearSearch()`, and `runFetch`'s
term-through-poll behavior are unchanged.

#### 2. Gate unit tests

**File**: `src/frontend/src/app/receipts/list/list.component.spec.ts`

**Intent**: Extend the `ReceiptListComponent search` describe block with cases proving the gate:
a 1-character term fires no fetch after the debounce, and a 2-character term does. Keep the
existing search tests (300ms debounce, no-match panel, clear, poll-carries-term) passing —
update any that set a 1-char term to use ≥2 chars so they still exercise a real search.

**Contract**: New tests assert that after `searchControl.setValue('r')` + 300ms advance,
`getReceipts` is not called; after `searchControl.setValue('ro')` + 300ms, `getReceipts` is
called with `'ro'`. Existing terms in the block (`'rower'`, `'xyz'`) already satisfy ≥2 chars
and need no change.

### Success Criteria:

#### Automated Verification:

- Frontend unit tests pass: `npm test` (in `src/frontend/`)
- A 1-character term fires no `getReceipts` call after the debounce window
- A 2-character term fires `getReceipts` with that term after 300ms
- Clearing to empty still fetches browse-all (`getReceipts(undefined)`) — existing tests green

#### Manual Verification:

- Typing a single character shows no new results (current view held); typing the 2nd character
  triggers the search
- Clearing the box reverts to browse-all newest-first
- Deleting from ≥2 chars down to 1 does not error and leaves the last results in place

**Implementation Note**: After completing this phase and all automated verification passes,
pause here for manual confirmation from the human that the search box feels right at the 1→2
character boundary before considering the change done.

---

## Testing Strategy

### Unit Tests:

- **Backend** — term search sends the combined boosted Full query (both clauses, `*`, `^3`,
  `QueryType.Full`); stays scoped to the caller (`UserId` + `callerId` in `Filter`); search
  path no longer sets `SearchFields`; blank/whitespace term browses all, newest-first, unchanged.
- **Frontend** — a 1-char term fires no fetch after the debounce; a 2-char term fires
  `getReceipts` with that term; clear-to-empty still fetches browse-all.

### Integration Tests:

- None automated (no local Search emulator). Covered by manual live-index verification below.

### Manual Testing Steps:

1. Start the backend against the real Search index and the frontend via `npm run start:local`.
2. Query `?q=rowery` → the `rower`-tagged receipt appears (lemma under Full).
3. Query `?q=rowe` → both `rower` and `rowery` receipts appear (prefix).
4. Query a term with a Lucene special char (e.g. `a+b`, `foo:bar`) → no error, sensible result.
5. Confirm a doc matching both ways ranks lemma/exact at or above prefix.
6. In the UI, type a single character → no search fires; type the 2nd character → search fires.
7. Clear the search → browse-all lists everything newest-first, scoped to the caller.

## Performance Considerations

Single round trip (Option A), unchanged from today's one-call search. QPS is low (PRD
`target_scale.qps: low`), the frontend already debounces 300ms, and `Size = 1000` is unchanged.
The prefix clause fires from the 2nd character onward; the frontend gate suppresses the
broadest 1-char prefix and one round trip per first keystroke.

## Migration Notes

None. Both `Tags` and `TagsPl` already exist on the index and are populated from the same
normalized tag list on enrichment. No schema change, no rebuild, no backfill.

## References

- Related research: `context/changes/combined-search/research.md`
- Core constraint (lesson): `context/foundation/lessons.md:54-59`
- Method to change: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:21-31`
- Fields: `src/backend/ReceiptWell.Core/ReceiptDocument.cs:35,43-44`
- Contract tests (guardrail): `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs:21-83`
- Frontend search gate + tests: `src/frontend/src/app/receipts/list/list.component.ts:99-105`,
  `src/frontend/src/app/receipts/list/list.component.spec.ts:190-280`
- Prior regression + fix: `context/archive/2026-06-24-tag-search/reviews/impl-review.md:23-39`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Combined boosted query + contract tests

#### Automated

- [x] 1.1 Solution builds with zero warnings (`dotnet build src/backend/ReceiptWell.sln`)
- [x] 1.2 Unit tests pass (`dotnet test src/backend/ReceiptWell.sln`)
- [x] 1.3 Term test asserts both clauses (`TagsPl:`, `Tags:`), `*` prefix, `^3` boost, and `QueryType.Full`
- [x] 1.4 Browse-all test still passes unchanged (`*`, `UploadedAt desc`, empty `SearchFields`, caller scoping)

#### Manual

- [x] 1.5 Live `?q=rowery` still returns the `rower`-tagged receipt (lemma under Full)
- [x] 1.6 Live `?q=rowe` returns both `rower` and `rowery` receipts (prefix)
- [x] 1.7 Term with a Lucene special char does not error (escaping)
- [x] 1.8 Doc matching both ways ranks lemma/exact at or above prefix (boost)
- [x] 1.9 Browse-all unchanged: blank search lists all newest-first, scoped to caller

### Phase 2: Frontend 2-character minimum search gate

#### Automated

- [x] 2.1 Frontend unit tests pass (`npm test` in `src/frontend/`)
- [x] 2.2 A 1-character term fires no `getReceipts` call after the debounce window
- [x] 2.3 A 2-character term fires `getReceipts` with that term after 300ms
- [x] 2.4 Clearing to empty still fetches browse-all (`getReceipts(undefined)`)

#### Manual

- [ ] 2.5 Single character shows no new results; the 2nd character triggers the search
- [ ] 2.6 Clearing the box reverts to browse-all newest-first
- [ ] 2.7 Deleting from ≥2 chars down to 1 does not error and holds the last results
