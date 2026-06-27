<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Tag Search Implementation Plan

- **Plan**: context/changes/tag-search/plan.md
- **Scope**: Phase 1 + Phase 2 (all phases)
- **Date**: 2026-06-27
- **Verdict**: REJECTED → FIXED (all findings resolved during triage)
- **Findings**: 1 critical | 1 warning | 5 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | FAIL → FIXED |
| Scope Discipline | PASS |
| Safety & Quality | PASS |
| Architecture | PASS |
| Pattern Consistency | WARNING → FIXED |
| Success Criteria | WARNING → FIXED |

## Findings

### F1 — QueryType.Full + wildcard disables pl.microsoft lemmatization

- **Severity**: ❌ CRITICAL
- **Impact**: 🔬 HIGH — architectural stakes; think carefully before deciding
- **Dimension**: Plan Adherence
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs
- **Detail**: Implementation appended `+ "*"` to the search term and set `QueryType = SearchQueryType.Full` (Lucene syntax). Azure AI Search does NOT run the field's language analyzer on wildcard queries — the query token is matched literally as a prefix. So "rowery*" never hit the indexed lemma "rower". This is why the user reported "only exact match". The fix: remove `+ "*"` and `QueryType.Full`. A plain `searchText = term` with default `QueryType.Simple` causes Azure AI Search to apply `pl.microsoft` at query time — "rowery" → "rower" → matches indexed lemma "rower".
- **Decision**: FIXED — removed `+ "*"` from searchText and `QueryType = SearchQueryType.Full` from ReceiptQueryService.cs.

### F2 — Tests assert the wrong behavior (prefix, not stemming)

- **Severity**: ⚠️ WARNING
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Success Criteria
- **Location**: src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs + ReceiptSearchEndpointTests.cs
- **Detail**: Tests faithfully mirrored the drift: `capturedText == term + "*"` and `QueryType.Full`. After F1 fix these needed updating to assert `capturedText == term` and remove the `QueryType.Full` assertion.
- **Decision**: FIXED — updated capturedText assertions to `term` (no asterisk), removed `QueryType.Full` assertions in both unit and endpoint tests.

### F3 — Pre-existing lint error in app.spec.ts

- **Severity**: OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Success Criteria
- **Location**: src/frontend/src/app/app.spec.ts:10
- **Detail**: `ng lint` failed on "Unexpected empty method 'setActiveAccount'" (@typescript-eslint/no-empty-function). Not from tag-search (last touched in commit a3765e1). Blocked the 2.2 lint success criterion.
- **Decision**: FIXED — added `// eslint-disable-next-line @typescript-eslint/no-empty-function` above the method.

### F4 — OData filter uses string interpolation on userId

- **Severity**: OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:17
- **Detail**: `Filter = $"UserId eq '{userId}'"` — safe in practice (userId is always a GUID). Risk only if pattern is copied with user-controlled string input.
- **Decision**: SKIPPED — safe as-is; userId is always an OID.

### F5 — EmptyResults() helper duplicated across two test files

- **Severity**: OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: ReceiptQueryScopingTests.cs:47 / ReceiptSearchTests.cs:88
- **Detail**: Identical `EmptyResults()` factory in both files. ReceiptSearchTests already marks it `internal static`.
- **Decision**: SKIPPED

### F6 — Two top-level test classes in one file

- **Severity**: OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs
- **Detail**: `ReceiptSearchTests` (unit) and `ReceiptSearchEndpointTests` (integration) shared a file; one-class-per-file convention broken.
- **Decision**: FIXED — extracted `ReceiptSearchEndpointTests` into `ReceiptSearchEndpointTests.cs`.

### F7 — Blank-term test missing "UserId" field-name assertion

- **Severity**: OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Success Criteria
- **Location**: ReceiptSearchTests.cs (blank-term unit test)
- **Detail**: Blank-term test asserted `callerId` in filter but not `"UserId"` field name. Asymmetric with search-term test.
- **Decision**: FIXED — added `Assert.Contains("UserId", capturedOptions.Filter)` to blank-term unit test.
