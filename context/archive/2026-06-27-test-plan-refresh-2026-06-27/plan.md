# Test Plan Refresh 2026-06-27 — Implementation Plan

## Overview

Update `context/foundation/test-plan.md` with four targeted edits that bring the document in sync with what shipped since the initial plan: tag-search TDD tests (out-of-phase Risk #6 partial coverage), three per-edit agent hooks, a lefthook pre-commit gate, and a freshness date bump.

## Current State Analysis

The test plan was last reviewed 2026-06-23. Two changes shipped since then:

1. **Tag-search (PR #7, commit `4d8acb1`)** — added `ReceiptSearchTests.cs`, `ReceiptSearchEndpointTests.cs`, and 5 new frontend specs. This covers Risk #6's search-retrieval half (correct field, scoping, HTTP contract). The normalization half (`TagNormalizer` unit test with requirement-derived oracle) has zero automated coverage and remains a Phase 4 obligation.
2. **Agent hooks + lefthook** — three `PostToolUse` per-edit checks (`.claude/check_cs_build.py`, `.claude/check_frontend_lint.py`, `.claude/check_csproj_restore.py`) and `lefthook.yml` pre-commit backend test suite are running. §5 Quality Gates currently describes "post-edit hook (run affected tests) → recommended" which is inaccurate: per-edit hooks run build/lint only; tests run at commit via lefthook.

### Key Discoveries

- `§5` row "post-edit hook (run affected tests)" misrepresents the implementation — hooks run compile+lint, not tests (research §B)
- `ReceiptSearchTests.cs` + `ReceiptSearchEndpointTests.cs` cover Risk #6 search-retrieval; `TagNormalizer` has zero automated tests (research §A, "6a. Normalization")
- `lefthook.yml` runs the full backend suite on every commit with no glob filter — a known gap, low priority; document, don't fix here
- Frontend has no commit gate — per-edit ESLint+tsc and CI only; deliberate until Phase 5

## Desired End State

`context/foundation/test-plan.md` accurately reflects the current test infrastructure and coverage:
- §3 Phase 4 notes which Risk #6 sub-risks are already covered and what remains
- §5 Quality Gates lists the actual hooks running (3 per-edit + 1 pre-commit)
- §6.6 captures what tag-search + hooks work taught
- §8 date updated to 2026-06-27

Verify by diffing the four target sections against the research summary table; no other sections should change.

### Key Discoveries

- Research doc `context/changes/test-plan-refresh-2026-06-27/research.md` maps every edit precisely — no codebase spelunking needed
- `context/foundation/test-plan.md` is a standalone markdown file with stable section headings; edits are surgical

## What We're NOT Doing

- Fixing the lefthook glob filter (document the gap, no code change)
- Adding a frontend commit gate (deferred to Phase 5)
- Splitting Phase 4 into 4a/4b (normalization test is small; keep combined)
- Writing any new tests (document edits only)
- Changing Phase 4 status from "not started" (scope is clarified, not started)

## Implementation Approach

Single-phase direct edit of `context/foundation/test-plan.md`. Four non-overlapping sections in document order. Each change is surgical — a few lines at most.

---

## Phase 1: Update test-plan.md

### Overview

Four targeted section edits in document order: §3 Phase 4 row annotation, §5 Quality Gates table replacement, §6.6 Phase notes addition, §8 Freshness Ledger date.

### Changes Required:

#### 1. §3 Phased Rollout — Phase 4 row

**File**: `context/foundation/test-plan.md`

**Intent**: Annotate the Phase 4 row so readers know Risk #6's search-retrieval half is already covered by tag-search TDD tests; what remains is Risk #5 (async extraction + poison path) and Risk #6's normalization half (`TagNormalizer` unit test with requirement-derived oracle).

**Contract**: The existing row reads:
```
| 4 | Async extraction + business rules | Failed extraction reaches a visible terminal status (not stuck) and the poison path; tags normalize to PL | #5, #6 | unit + integration | not started | — |
```
Replace the Goal cell text with: `Failed extraction reaches a visible terminal status (not stuck) and the poison path (#5); TagNormalizer produces PL tags with requirement-derived oracle (#6 normalization). Note: Risk #6 search-retrieval (correct field, scoping, HTTP contract) already covered by ReceiptSearchTests + ReceiptSearchEndpointTests (tag-search TDD).`

Keep Status (`not started`) and Change folder (`—`) unchanged.

#### 2. §5 Quality Gates — replace placeholder hook row and add pre-commit row

**File**: `context/foundation/test-plan.md`

**Intent**: Remove the inaccurate "post-edit hook (run affected tests) → recommended" row and replace it with three accurate per-edit rows plus a pre-commit row that match what actually runs.

**Contract**: Remove the row:
```
| post-edit hook (run affected tests) | local (agent loop) | recommended | regressions at edit time |
```

Insert four rows in its place (keep Required? consistent with the existing table format):

```
| per-edit C# build check (`check_cs_build.py`) | local (agent loop, `.cs` edits) | required | compilation errors at edit time |
| per-edit frontend lint + typecheck (`check_frontend_lint.py`) | local (agent loop, `.ts`/`.html` in frontend) | required | ESLint violations, TypeScript type errors at edit time |
| per-edit csproj restore check (`check_csproj_restore.py`) | local (agent loop, `.csproj` edits) | required | restore failures at edit time |
| pre-commit backend test suite (lefthook) | local (git pre-commit) | required | backend regressions before commit (known gap: no glob filter, runs on all commits regardless of which files changed; frontend has no commit gate — deferred to Phase 5) |
```

#### 3. §6.6 Per-rollout-phase notes — add tag-search + hooks entry

**File**: `context/foundation/test-plan.md`

**Intent**: Capture what the tag-search + hooks work taught: the wildcard/QueryType.Full analyzer trap, the per-edit vs. commit layering decision, and the Risk #6 coverage split.

**Contract**: Append after the Phase 2 note block (after the last bullet under `**Phase 2 — Infra-boundary failure shape**`):

```
**Tag-search + hooks (out-of-band, 2026-06-27):**
- **Wildcard queries bypass the field's language analyzer.** `QueryType.Full` + `*` wildcard disables `pl.microsoft` at query time (discovered mid-tag-search TDD, committed to `lessons.md`). Future tests for search fields should assert the absence of wildcards and `QueryType.Full` when a language-analyzer field is involved.
- **Per-edit hooks run build/lint, not tests.** `dotnet test` is too slow for the agent loop — tests moved to commit (lefthook pre-commit, full backend suite). Three per-edit hooks cover: C# build (`check_cs_build.py`), frontend lint+tsc (`check_frontend_lint.py`), csproj restore (`check_csproj_restore.py`).
- **Risk #6 split.** Tag-search TDD covered the search-retrieval half of Risk #6 (`ReceiptSearchTests`, `ReceiptSearchEndpointTests`, 5 frontend specs — correct field, scoping, HTTP contract). The normalization half (`TagNormalizer` unit test with requirement-derived oracle — "bike"/"bicycle" → "rower") has zero automated coverage and remains a Phase 4 obligation.
```

#### 4. §8 Freshness Ledger — update review date

**File**: `context/foundation/test-plan.md`

**Intent**: Advance the "Strategy last reviewed" date to reflect today's refresh.

**Contract**: Change:
```
- Strategy (§1–§5) last reviewed: 2026-06-23
```
To:
```
- Strategy (§1–§5) last reviewed: 2026-06-27
```

### Success Criteria:

#### Automated Verification:

- §5 no longer contains the string "post-edit hook (run affected tests)"
- §8 reads "Strategy (§1–§5) last reviewed: 2026-06-27"
- File parses as valid markdown with no broken table rows (pipe count consistent)

#### Manual Verification:

- §3 Phase 4 row clearly names covered sub-risk (search-retrieval) and remaining obligations (#5 + #6 normalization); Status is still "not started"
- §5 has exactly the four new rows (3 per-edit + 1 pre-commit) with accurate descriptions and the old placeholder row is gone
- §6.6 "Tag-search + hooks" block is present with all three lessons (wildcard trap, layering decision, Risk #6 split)
- No other sections of the file were inadvertently altered (verify via diff)

**Implementation Note**: One-phase document edit. After manual review confirms accuracy, mark complete.

---

## Testing Strategy

### Manual Testing Steps:

1. Read §3 Phase 4 row — confirm covered vs. remaining Risk #6 sub-risks are named; Status unchanged
2. Read §5 Quality Gates — confirm old placeholder row gone; four new rows present with accurate descriptions
3. Read §6.6 — confirm new "Tag-search + hooks (out-of-band, 2026-06-27)" block present with three bullets
4. `git diff context/foundation/test-plan.md` — confirm edits are confined to the four target sections

## References

- Research: `context/changes/test-plan-refresh-2026-06-27/research.md`
- Tag-search plan: `context/changes/tag-search/plan.md`
- Hook files: `.claude/check_cs_build.py`, `.claude/check_frontend_lint.py`, `.claude/check_csproj_restore.py`
- Pre-commit gate: `lefthook.yml`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Update test-plan.md

#### Automated

- [x] 1.1 §5 no longer contains "post-edit hook (run affected tests)" — d3847b5
- [x] 1.2 §8 reads "Strategy (§1–§5) last reviewed: 2026-06-27" — d3847b5
- [x] 1.3 File parses as valid markdown with no broken table rows — d3847b5

#### Manual

- [x] 1.4 §3 Phase 4 row names covered sub-risk and remaining obligations; Status still "not started" — d3847b5
- [x] 1.5 §5 has four new rows (3 per-edit + 1 pre-commit) with accurate descriptions; old placeholder gone — d3847b5
- [x] 1.6 §6.6 "Tag-search + hooks" block present with all three lessons — d3847b5
- [x] 1.7 No unintended edits outside the four target sections (verified via diff) — d3847b5
