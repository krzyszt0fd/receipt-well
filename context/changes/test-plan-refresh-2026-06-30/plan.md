# Test Plan Refresh 2026-06-30 — Implementation Plan

## Overview

Two-phase change: (1) commit the Risk #5 E2E spec and CI workflow fixes sitting uncommitted in the working tree; (2) update `context/foundation/test-plan.md` to reflect that E2E tests for both E2E risks are live, Phase 5 is implementing, and CI gates are partially wired.

## Current State Analysis

Since the 2026-06-27 refresh, the following landed in commits:
- E2E project scaffolded (`0ced11c chore(preparation for e2e-tests)`)
- Risk #6 no-match panel E2E test + infrastructure (`c47c9ff`)
- E2E project documented in CLAUDE.md / README (`714c3a1`)

Uncommitted today (working tree dirty):
- `src/e2e-tests/tests/async-processing-status.spec.ts` — new Risk #5 E2E test (untracked)
- `.github/workflows/e2e-tests.yml` — fixed: separate frontend dep install step + auth env secrets
- `.github/workflows/frontend-deploy.yml` — added lint + Vitest steps to the frontend deploy job
- `src/e2e-tests/playwright.config.ts` — fixed `npm --prefix ../frontend run start:local`

The test-plan.md was last refreshed 2026-06-29; Phase 5 still reads "not started" and Playwright still reads "planned".

## Desired End State

1. All four uncommitted files are committed.
2. `context/foundation/test-plan.md` accurately reflects: Phase 5 status → `implementing`, Playwright stack row → live with version, e2e gate "Where" → actual CI trigger, §6.6 has E2E lessons, header + §8 date → 2026-06-30.

## What We're NOT Doing

- Writing frontend unit tests for guarded routes / upload-validation UX (Phase 5 remainder)
- Changing the e2e CI trigger from push-to-develop to PR-required
- Creating a formal Phase 5 change folder (E2E work shipped out-of-band)
- Any backend changes

---

## Phase 1: Commit Risk #5 E2E spec + CI workflow fixes

### Overview

Stage and commit four files from the working tree. No code changes required — the files are already authored.

### Changes Required

Files to commit (no code edits needed):
- `src/e2e-tests/tests/async-processing-status.spec.ts` (new, untracked)
- `.github/workflows/e2e-tests.yml` (modified)
- `.github/workflows/frontend-deploy.yml` (modified)
- `src/e2e-tests/playwright.config.ts` (modified)

### Success Criteria

#### Automated

- 1.1 `async-processing-status.spec.ts` is no longer untracked (appears in git log)
- 1.2 `e2e-tests.yml` diff from HEAD is empty
- 1.3 `frontend-deploy.yml` diff from HEAD is empty
- 1.4 `playwright.config.ts` diff from HEAD is empty

#### Manual

- 1.5 Commit message accurately describes Risk #5 E2E test + CI fixes

---

## Phase 2: Update test-plan.md

### Overview

Six targeted edits to `context/foundation/test-plan.md` in document order.

### Changes Required

#### 1. Header "Last updated" line

**File**: `context/foundation/test-plan.md`

**Intent**: Reflect that E2E tests and CI wiring shipped in this refresh.

**Contract**: Replace:
```
Last updated: 2026-06-29 (e2e candidates promoted: Risk #5 async flow + Risk #6 no-match state rendering)
```
With:
```
Last updated: 2026-06-30 (Risk #5 + Risk #6 E2E specs shipped; Phase 5 implementing; CI e2e gate + frontend lint/test wired)
```

#### 2. §3 Phase 5 row — status + goal annotation

**File**: `context/foundation/test-plan.md`

**Intent**: Mark Phase 5 implementing and note which sub-deliverables are done out-of-band.

**Contract**: In the Phase 5 table row:
- Status cell: `not started` → `implementing`
- Goal cell: append to the existing text " Note: E2E specs for Risk #5 (`async-processing-status.spec.ts` — polling flow) and Risk #6 (`tag-search-no-match.spec.ts` — no-match panel) shipped out-of-band; CI e2e gate wired to develop push + frontend lint/test added to deploy pipeline."

#### 3. §4 Stack — update e2e Playwright row

**File**: `context/foundation/test-plan.md`

**Intent**: Replace "planned" with actual version and live status.

**Contract**: Replace the Tool/Version/Notes values in the `e2e (Playwright)` row:
- Tool: `@playwright/test` (was "planned — §3 Phase 5")
- Version: `1.61.1`
- Notes: "Live in `src/e2e-tests/`. Two specs: `async-processing-status.spec.ts` (Risk #5 polling flow) and `tag-search-no-match.spec.ts` (Risk #6 no-match panel). CI: `e2e-tests.yml` on push to develop. Auth via ROPC storageState (`playwright/.auth/user.json`)."

#### 4. §5 Quality Gates — correct e2e gate trigger

**File**: `context/foundation/test-plan.md`

**Intent**: Fix the "Where" column for the e2e gate — it runs on push to develop, not on PR (not yet).

**Contract**: In the e2e gate row, replace "CI on PR" with "CI on push to develop".

#### 5. §6.6 Per-rollout-phase notes — add E2E entry

**File**: `context/foundation/test-plan.md`

**Intent**: Capture what the E2E work taught.

**Contract**: Append after the "Tag-search + hooks (out-of-band, 2026-06-27)" block:

```
**E2E — Phase 5 partial (out-of-band, 2026-06-30):**
- **Risk #5 and Risk #6 E2E specs shipped before the formal Phase 5 rollout.** `async-processing-status.spec.ts` (Risk #5 — polling flow: pending → ready) and `tag-search-no-match.spec.ts` (Risk #6 — no-match panel rendering) were authored across the e2e preparation + tag-search changes, not a dedicated Phase 5 change folder.
- **Polling mock pattern for async status.** Intercept `GET /receipts` with `page.route()`, return `pending` on the first call and `ready` on subsequent calls. Register `waitForResponse()` *before* `page.goto()` to avoid the race where a fast response arrives before the listener is set up.
- **`webServer.command` must prefix the frontend path.** When `playwright.config.ts` lives in `src/e2e-tests/`, use `npm --prefix ../frontend run start:local` — plain `npm run start:local` fails because it executes from the e2e-tests directory and can't find the frontend `package.json`.
```

#### 6. §8 Freshness Ledger — update review date

**File**: `context/foundation/test-plan.md`

**Intent**: Advance the "Strategy last reviewed" date to today.

**Contract**: Replace:
```
- Strategy (§1–§5) last reviewed: 2026-06-29
```
With:
```
- Strategy (§1–§5) last reviewed: 2026-06-30
```

### Success Criteria

#### Automated

- 2.1 Header line reads "Last updated: 2026-06-30"
- 2.2 §3 Phase 5 row Status column reads `implementing`
- 2.3 §5 e2e gate "Where" reads "CI on push to develop"
- 2.4 §8 reads "Strategy (§1–§5) last reviewed: 2026-06-30"
- 2.5 File parses as valid markdown (no broken table rows — pipe count consistent)

#### Manual

- 2.6 §3 Phase 5 goal cell names both specs + CI wired; no other Phase 5 cells changed
- 2.7 §4 Playwright row shows version 1.61.1 and "Live in `src/e2e-tests/`"
- 2.8 §6.6 E2E block has three bullets (out-of-band note, polling pattern, webServer prefix)
- 2.9 No unintended edits outside the six target sections (verify via `git diff`)

---

## Testing Strategy

### Manual Testing Steps

1. `git diff context/foundation/test-plan.md` — confirm edits confined to the six target sections
2. Read §3 Phase 5 row — confirm "implementing" + goal annotation present
3. Read §4 e2e row — confirm version 1.61.1 and "Live in `src/e2e-tests/`"
4. Read §5 e2e gate — confirm "CI on push to develop"
5. Read §6.6 — confirm E2E block with three bullets
6. Read §8 — confirm 2026-06-30

## References

- Previous refresh: `context/changes/test-plan-refresh-2026-06-27/plan.md`
- Risk #5 spec: `src/e2e-tests/tests/async-processing-status.spec.ts`
- Risk #6 spec: `src/e2e-tests/tests/tag-search-no-match.spec.ts`
- E2E config: `src/e2e-tests/playwright.config.ts`
- CI: `.github/workflows/e2e-tests.yml`, `.github/workflows/frontend-deploy.yml`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Commit Risk #5 E2E spec + CI workflow fixes

#### Automated

- [x] 1.1 async-processing-status.spec.ts committed (no longer untracked) — defe8eb
- [x] 1.2 e2e-tests.yml diff from HEAD is empty — defe8eb
- [x] 1.3 frontend-deploy.yml diff from HEAD is empty — defe8eb
- [x] 1.4 playwright.config.ts diff from HEAD is empty — defe8eb

#### Manual

- [ ] 1.5 Commit message accurately describes Risk #5 E2E test + CI fixes

### Phase 2: Update test-plan.md

#### Automated

- [x] 2.1 Header line reads "Last updated: 2026-06-30" — c3a9e18
- [x] 2.2 §3 Phase 5 row Status reads "implementing" — c3a9e18
- [x] 2.3 §5 e2e gate "Where" reads "CI on push to develop" — c3a9e18
- [x] 2.4 §8 reads "Strategy (§1–§5) last reviewed: 2026-06-30" — c3a9e18
- [x] 2.5 File parses as valid markdown (no broken table rows) — c3a9e18

#### Manual

- [x] 2.6 §3 Phase 5 goal names both specs + CI wired; other cells unchanged — c3a9e18
- [x] 2.7 §4 Playwright row shows version 1.61.1 and "Live in `src/e2e-tests/`" — c3a9e18
- [x] 2.8 §6.6 E2E block has three bullets — c3a9e18
- [x] 2.9 No unintended edits outside the six target sections (verified via diff) — c3a9e18
