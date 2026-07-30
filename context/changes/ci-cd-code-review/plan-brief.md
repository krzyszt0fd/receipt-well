# First CI/CD Workflow for PR Code Reviews — Plan Brief

> Full plan: `context/changes/ci-cd-code-review/plan.md`
> Research: `context/changes/ci-cd-code-review/research.md`

## What & Why

Add the repo's first PR-triggered GitHub Actions workflow: wrap the existing `packages/code-reviewer` Claude Agent SDK CLI as a composite action, run it on PRs to `develop`, and post a summary comment plus `ai-review:passed`/`ai-review:failed` labels. This is also the first step away from the repo's MVP-era "commit directly to `develop`, no PR required" default.

## Starting Point

`packages/code-reviewer/src/review.ts` already works as a standalone CLI — it reads a diff from stdin and returns a structured pass/fail verdict via a Claude Agent SDK call. It has no PR title/description input, no GitHub-side behavior at all, and its stdout mixes a human-readable cost line with its JSON result. No workflow in this repo triggers on `pull_request` today; all four existing ones are `push`-to-`develop` deploys.

## Desired End State

Opening a PR against `develop` (or adding an `ai-review:review` label) triggers one review run that posts/updates a single PR comment and applies the right outcome label. New pushes clear the stale label without spending another paid run; the author explicitly asks for a re-review via the label. A tooling error (not a real fail) fails the check without mislabeling the PR.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
|---|---|---|---|
| Composite action location | `.github/actions/code-reviewer/` (not `packages/code-reviewer/`) | Follows GitHub's own convention for local composite actions; `working-directory` scopes into `packages/code-reviewer` only for the npm-specific steps | Plan (user override of requirements.md) |
| Auto-trigger scope | Full review on open/reopen/retry-label only; a push just clears stale labels | Bounds cost to explicit triggers while preventing a stale `passed` label from surviving new commits | Plan |
| Review criteria (`{{CR_CRITERIA}}`) | Curated diff-checkable subset of CLAUDE.md rules, explicitly excluding build/AXE/test-independence checks | Avoids false positives on things an LLM can't verify from a diff alone | Plan |
| PR context passed to reviewer | Title + description (not diff-only) | Gives the reviewer intent context; existing `$0.7` budget cap already bounds cost regardless of prompt size | Plan |
| Label lifecycle | Replace prior outcome label every run; always consume the `ai-review:review` trigger label | Keeps label state honest — never two outcome labels at once, never a stuck "pending" label | Plan |
| Tooling error vs. fail verdict | Fail the GitHub check, but never apply `ai-review:failed` on a tooling error | Keeps `ai-review:failed` meaning "the code has issues," not "our automation broke" | Plan |
| Concurrency | Per-PR `cancel-in-progress` group | Avoids duplicate comments/cost when a PR gets rapid triggers | Plan |

## Scope

**In scope:**
- `review.ts`/`review-schema.ts` input/output contract changes and curated review criteria
- `.github/actions/code-reviewer/action.yml` composite action (run + parse + labels + comment)
- `.github/workflows/pr-code-review.yml` trigger wiring
- Stale `CLAUDE.md` line update, `ANTHROPIC_API_KEY` secret documentation

**Out of scope:**
- Branch protection / required-check wiring (GitHub Settings, not code)
- Fork-PR security hardening (`pull_request_target`) — solo-dev private repo, not a current risk
- A unit test harness for `packages/code-reviewer`
- Changing the existing `maxBudgetUsd`/`maxTurns` caps

## Architecture / Approach

`pull_request` (opened/reopened/synchronize/labeled) → workflow picks `review` job (full run) or `clear-stale-label` job (push only, no paid call) → `review` job checks out with `fetch-depth: 0` and calls the composite action with explicit inputs (composite actions can't read `secrets.*`/`github.*` directly) → composite action installs deps, computes a three-dot diff, runs `review.ts`, then branches on success/failure to update labels and a single edited-in-place PR comment.

## Phases at a Glance

| Phase | What it delivers | Key risk |
|---|---|---|
| 1. Extend `review.ts`/`review-schema.ts` | PR-aware input, clean JSON-only stdout, curated review criteria | Criteria list drifts from CLAUDE.md over time (no auto-sync) |
| 2. Composite action core | Diff computation + reviewer invocation + JSON capture | `npm run` stdout banner corrupting JSON if not avoided |
| 3. Composite action side-effects | Idempotent labels, edit-in-place comment, tooling-error path | Distinguishing a crash from a real fail verdict without extra plumbing |
| 4. Workflow wiring | Triggers, concurrency, permissions | Missing `fetch-depth: 0` breaks the diff computation |
| 5. Docs & secret | Updated `CLAUDE.md`, documented manual secret | Forgetting to add `ANTHROPIC_API_KEY` before first real PR |

**Prerequisites:** `ANTHROPIC_API_KEY` added as a GitHub Actions secret before Phase 4 is exercised on a real PR.
**Estimated effort:** ~1–2 sessions across 5 phases (small, well-scoped file surface; most effort is composite-action YAML plumbing, not new business logic).

## Open Risks & Assumptions

- Assumes GitHub CLI (`gh`) is preinstalled and authenticated via `GITHUB_TOKEN` on `ubuntu-latest` runners (standard, but worth a quick check on first real run).
- Assumes `gh pr comment --edit-last` reliably targets the bot's own prior comment; a fallback to a fresh comment is included in case no prior comment exists.
- The curated `{{CR_CRITERIA}}` list is a point-in-time snapshot of today's CLAUDE.md rules — it will need manual upkeep as those files evolve.

## Success Criteria (Summary)

- A real PR against `develop` gets exactly one bot comment and one correct outcome label, without manual intervention.
- A follow-up push clears the stale label without a new paid run; the `ai-review:review` label re-triggers a real run that edits (not duplicates) the comment.
- A tooling failure fails the check visibly without falsely labeling the PR `ai-review:failed`.
