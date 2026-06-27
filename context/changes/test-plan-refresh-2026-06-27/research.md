---
date: 2026-06-27T00:00:00+02:00
researcher: Krzysztof Dudzik
git_commit: 4d8acb1c1eb4fa76ec81f1c5c234a82be3862115
branch: develop
repository: receipt-well
topic: "What changed since the initial test plan and what needs updating"
tags: [research, codebase, test-plan, tag-search, hooks, quality-gates]
status: complete
last_updated: 2026-06-27
last_updated_by: Krzysztof Dudzik
---

# Research: What changed since the initial test plan and what needs updating

**Date**: 2026-06-27
**Researcher**: Krzysztof Dudzik
**Git Commit**: 4d8acb1c1eb4fa76ec81f1c5c234a82be3862115
**Branch**: develop
**Repository**: receipt-well

## Research Question

Some changes were made on a feature branch. Tag-search has been implemented with a TDD approach, and Claude agent hooks and git hooks were implemented. Verify what has changed since the initial test plan and suggest what needs updating.

## Summary

Four concrete updates are needed in `context/foundation/test-plan.md`:

1. **§5 Quality Gates** — Replace the placeholder "post-edit hook (run affected tests)" row with the three actual agent hooks (C# build, csproj restore, frontend lint+typecheck) and a new pre-commit row (lefthook backend test suite). These are what is running today; the description does not match the implementation.

2. **§3 Phase 4** — Annotate that Risk #6's search-side behavior (routing `?q=` through the endpoint, querying against `TagsPl`, user scoping on the search path, and 200-not-400 for zero matches) is already covered by tag-search TDD tests. What remains for Phase 4 is Risk #5 (async extraction, poison path) and Risk #6's normalization unit test (a `TagNormalizer` unit test with a requirement-derived oracle).

3. **§6.6 Phase notes** — Add an out-of-band note capturing what the tag-search + hooks work taught: the wildcard/QueryType.Full trap and the per-edit vs. commit layering decision.

4. **§8 Freshness Ledger** — Update "Strategy last reviewed" from 2026-06-23 to 2026-06-27.

---

## Detailed Findings

### A. Tag-search implementation (Risk #6 — partial coverage delivered out-of-phase)

The tag-search feature (change-id: `tag-search`, status: `impl_reviewed`) was implemented TDD across two phases and merged via PR #7.

#### What was built

**Backend (Phase 1 — commit `14634f9`, fix `0035c81`):**
- `src/backend/ReceiptWell.Core/ReceiptDocument.cs` — new `TagsPl` property with `[SearchableField(AnalyzerName = LexicalAnalyzerName.Values.PlMicrosoft)]`
- `src/backend/ReceiptWell.Functions/ReceiptStore.cs` — `SetReadyAsync` now populates `TagsPl` alongside `Tags`
- `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs` — `GetReceiptsAsync(userId, query?, ct)` routes blank/null to `searchText="*"` (browse) and non-blank to `searchText=term` + `SearchFields=["TagsPl"]` (search)
- `src/backend/ReceiptWell.Web/Program.cs` — `GET /receipts` accepts optional `?q=` and forwards to query service
- `src/backend/ReceiptWell.Tests/BackfillTagsPl.cs` — throwaway one-time backfill utility (lives in test project)

**Frontend (Phase 2 — commit `784b159`):**
- `src/frontend/src/app/receipts/receipt.service.ts` — `getReceipts(query?)` appends `?q=` via `HttpParams`
- `src/frontend/src/app/receipts/list/list.component.ts` — search term as signal, debounced 300ms, poll carries active term, `clearSearch()` bypasses debounce
- `src/frontend/src/app/receipts/list/list.component.html` — search input above list, `@if` no-match branch distinct from "no receipts yet"

**Critical fix (commit `0035c81`):** Initial P1 used `QueryType.Full` + `"rower*"` wildcard, which disables the `pl.microsoft` analyzer at query time (per `lessons.md` entry added from this discovery). Reverted to plain `searchText = term` with default `QueryType.Simple` so the field's analyzer runs at both index and query time.

#### New test files

| File | Lines | Scope |
|------|-------|-------|
| `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs` | 97 | Unit: term → `TagsPl` + scoping; blank variants → browse-all with `OrderBy UploadedAt desc` |
| `src/backend/ReceiptWell.Tests/ReceiptSearchEndpointTests.cs` | 66 | Integration (WebFactory): `?q=rower` routes through correctly; zero-match → 200 not 400 |
| `src/frontend/src/app/receipts/list/list.component.spec.ts` | 278 (extended) | Component: 5 new search specs — debounce 300ms, no-match state, clear via input, `clearSearch()` immediate, poll carries term |

`ReceiptQueryScopingTests.cs` (pre-existing, Risk #1) was not changed by tag-search.

#### What Risk #6 coverage now exists vs. what was planned

The test plan's Phase 4 goal for Risk #6 was: *"tags normalize to PL"* — standing for two sub-risks:
- **6a. Normalization** (AI + `TagNormalizer` produces Polish output) — **still NOT tested by any automated test**. `TagNormalizer` has no unit test. The requirement-derived oracle approach (assert "bike"/"bicycle" → "rower") described in Risk Response Guidance §2 has not been written.
- **6b. Search retrieval** (Polish-stemmed query matches indexed tags) — **NOW covered** by `ReceiptSearchTests` and `ReceiptSearchEndpointTests` at the structural level (correct field, correct scoping, no 400). Actual `pl.microsoft` lemmatization is validated manually (Phase 1.6 in `tag-search/plan.md`).

---

### B. Agent hooks (PostToolUse — per-edit)

**Configuration:** `.claude/settings.json` — `PostToolUse` on matcher `Write|Edit`, three sequential hooks.

#### Hook 1: C# build check (`check_cs_build.py`)
- **Fires on:** `.cs` files only (exits 0 silently on other file types)
- **What it runs:** `dotnet build <nearest .csproj> --no-restore`
- **Timeout:** 30 s
- **Failure signal:** exit 2 → feeds compilation error back into agent context

#### Hook 2: csproj restore check (`check_csproj_restore.py`)
- **Fires on:** `.csproj` files only
- **What it runs:** `dotnet restore src/backend/ReceiptWell.sln`
- **Timeout:** 60 s
- **Failure signal:** exit 2

#### Hook 3: Frontend lint + typecheck (`check_frontend_lint.py`)
- **Fires on:** `.ts` or `.html` files that contain "frontend" in their path
- **What it runs:** (1) `npx eslint <file>` → then (2) `npx tsc --noEmit -p tsconfig.app.json`
- **Timeout:** 60 s
- **Failure signal:** exit 2 on either check; diagnostic output distinguishes lint vs. type error

**Architectural choice:** Per-edit hooks run **compilation + lint, NOT tests**. This is deliberate — `dotnet test` takes > a few seconds so it was moved to the commit gate (see CLAUDE.md principle: "keep per-edit hooks fast"). The test plan's §5 row "post-edit hook (run affected tests)" does not match this reality.

---

### C. Git hooks (pre-commit via lefthook)

**Configuration:** `lefthook.yml` at repo root.

```yaml
pre-commit:
  commands:
    backend-tests:
      run: dotnet test src/backend/ReceiptWell.sln
```

- **Installed:** Yes — `.git/hooks/pre-commit` exists, calls lefthook, which locates the binary (WinGet install path)
- **Scope:** Full backend test suite, **no glob filter** — runs on every commit regardless of which files changed
- **Frontend:** Not gated at commit. Frontend is only checked per-edit (agent hook) and in CI.
- **Gap:** Running the full suite on every commit is high-friction when only docs/infra/frontend files changed. A `glob: "src/backend/**"` filter could scope it. (Low priority — document, not fix, here.)

**Hooks work was done inside the tag-search change** (no dedicated change folder). Commits `4735430` (repair Write|Edit hooks), `2cb0840` (frontend linter hook), and `e1688ef` (lefthook.yml + plan-brief) are all on that branch.

---

### D. New lesson in lessons.md

The impl-review for tag-search surfaced and committed a new lesson entry (the last entry in `context/foundation/lessons.md`):

> **Azure AI Search: wildcard queries bypass the field's language analyzer** — use plain `searchText = term` with `QueryType.Simple`; `QueryType.Full` + `*` wildcard disables analyzer at query time.

This is already in `lessons.md`. It is also relevant to §6.2 cookbook pattern guidance (any future test that checks `SearchOptions` should assert the absence of wildcards and `QueryType.Full`).

---

## Code References

- `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs:22` — `GetReceiptsAsync_with_a_term_searches_TagsPl_and_stays_scoped_to_the_caller`
- `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs:56` — `GetReceiptsAsync_with_a_blank_term_browses_all_newest_first` (Theory: null, "", "   ")
- `src/backend/ReceiptWell.Tests/ReceiptSearchEndpointTests.cs:19` — `GET_receipts_with_q_forwards_the_term_to_Search_scoped_to_the_caller`
- `src/backend/ReceiptWell.Tests/ReceiptSearchEndpointTests.cs:40` — `GET_receipts_with_zero_matches_returns_an_empty_200_not_400`
- `src/frontend/src/app/receipts/list/list.component.spec.ts:207` — debounce 300ms test
- `src/frontend/src/app/receipts/list/list.component.spec.ts:221` — no-match panel state
- `src/frontend/src/app/receipts/list/list.component.spec.ts:248` — `clearSearch()` bypasses debounce
- `src/frontend/src/app/receipts/list/list.component.spec.ts:265` — background poll carries active term
- `.claude/settings.json:1-29` — PostToolUse hook configuration
- `.claude/check_cs_build.py` — C# build gate (fires on `.cs`, finds nearest `.csproj`, exit 2 on failure)
- `.claude/check_csproj_restore.py` — restore gate (fires on `.csproj`, exit 2 on failure)
- `.claude/check_frontend_lint.py` — ESLint + tsc gate (fires on `.ts`/`.html` in frontend, exit 2 on either)
- `lefthook.yml:1-5` — pre-commit full backend test suite

## Architecture Insights

**Layering decision made:** The team chose compile+lint per-edit and test at commit — matching the CLAUDE.md lesson ("keep per-edit hooks fast"). Tests are not run per-edit because `dotnet test` is too slow for the agent loop. This is a deliberate tradeoff, not an omission.

**Risk #6 split:** The tag-search implementation covers the "search retrieval" half of Risk #6 with automated tests (structural: correct field, scoping, HTTP contract). The "normalization" half (does the AI produce Polish tags? does `TagNormalizer` work correctly?) has zero automated coverage and remains an obligation for Phase 4.

**Requirement-derived oracle still holds:** `ReceiptSearchTests` asserts `filter.Contains("UserId")` + `filter.Contains(callerId)`, never an exact OData string. The pattern is consistent with the Phase 1 and Phase 2 test cookbook patterns.

## Historical Context (from prior changes)

- `context/changes/tag-search/plan.md` — Full plan including the Phase 4 search testing contract. The impl-review findings (QueryType.Full bug, test assertions corrected) are logged in `context/changes/tag-search/reviews/`.
- `context/changes/testing-access-control/` — Phase 1 of the test rollout (complete); established `ReceiptWellWebFactory`, `TestAuthHandler`, cookbook patterns now in §6.1/§6.2.
- `context/changes/testing-infra-boundary-failure/` — Phase 2 of the test rollout (complete); established `ConfirmFlowHarness`, `ProblemDetailsAssertions`, §6.2 infra-failure patterns.

## Related Research

- `context/changes/tag-search/plan.md` — Implementation plan with progress tracking for tag-search
- `context/foundation/test-plan.md` — The document this research is feeding into

## Open Questions

1. **TagNormalizer unit test** — Should Phase 4 open with a research step specifically on `TagNormalizer.cs` to understand its current behavior and write a requirement-derived oracle? The plan currently treats it as a combined "extraction + business rules" phase; the normalization test may want its own sub-task.

2. **lefthook glob filter** — Should the pre-commit hook be scoped to `src/backend/**` to avoid running `dotnet test` on pure frontend/docs/infra commits? Low friction to add; worth noting in the test plan as a known gap.

3. **Frontend commit gate** — There is no frontend gate at commit time — only per-edit ESLint+tsc and CI. Is this acceptable until Phase 5, or should `lefthook.yml` gain a `frontend-lint` command now?

4. **Phase 4 scoping after tag-search** — Phase 4's original scope was #5 + #6 together. Now that #6's search-side is done, is Phase 4 worth splitting: 4a = Risk #5 (async/poison path), 4b = Risk #6 normalization unit test? Or keep combined given #6 normalization is a small addition?

---

## Recommended test-plan.md Updates (summary for /10x-plan)

| Section | What to change |
|---------|---------------|
| §3 Phase 4 | Annotate that Risk #6 search-side behavior is already covered by `ReceiptSearchTests` + `ReceiptSearchEndpointTests` (tag-search TDD). Remaining: Risk #5 (async extraction + poison) and Risk #6 normalization unit test (`TagNormalizer` with requirement-derived oracle). |
| §5 Quality Gates | Replace "post-edit hook (run affected tests) → recommended" with three accurate rows: (1) per-edit C# build check (required), (2) per-edit frontend lint+typecheck (required), (3) per-edit csproj restore (required). Add a new row: pre-commit backend test suite via lefthook (required). |
| §6.6 Phase notes | Add an out-of-band "Tag-search + hooks (2026-06-27)" note: wildcard/QueryType.Full trap on language-analyzer fields (→ lessons.md); hooks layer: 3 per-edit checks + lefthook pre-commit for tests; Risk #6 search-side now automated, normalization remains for Phase 4. |
| §8 Freshness Ledger | Update "Strategy last reviewed" to 2026-06-27. |
