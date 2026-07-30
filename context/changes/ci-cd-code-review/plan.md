# First CI/CD Workflow for PR Code Reviews — Implementation Plan

## Overview

Add the repo's first PR-triggered GitHub Actions workflow. It wraps the existing `packages/code-reviewer` Claude Agent SDK CLI as a composite action at `.github/actions/code-reviewer`, runs it on pull requests targeting `develop`, and posts a summary comment plus `ai-review:passed`/`ai-review:failed`/`ai-review:review` labels. This is also the first step away from this repo's "commit directly to `develop`, no PR required" MVP default.

## Current State Analysis

- `packages/code-reviewer/src/review.ts` is a working standalone CLI: reads a diff from stdin, runs a Claude Agent SDK `query()` with `Read`/`Grep`/`Glob` tools and `settingSources: []`, prints a cost line and then a JSON result to stdout. It has no PR title/description input, no GitHub-side behavior, and mixes a human-readable cost line into the same stdout stream as its JSON output.
- `review-schema.ts`'s `SYSTEM_PROMPT` asks for 5 fixed generic criteria (1–10) plus a `pass`/`fail` verdict and Markdown summary — no project-specific rules are injected yet (`{{CR_CRITERIA}}` in `requirements.md` is unresolved).
- No workflow in `.github/workflows/` declares a `pull_request` trigger, `pull-requests: write` permission, or posts comments/labels — all four existing workflows are `push`-to-`develop` deploys. `.github/actions/` doesn't exist yet.
- GitHub secrets are provisioned manually in this repo (no Terraform path for them); `ANTHROPIC_API_KEY` needs the same manual add, documented in a table (matching `context/changes/deployment/deployment-plan.md`'s convention).
- Root `.claude/CLAUDE.md:20` currently reads "Solo dev during MVP — commit directly to `develop`, no PR required" — stale once this ships.

## Desired End State

Opening (or reopening) a PR against `develop`, or adding the `ai-review:review` label to an existing PR, triggers a single Claude-Agent-SDK-backed review run that posts/updates one PR comment with the verdict + scores and applies `ai-review:passed` or `ai-review:failed`. Pushing new commits to an open PR clears any stale outcome label without spending another paid run, until the author explicitly asks for a re-review via the label. A run that errors (not a genuine fail verdict) fails the GitHub check without misleadingly labeling the PR `ai-review:failed`.

Verify by: opening a real PR against `develop`, watching the workflow run, seeing the comment + `ai-review:passed`/`failed` label land, pushing a commit and confirming the label is cleared without a new run, then adding `ai-review:review` and confirming a fresh run replaces the comment (not duplicates it) and re-applies a label.

### Key Discoveries:

- Composite actions cannot read `secrets.*` or `github.*` context directly — the calling workflow must pass everything as explicit `inputs:` ([research.md:110](research.md)).
- `npm run <script>` prints an npm banner (`> code-reviewer@1.0.0 review\n> tsx src/review.ts`) to stdout before the script's own output — this would corrupt naive JSON parsing of `review.ts`'s stdout. Invoking `npx tsx src/review.ts` directly avoids the wrapper noise.
- `review.ts`'s own cost line (`console.log` at [review.ts:34](../../../packages/code-reviewer/src/review.ts#L34)) is on the same stdout stream as the final JSON dump ([review.ts:46](../../../packages/code-reviewer/src/review.ts#L46)) — must move to stderr so a workflow step can treat stdout as pure JSON.

## What We're NOT Doing

- No branch protection rule wiring `ai-review:failed`/`passed` as a required merge check — that's a GitHub repo Settings change, not code, and stays a manual follow-up if/when desired.
- No handling for PRs from forks (no `pull_request_target` hardening, no secret-exposure mitigation) — this is a solo-dev private repo; not a current risk.
- No unit test suite for `packages/code-reviewer`'s TS logic — mirrors the package's existing stub `test` script; verification here is the manual end-to-end PR run.
- No changes to `maxBudgetUsd`/`maxTurns` caps in `review.ts` — the existing $0.7 / 20-turn ceiling already bounds cost regardless of prompt size.
- Business alignment and architectural-fit review criteria (parked in `requirements.md` — need broader context than a diff review can provide).

## Implementation Approach

Five phases, each independently verifiable: (1) extend the existing CLI's input/output contract and bake in project-specific review criteria, (2) build the composite action's core run-and-parse logic, (3) add its GitHub side-effects (labels, comment), (4) wire the calling workflow with the right triggers/permissions/concurrency, (5) update stale docs and document the one manual secret. Phases 2–3 both live in the same `action.yml` but are separated because they're independently testable: phase 2 can be verified by inspecting captured JSON output, phase 3 requires a real PR.

## Critical Implementation Details

**Composite action input passing**: `.github/actions/code-reviewer/action.yml` cannot read `${{ secrets.ANTHROPIC_API_KEY }}`, `${{ secrets.GITHUB_TOKEN }}`, or any `github.event.pull_request.*` field itself. The calling workflow (Phase 4) must pass every one of these as an explicit `with:` input, and the composite action re-exposes them as step `env:` inside itself.

**Diff must use three-dot notation**: `git diff ${{ inputs.base-sha }}...${{ inputs.head-sha }}` (three dots), not two-dot `base..head`. Three-dot diffs against the merge-base, matching what GitHub's own PR diff view shows and excluding unrelated commits `develop` picked up after the PR branched. Two-dot would include those, confusing the reviewer with unrelated changes. Requires the caller's `actions/checkout` to use `fetch-depth: 0` (Phase 4) so both SHAs are actually present locally.

**stdout must be pure JSON**: Phase 1 moves `review.ts`'s cost line to `console.error`. Phase 2's run step must invoke `npx tsx src/review.ts` directly (not `npm run review`), since `npm run` prepends a script-name banner to stdout that would break JSON parsing.

**Distinguish a tooling error from a real "fail" verdict**: the run-review step needs `continue-on-error: true` so a `review.ts` crash (API outage, malformed structured output, budget/turn exceeded) doesn't skip the rest of the composite action. A follow-up step checks `steps.run-review.outcome`: on `failure`, post a "review couldn't complete, retry via the `ai-review:review` label" comment and explicitly `exit 1` (the action must still end in overall failure so the GitHub check goes red) without touching `ai-review:passed`/`ai-review:failed`. On `success`, proceed with real verdict handling. This keeps `ai-review:failed` meaning "the code has issues," never "our automation broke."

## Phase 1: Extend `review.ts` / `review-schema.ts` contract

### Overview

Give the CLI a PR-aware input contract and a clean stdout, and resolve the `{{CR_CRITERIA}}` placeholder with a curated, diff-checkable subset of this repo's CLAUDE.md rules.

### Changes Required:

#### 1. PR title/description input + stdout/stderr split

**File**: `packages/code-reviewer/src/review.ts`

**Intent**: Accept the PR's title and description as additional context for the review, and stop mixing the human-readable cost line into the JSON stdout stream.

**Contract**: Read `PR_TITLE` and `PR_DESCRIPTION` from `process.env` (default to empty string if unset), and include them in the `prompt` string passed to `query()` ahead of the diff. Change the cost-line `console.log` at review.ts:34 to `console.error`, so stdout at review.ts:46 is the only stdout output — pure JSON.

#### 2. Curated project-specific review criteria

**File**: `packages/code-reviewer/src/review-schema.ts`

**Intent**: Resolve `requirements.md`'s `{{CR_CRITERIA}}` placeholder by appending a curated, diff-checkable rule set to `SYSTEM_PROMPT`, in the same Polish register as the existing prompt. Only rules an LLM can judge from a diff alone are included; build-success, AXE/WCAG, and test-independence checks are explicitly named as out of scope for this reviewer (they already run in existing CI/test jobs).

**Contract**: Append a criteria block to `SYSTEM_PROMPT` covering:
- **Bezpieczeństwo/sekrety**: diff nie może wprowadzać sekretów, kluczy, tokenów, connection stringów, danych hostingowych ani PII do plików śledzonych przez git; recenzja może oznaczyć ich obecność, ale nigdy nie cytuje ich treści w podsumowaniu.
- **Brak SQL/EF Core**: diff nie może wprowadzać pakietów EF Core, `DbContext` ani surowego SQL — projekt nie używa bazy SQL.
- **Backend (.NET)**: przestrzeń nazw zaczyna się od `ReceiptWell`; nowe typy referencyjne mają adnotacje nullable; brak atrybutu `Version` w plikach `.csproj` (centralne zarządzanie pakietami); logowanie przez source-generated logging z ID tożsamości/encji, bez logowania treści request/response.
- **Backend testy**: `TestAuthHandler` używa nagłówków `X-Test-Oid`/`X-Test-No-Oid`; mocki NSubstitute używają `DidNotReceiveWithAnyArgs()` tam, gdzie to zasadne; asercje filtrów sprawdzają pole+id, nie dokładny string.
- **Frontend (Angular)**: TypeScript strict, unikanie `any` na rzecz `unknown`; stan przez `signal()`/`computed()`, komponenty `OnPush`, brak `ngClass`/`ngStyle` na rzecz natywnego `@if`/`@for`, brak `.mutate()` na sygnałach, `inject()` zamiast wstrzykiwania przez konstruktor, `input()`/`output()` zamiast dekoratorów.
- **E2E (Playwright)**: lokatory `getByRole`/`getByLabel`/`getByText` w pierwszej kolejności, `getByTestId` tylko gdy atrybuty dostępności są niejednoznaczne, nigdy selektory CSS/XPath/struktura DOM; zakaz `page.waitForTimeout()`; autoryzacja tylko przez `storageState`; mockowanie wyłącznie na granicy API przez `page.route()`.
- Explicit closing line naming what's **out of scope** for this reviewer: poprawność buildu (zero warnings), AXE/WCAG AA, niezależność testów e2e, wszystko wymagające faktycznego uruchomienia aplikacji.

### Success Criteria:

#### Automated Verification:

- `npm ci` succeeds: `cd packages/code-reviewer && npm ci`
- CLI still runs end-to-end against a sample diff: `git diff HEAD~1 | PR_TITLE="test" PR_DESCRIPTION="test" ANTHROPIC_API_KEY=*** npx tsx src/review.ts` (in `packages/code-reviewer`) produces valid JSON on stdout with no extraneous lines

#### Manual Verification:

- Cost line appears on stderr, not stdout, when run locally
- Review summary for a diff touching a rule from the curated list (e.g. an EF Core reference, or a raw CSS selector in an e2e test) actually flags it

---

## Phase 2: Composite action core (`.github/actions/code-reviewer/action.yml`)

### Overview

Build the composite action's run-and-parse logic: install deps, compute the PR diff, invoke the reviewer, capture its structured result. No GitHub side-effects yet — this phase is verifiable by inspecting the captured JSON.

### Changes Required:

#### 1. Composite action definition and core steps

**File**: `.github/actions/code-reviewer/action.yml`

**Intent**: Define the action's inputs, install `packages/code-reviewer`'s dependencies, compute the three-dot diff between `base-sha` and `head-sha`, and run the reviewer against it, capturing stdout as the JSON result.

**Contract**: `using: composite`. Inputs: `anthropic-api-key`, `github-token`, `pr-number`, `pr-title`, `pr-description`, `base-sha`, `head-sha` (all `required: true`). Steps:
1. `actions/setup-node@v4` with `node-version: lts/*` (matches existing workflow convention).
2. `npm ci` with `working-directory: packages/code-reviewer`.
3. Compute diff at repo root: `git diff ${{ inputs.base-sha }}...${{ inputs.head-sha }} > "$RUNNER_TEMP/pr.diff"` (three-dot — see Critical Implementation Details).
4. Run review, `id: run-review`, `continue-on-error: true`, `working-directory: packages/code-reviewer`, env `ANTHROPIC_API_KEY`/`PR_TITLE`/`PR_DESCRIPTION` from inputs: `npx tsx src/review.ts < "$RUNNER_TEMP/pr.diff" > "$RUNNER_TEMP/review-result.json"`.

### Success Criteria:

#### Automated Verification:

- `action.yml` is valid composite-action YAML (GitHub validates on workflow run; no local linter needed given no prior `.github/actions/*` precedent in this repo)

#### Manual Verification:

- Triggering the workflow (Phase 4 must exist first) on a real PR produces a populated `$RUNNER_TEMP/review-result.json` with `verdict`, 5 scores, and `summary` fields, visible in the run logs

---

## Phase 3: Composite action side-effects (labels + comment)

### Overview

Add the GitHub-facing half of the composite action: idempotent label bootstrap, verdict-driven label replacement, and a single updated (not duplicated) PR comment. Also handles the tooling-error path from Critical Implementation Details.

### Changes Required:

#### 1. Label bootstrap, comment, and outcome labeling

**File**: `.github/actions/code-reviewer/action.yml` (additional steps, same file as Phase 2)

**Intent**: Ensure the three `ai-review:*` labels exist, then branch on `steps.run-review.outcome`: on success, post/update the PR comment from the review's `summary` + scores and replace the outcome label; on failure, post an error comment and fail the action without touching outcome labels; either way, consume the `ai-review:review` trigger label if the run was triggered by it.

**Contract**: All steps use `env: GITHUB_TOKEN: ${{ inputs.github-token }}` and the `gh` CLI (preinstalled on `ubuntu-latest`):
- Bootstrap: `gh label create "ai-review:passed" --color 2ea44f --force`, `...:failed --color d73a4a --force`, `...:review --color fbca04 --force` — `--force` makes creation idempotent (updates if present, creates if not).
- On `steps.run-review.outcome == 'failure'`: build a short "review couldn't complete — retry via the `ai-review:review` label" comment, post via `gh pr comment ${{ inputs.pr-number }} --edit-last --body-file <path> || gh pr comment ${{ inputs.pr-number }} --body-file <path>` (edit the bot's last comment if one exists, else create), remove `ai-review:review` if present (`gh pr edit ${{ inputs.pr-number }} --remove-label "ai-review:review"`), then `exit 1` to fail the action (per Critical Implementation Details).
- On `steps.run-review.outcome == 'success'`: parse `verdict`/scores/`summary` from `$RUNNER_TEMP/review-result.json` via `jq`, build the comment body (summary + a Markdown scores table), post with the same edit-last-or-create pattern, then `gh pr edit ${{ inputs.pr-number }} --remove-label "ai-review:passed" --remove-label "ai-review:failed" --add-label "ai-review:$( [ "$VERDICT" = pass ] && echo passed || echo failed )"`, then remove `ai-review:review` if present.

### Success Criteria:

#### Automated Verification:

- N/A — this phase is GitHub-API side-effect logic with no local test harness; covered by manual verification once Phase 4 wires the trigger

#### Manual Verification:

- Opening a test PR produces exactly one bot comment and one correct outcome label
- Re-triggering via the `ai-review:review` label edits the same comment (no duplicate) and the trigger label is gone afterward
- Forcing a failure (e.g. temporarily unset the API key secret) fails the GitHub check without applying `ai-review:failed`, and posts the error comment

---

## Phase 4: Workflow wiring (`.github/workflows/pr-code-review.yml`)

### Overview

Wire the composite action into a real trigger: full review on open/reopen/retry-label, stale-label clearing (no paid run) on push, with a per-PR concurrency guard.

### Changes Required:

#### 1. New workflow file

**File**: `.github/workflows/pr-code-review.yml`

**Intent**: Trigger a full review run on `opened`, `reopened`, and `labeled` (filtered to the `ai-review:review` label); on `synchronize` (a new push), only clear any stale outcome label without spending a run. Guard against overlapping runs on the same PR.

**Contract**:
```yaml
on:
  pull_request:
    branches: [develop]
    types: [opened, reopened, synchronize, labeled]

permissions:
  contents: read
  pull-requests: write

concurrency:
  group: pr-code-review-${{ github.event.pull_request.number }}
  cancel-in-progress: true

jobs:
  review:
    if: >
      github.event.action != 'synchronize' &&
      (github.event.action != 'labeled' || github.event.label.name == 'ai-review:review')
    runs-on: ubuntu-latest
    timeout-minutes: 15
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: ./.github/actions/code-reviewer
        with:
          anthropic-api-key: ${{ secrets.ANTHROPIC_API_KEY }}
          github-token: ${{ secrets.GITHUB_TOKEN }}
          pr-number: ${{ github.event.pull_request.number }}
          pr-title: ${{ github.event.pull_request.title }}
          pr-description: ${{ github.event.pull_request.body }}
          base-sha: ${{ github.event.pull_request.base.sha }}
          head-sha: ${{ github.event.pull_request.head.sha }}

  clear-stale-label:
    if: github.event.action == 'synchronize'
    runs-on: ubuntu-latest
    permissions:
      pull-requests: write
    steps:
      - env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: gh pr edit ${{ github.event.pull_request.number }} --repo ${{ github.repository }} --remove-label "ai-review:passed" --remove-label "ai-review:failed"
```
`fetch-depth: 0` is required so both `base-sha` and `head-sha` are present for the composite action's diff (see Critical Implementation Details).

### Success Criteria:

#### Automated Verification:

- Workflow YAML is syntactically valid (GitHub Actions validates on push/PR; no local linter in this repo's toolchain)

#### Manual Verification:

- Opening a PR against `develop` triggers the `review` job only (not `clear-stale-label`)
- Pushing a new commit to that PR triggers `clear-stale-label` only, and the outcome label disappears without a new workflow run consuming budget
- Adding `ai-review:review` triggers `review` again
- Pushing two commits in quick succession cancels the first run's job (visible in the Actions UI) rather than running both to completion

---

## Phase 5: Docs & secret provisioning

### Overview

Update the now-stale workflow line in root `CLAUDE.md` and document the one manual secret this workflow needs.

### Changes Required:

#### 1. Update stale workflow description

**File**: `.claude/CLAUDE.md`

**Intent**: Line 20 currently says "commit directly to `develop`, no PR required" — no longer true once this workflow ships.

**Contract**: Replace the `## Workflow` section's line with wording reflecting the new reality: changes land via PR to `develop`; every PR gets an automated AI code review (`ai-review:passed`/`ai-review:failed`); re-run via the `ai-review:review` label; the review is advisory (no branch-protection gate), merge decision stays with the solo dev.

#### 2. Document the manual `ANTHROPIC_API_KEY` secret

**File**: `context/changes/ci-cd-code-review/plan.md` (this file — the secret table below)

**Intent**: Match the existing convention (`context/changes/deployment/deployment-plan.md:423-435`) of documenting manually-added GitHub secrets in a table, since there's no Terraform path for this one.

**Contract**:

| Secret name | Value | Where to add |
|---|---|---|
| `ANTHROPIC_API_KEY` | An Anthropic API key with access to `claude-sonnet-5`, scoped for this repo's CI use | GitHub → Settings → Secrets and variables → Actions → New repository secret |

### Success Criteria:

#### Automated Verification:

- N/A — documentation-only phase

#### Manual Verification:

- `ANTHROPIC_API_KEY` is present in GitHub → Settings → Secrets and variables → Actions before Phase 4's workflow is exercised on a real PR
- `.claude/CLAUDE.md`'s `## Workflow` section no longer claims "no PR required"

---

## Testing Strategy

### Unit Tests:

- None added — `packages/code-reviewer` has no test harness (stub `test` script) and this plan doesn't introduce one; see "What We're NOT Doing".

### Integration Tests:

- None automated — GitHub Actions workflow behavior is verified by real PR runs (see each phase's Manual Verification).

### Manual Testing Steps:

1. Add the `ANTHROPIC_API_KEY` secret (Phase 5) before testing any run end-to-end.
2. Open a real PR against `develop` with a small diff; confirm one comment + one correct outcome label appear.
3. Push a follow-up commit; confirm the outcome label disappears and no new run consumes budget.
4. Add the `ai-review:review` label; confirm a fresh run replaces the same comment and reapplies a label.
5. Push two commits within seconds of each other; confirm the Actions UI shows the first run cancelled, not both completing.
6. Temporarily break the run (e.g. unset the secret) to confirm the failure path: red check, no `ai-review:failed` label, error comment posted.

## Performance Considerations

Existing `maxBudgetUsd: 0.7` / `maxTurns: 20` caps in `review.ts` already bound per-run cost; this plan's trigger design (Phase 4) additionally bounds run *frequency* by not re-running on every push.

## Migration Notes

Not applicable — this is new workflow surface with no existing state to migrate.

## References

- Related research: `context/changes/ci-cd-code-review/research.md`
- Requirements: `context/changes/ci-cd-code-review/requirements.md`
- Secret provisioning precedent: `context/changes/deployment/deployment-plan.md:423-435`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Extend `review.ts` / `review-schema.ts` contract

#### Automated

- [x] 1.1 `npm ci` succeeds in `packages/code-reviewer` — 3c155db
- [x] 1.2 CLI runs end-to-end against a sample diff, producing valid JSON-only stdout — 3c155db

#### Manual

- [x] 1.3 Cost line appears on stderr, not stdout — 3c155db
- [x] 1.4 Review flags a diff touching a curated criterion (e.g. EF Core reference or raw CSS selector) — 3c155db

### Phase 2: Composite action core

#### Automated

- [x] 2.1 `action.yml` is valid composite-action YAML — 33b8c00

#### Manual

- [ ] 2.2 A real trigger produces a populated `review-result.json` with verdict, scores, and summary

### Phase 3: Composite action side-effects

#### Manual

- [ ] 3.1 A test PR produces exactly one bot comment and one correct outcome label
- [ ] 3.2 Re-triggering via `ai-review:review` edits the same comment and clears the trigger label
- [ ] 3.3 A forced failure fails the check without applying `ai-review:failed`, and posts the error comment

### Phase 4: Workflow wiring

#### Automated

- [ ] 4.1 Workflow YAML is syntactically valid

#### Manual

- [ ] 4.2 Opening a PR triggers only the `review` job
- [ ] 4.3 A push triggers only `clear-stale-label`, no new paid run
- [ ] 4.4 Adding `ai-review:review` triggers `review` again
- [ ] 4.5 Rapid successive pushes cancel the earlier in-flight run

### Phase 5: Docs & secret provisioning

#### Manual

- [ ] 5.1 `ANTHROPIC_API_KEY` is present in GitHub Actions secrets
- [ ] 5.2 `.claude/CLAUDE.md`'s `## Workflow` section reflects the new PR-based flow
