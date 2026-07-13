<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Receipts List with Status (S-02)

- **Plan**: context/changes/receipts-list-with-status/plan.md
- **Scope**: Phase 2 + Phase 3 of 3 (Phase 1 reviewed separately — see `impl-review-phase-1.md`)
- **Date**: 2026-06-23
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical, 3 warnings, 0 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | WARNING |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

## Findings

### F1 — Polling has no cutoff; a permanently-stuck legacy `pending` receipt polls forever

- **Severity**: WARNING
- **Impact**: HIGH — architectural stakes; think carefully before deciding
- **Dimension**: Safety & Quality
- **Location**: `src/frontend/src/app/receipts/list/list.component.ts:36` (`hasPending`), `:85-89` (`schedulePollIfPending`)
- **Detail**: `hasPending` is a pure existence check over the current snapshot (`this.receipts().some(r => r.status === 'pending')`) with no attempt counter, no age cutoff, and no backoff. `schedulePollIfPending()` re-arms a fixed 5s timer unconditionally on every successful *and* failed poll. Records uploaded before the backend processing queue existed are permanently stuck at `pending` — the queue never retroactively picks them up, so they never flip to `ready`/`error`. Per the user's report, any account with even one such legacy record polls every 5 seconds, indefinitely, for as long as the list page stays mounted — regardless of how many other receipts have already settled. This wasn't an accepted tradeoff: the plan's Performance Considerations section only says polling "stops itself as soon as nothing is pending," assuming all `pending` receipts eventually settle. The legacy-record scenario isn't mentioned anywhere in plan.md, change.md, or the queue-introducing slice's docs — it's an unconsidered gap, not a documented limitation.
- **Fix A ⭐ Recommended**: One-time backend data fix — bulk-update the old pre-queue records' `Status` from `pending` to `error` in the Search index (they genuinely will never be processed, which is what `error` already means).
  - Strength: Fixes the root cause once; `error` is already a fully-supported terminal state (chip color, no retry UI needed per existing "no manual-retry UI for error receipts" decision) — `hasPending` stops counting them with zero new code.
  - Tradeoff: One-time scripted/manual op against the live index; needs the queue's go-live date as the cutoff to avoid misclassifying receipts that are legitimately still processing.
  - Confidence: HIGH — uses the existing status model as-is, no new states or polling logic to maintain.
  - Blind spot: Haven't verified there's an existing path to bulk-update Search documents outside the running app (e.g. a script using the same `SearchClient` credentials).
- **Fix B**: Add an age-based cutoff in `hasPending()` — exclude a receipt from the pending check once its `uploadedAt` exceeds a threshold (e.g. 30 min), regardless of root cause.
  - Strength: Defends against any future cause of stuck `pending`, not just this one-time legacy gap; ships entirely within this component, no backend change.
  - Tradeoff: Heuristic threshold can misfire on a legitimately slow pipeline run; once a receipt ages out, only manual refresh re-checks it — auto-poll never resumes for it even if it later starts moving again.
  - Confidence: MEDIUM — sound circuit-breaker pattern, but introduces age-tracking state the plan never specified.
  - Blind spot: Haven't checked S-03's actual processing-latency distribution to pick a threshold that won't cut off real, still-in-flight receipts.
- **Decision**: FIXED via Fix B. Added `POLL_STALE_THRESHOLD_MS` (30 min) and `isStalePending()` in `list.component.ts`; `hasPending` now excludes receipts pending past that age. Updated test fixtures (`pendingReceipt` now uses a live timestamp instead of a fixed past date, which would otherwise read as already-stale) and added a spec asserting a receipt stuck `pending` since 2020 does not register as `hasPending`. Build clean, 15/15 tests pass.

### F2 — Shared `.page-card` widened globally instead of a local `.receipts-card` override

- **Severity**: WARNING
- **Impact**: MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Plan Adherence
- **Location**: `src/frontend/src/styles.scss:23-27`; intended-but-missing: `src/frontend/src/app/receipts/list/list.component.scss` (no `.receipts-card` selector exists)
- **Detail**: Plan's Critical Implementation Details explicitly said: *"Add a local modifier class (e.g. `.page-card.receipts-card { max-width: 720px; }`) in the list component's scss rather than widening the shared class — the upload card must stay narrow."* The shared `.page-card` rule in `styles.scss` was instead widened directly from 480px to 720px, and no `.receipts-card` class was ever added. Since `upload.component.html` uses the bare `.page-card` class with no override, the upload form — which the plan says "must stay narrow" — is now also 720px, the same width as the receipts list.
- **Fix A ⭐ Recommended**: Revert `styles.scss`'s `.page-card` back to `max-width: 480px`; add `.page-card.receipts-card { max-width: 720px; }` to `list.component.scss` and apply the `receipts-card` class on the list's outer `mat-card`, matching the plan as written.
  - Strength: Restores the upload form's intended narrow width; matches the plan exactly with a small, contained change.
  - Tradeoff: Visually shrinks the upload card back down — if you've already gotten used to seeing it wider, this is a visible regression-of-the-regression.
  - Confidence: HIGH — this is literally what the plan specified and it's a two-line change.
  - Blind spot: Haven't checked whether any other page added since this commit also now depends on the wider shared `.page-card`.
- **Fix B**: Keep the global 720px width and update plan.md to record it as a deliberate addendum (shared width bumped for both pages by design).
  - Strength: No further code change; if the wider upload form is actually fine/preferred, this avoids needless churn.
  - Tradeoff: Plan's stated rationale ("must stay narrow") is overridden without having re-confirmed that's still wanted.
  - Confidence: MEDIUM — depends entirely on whether the wider upload form was actually a regression or an unnoticed improvement.
  - Blind spot: Haven't seen the rendered upload page at 720px to judge whether it still looks right for a single-field form.
- **Decision**: ACCEPTED via Fix B. Kept the global 720px `.page-card` width; recorded as a post-implementation addendum in `plan.md`'s Critical Implementation Details.

### F3 — Extra "View my receipts" link added to upload's idle-state header (not in plan)

- **Severity**: WARNING
- **Impact**: LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: `src/frontend/src/app/receipts/upload/upload.component.html` (idle-state `page-header`)
- **Detail**: Plan item 5 only specified adding "View my receipts" to the **confirmed** state's `mat-card-actions`. The implementation also added the same link to the idle-state header, a third placement beyond what was planned. Functionally harmless and on-theme with the slice's bridging goal, but undocumented scope addition.
- **Fix**: Document the idle-state nav link as a plan addendum in plan.md (one line under Phase 2's changes) — it's a benign, consistent UX improvement, not worth removing.
- **Decision**: SKIPPED
