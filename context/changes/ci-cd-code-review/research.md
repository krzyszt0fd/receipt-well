---
date: 2026-07-30T15:32:38+02:00
researcher: Claude (10x-research)
git_commit: 31b989cf587f4fe4e35bb9df63500fa13dbfa344
branch: develop
repository: krzyszt0fd/receipt-well
topic: "First CI/CD workflow for PR code reviews (ci-cd-code-review)"
tags: [research, codebase, github-actions, code-reviewer, ci-cd]
status: complete
last_updated: 2026-07-30
last_updated_by: Claude (10x-research)
---

# Research: First CI/CD workflow for PR code reviews

**Date**: 2026-07-30T15:32:38+02:00
**Researcher**: Claude (10x-research)
**Git Commit**: 31b989cf587f4fe4e35bb9df63500fa13dbfa344
**Branch**: develop
**Repository**: krzyszt0fd/receipt-well

## Research Question

Ground a plan for `context/changes/ci-cd-code-review/requirements.md`: a GitHub Actions workflow, triggered on pull requests, that wraps the existing `packages/code-reviewer` Claude Agent SDK script as a composite action, posts a PR summary comment, and applies `ai-review:passed`/`ai-review:failed` labels, with on-demand retry via an `ai-review:review` label.

## Summary

- `packages/code-reviewer` (added 2026-07-29, commit `31b989c`) is a working standalone CLI — `review.ts` reads a diff from stdin, runs a Claude Agent SDK query with `Read`/`Grep`/`Glob` tools and `settingSources: []`, and returns a structured `pass`/`fail` verdict + 5 scores + Markdown summary via `REVIEW_JSON_SCHEMA`. It does **not** yet accept PR title/description as separate inputs, does not read `CLAUDE.md`/README rules itself (though it *can*, via its Read/Grep/Glob tools, since checkout gives it the full repo), and has no GitHub-side behavior (comments, labels) at all — that's all new surface area.
- This repo has **zero prior art** for PR-triggered workflows or code-review automation. All four existing `.github/workflows/*.yml` are `push`-to-`develop` deploy pipelines. The project's own `.claude/CLAUDE.md` currently states "Solo dev during MVP — commit directly to `develop`, no PR required" (line 20) — introducing this workflow is the repo's first step toward a PR-based flow, confirmed by the user to target **`develop`** (not `master`, which doesn't exist in this repo) as the PR base branch.
- GitHub secrets/vars are provisioned **manually** in this repo — Terraform (`infra/oidc.tf`) only federates Azure OIDC identities; it explicitly does not create GitHub secrets. The convention (per `infra/outputs.tf` and `context/changes/deployment/deployment-plan.md`) is: `terraform output` → paste into GitHub Settings → Secrets/Variables → Actions, documented in a `| Secret name | Value |` table. `ANTHROPIC_API_KEY` (needed by `@anthropic-ai/claude-agent-sdk`) has no Azure/Terraform counterpart and will be a purely manual secret, added the same way.
- No existing workflow declares `pull_request` triggers, `pull-requests: write` permission, or posts PR comments/labels — this workflow introduces all of that machinery fresh.
- `requirements.md` contains an unresolved `{{CR_CRITERIA}}` template placeholder in its "Code Review Criteria" section — this needs to be filled in (or explicitly deferred) before/during planning; it is not something research can resolve.
- The four CLAUDE.md files plus root/backend/frontend READMEs contain a large set of diff-checkable conventions (namespace, nullable annotations, Angular signals/OnPush, locator rules, security guardrails, etc.) that can seed `{{CR_CRITERIA}}` or the reviewer's system prompt — full breakdown below. A meaningful subset (build succeeds, tests pass, AXE/WCAG compliance, true test independence) is **not** diff-checkable by an LLM review step and should stay with existing CI/test jobs, not be assigned to this reviewer.
- The root CLAUDE.md's security guardrail ("never write secrets/hostnames/PII to git-tracked files") applies to the reviewer's own output too: since its PR comment is posted content, the review agent must flag-but-not-reproduce any secret/hostname/PII it encounters in a diff, not quote it back into the comment.

## Detailed Findings

### `packages/code-reviewer` — current capability and gaps

- [review.ts](packages/code-reviewer/src/review.ts) — CLI entry point. Reads diff from stdin only ([review.ts:4-9](packages/code-reviewer/src/review.ts#L4-L9)); no argument/env handling for PR title or description yet. Calls `query()` with `model: "claude-sonnet-5"`, `tools: ["Read", "Grep", "Glob"]`, `settingSources: []`, `permissionMode: "bypassPermissions"`, `maxBudgetUsd: 0.7`, `maxTurns: 20` ([review.ts:15-27](packages/code-reviewer/src/review.ts#L15-L27)). Output is printed as JSON to stdout ([review.ts:46](packages/code-reviewer/src/review.ts#L46)) — a workflow step will need to capture/parse this to drive labels + PR comment.
- [review-schema.ts](packages/code-reviewer/src/review-schema.ts) — `SYSTEM_PROMPT` (Polish-language) instructs a 5-criterion 1–10 score (implementation correctness, idiomaticity, complexity, test-risk coverage, security/safety) plus a binding `pass`/`fail` verdict and a Markdown summary "ready to post as a PR comment" ([review-schema.ts:3-20](packages/code-reviewer/src/review-schema.ts#L3-L20)). Comment at [review-schema.ts:9-11](packages/code-reviewer/src/review-schema.ts#L9-L11) explains scores are `z.number()` not `z.number().min(1).max(10)` because Anthropic's structured-output rejects `minimum`/`maximum` on integer JSON Schema types — the range is enforced only via prompt/description, not schema. `REVIEW_JSON_SCHEMA` uses `target: "draft-07"` ([review-schema.ts:23](packages/code-reviewer/src/review-schema.ts#L23)).
- `package.json` — standalone npm project (no root-level `package.json`/workspaces; `packages/code-reviewer` is self-contained). `"review": "tsx src/review.ts"` script. Deps: `@anthropic-ai/claude-agent-sdk ^0.3.220`, `zod ^4.4.3`; devDeps: `@types/node ^26.1.2`, `tsx ^4.23.1`. `package-lock.json` is committed (npm ci-able); `node_modules` is gitignored.
- Node version signal: `@types/node ^26.1.2` suggests authoring against Node 26 types, but nothing in the package enforces that at runtime — dependency `engines` fields in the lockfile only require `>=18`. The local dev machine runs Node v24.18.0. Existing workflows use `actions/setup-node@v4` with `node-version: lts/*` unpinned ([frontend-deploy.yml:24](.github/workflows/frontend-deploy.yml#L24), [e2e-tests.yml:18](.github/workflows/e2e-tests.yml#L18)) — reasonable to reuse `lts/*` here too, but worth a quick `npm ci` smoke-check in CI since the authored types target a newer major than current LTS.
- No test script exists for `code-reviewer` (`"test": "echo \"Error: no test specified\" && exit 1"`) — nothing to wire into a CI test step for this package itself.

### Existing GitHub Actions conventions (reference table)

| File | Trigger | Branches | Paths filter | `permissions:` | Secrets | Vars |
|---|---|---|---|---|---|---|
| [backend-deploy.yml](.github/workflows/backend-deploy.yml) | `push` | `develop` | `src/backend/**`, `!.../Functions/**`, self | `id-token: write`, `contents: read` | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | — |
| [frontend-deploy.yml](.github/workflows/frontend-deploy.yml) | `push` | `develop` | `src/frontend/**`, self | `id-token: write`, `contents: read` | `AZURE_STATIC_WEB_APPS_API_TOKEN`, `GITHUB_TOKEN` | `BACKEND_API_URL`, `AUTHORITY`, `KNOWN_AUTHORITY`, `MSAL_CLIENT_ID`, `API_SCOPE` |
| [functions-deploy.yml](.github/workflows/functions-deploy.yml) | `push` | `develop` | `src/backend/ReceiptWell.Functions/**`, `src/backend/ReceiptWell.Core/**`, self | `id-token: write`, `contents: read` | `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | `FUNCTION_APP_NAME` |
| [e2e-tests.yml](.github/workflows/e2e-tests.yml) | `push` | `develop` | none | **none declared** | `E2E_TEST_USERNAME`, `E2E_TEST_PASSWORD` | `E2E_AUTHORITY`, `AUTHORITY`, `KNOWN_AUTHORITY`, `MSAL_CLIENT_ID`, `API_SCOPE` |

None declare `pull_request` triggers or `pull-requests: write` — the new workflow is the first to need both, plus a step to capture the diff (`git diff` against the PR base) and post-process the reviewer's JSON output into a comment + labels.

### Secrets/OIDC provisioning pattern

- [infra/oidc.tf](infra/oidc.tf) provisions the Azure AD app registration, service principal, and a single federated identity credential scoped to `repo:${github_org}/${github_repo}:ref:refs/heads/${github_deploy_branch}` (default `develop`, [infra/variables.tf:60-64](infra/variables.tf#L60-L64)) — this is Azure-OIDC-specific machinery and does not apply to `ANTHROPIC_API_KEY` (not an Azure identity).
- [infra/outputs.tf:1-11,53-66](infra/outputs.tf) and [context/changes/deployment/deployment-plan.md:423-435](context/changes/deployment/deployment-plan.md#L423-L435) both document the same manual step: run `terraform output`, copy values into GitHub → Settings → Secrets and variables → Actions. There is **no IaC path** for GitHub secrets in this repo — `ANTHROPIC_API_KEY` will need the same manual add, documented the same way (a `| Secret name | Value |` table in the plan).
- `.env.example` is the only precedent for documenting env vars in this repo (gitignored `.env`, header comment, per-var inline explanation) — useful style reference for documenting `ANTHROPIC_API_KEY`'s purpose in a plan/README, but it's a local-dev convention, not a GH-secret one.

### Project rules relevant to review criteria (feeds `{{CR_CRITERIA}}`)

Full per-file breakdown (all 4 CLAUDE.md + 3 README.md files read in full; `src/e2e-tests/README.md` does not exist):

**Root `.claude/CLAUDE.md`**
- Security guardrails ([CLAUDE.md:11-16](.claude/CLAUDE.md#L11-L16), verbatim): never write hosting infra details, secrets/keys/tokens/passwords/connection strings, or PII to any git-tracked file. Directly relevant to the reviewer's own comment output, not just the diff.
- "No SQL database, no EF Core" ([CLAUDE.md:35](.claude/CLAUDE.md#L35)) — checkable: flag EF Core packages/DbContext or raw SQL in a diff.
- Workflow line ([CLAUDE.md:20](.claude/CLAUDE.md#L20)): "commit directly to develop, no PR required" — will need updating once this workflow ships, since it's now false.

**`src/backend/.claude/CLAUDE.md`**
- Config/secrets: `appsettings*.json` must stay structural/localhost-only (Azurite exception allowed); secrets via `dotnet user-secrets`; C# must read resource names/URLs via `IConfiguration`, never hardcode (lines 9-18).
- Conventions: namespace root `ReceiptWell`; nullable reference types annotated; implicit usings; zero-warning build (not diff-checkable — requires a real build); central package management, no per-`.csproj` `Version` attributes (lines 22-32).
- Logging: identity+entity IDs logged; source-generated logging; specific log-level mapping by failure type; no request/response logging (lines 37-45).
- Testing: `ReceiptWellWebFactory` eager-config-read contract; `TestAuthHandler` header contract (`X-Test-Oid`/`X-Test-No-Oid`); NSubstitute `DidNotReceiveWithAnyArgs()` pattern; filter assertions must check field+id, not exact string (explicit anti-pattern) (lines 49-57).
- Docs: `.http` collection must be updated when endpoint contracts change (lines 59-61).

**`src/frontend/.claude/CLAUDE.md`**
- Security: same secrets/PII rule as root; `environment.ts` localhost-only; `environment.prod.ts` build-time placeholders only (lines 4-17).
- TypeScript: strict typing, avoid `any`/prefer `unknown` (lines 21-23).
- Angular: standalone components (no explicit `standalone: true`), signals for state, lazy-loaded routes, no `@HostBinding`/`@HostListener`, `NgOptimizedImage` (lines 27-33).
- Accessibility: AXE/WCAG AA — **not reliably diff-checkable** by an LLM without rendering (lines 37-38).
- Components/state/templates/services: `input()`/`output()` functions, `computed()`, `OnPush`, no `ngClass`/`ngStyle`, native control flow (`@if`/`@for`), no `.mutate()` on signals, `inject()` not constructor injection (lines 42-70).
- Note: a `PostToolUse` hook already runs ESLint + `tsc --noEmit` on every frontend edit (per `src/frontend/README.md:76`) — lint/type errors are already caught before a PR exists, so this is lower-priority for the AI reviewer to re-check.

**`src/e2e-tests/CLAUDE.md`**
- Locators: `getByRole`/`getByLabel`/`getByText` first, `getByTestId` fallback only, never CSS/XPath/DOM-structure (lines 3-5) — directly greppable.
- No `page.waitForTimeout()` (lines 7-8) — directly greppable.
- `storageState`-only auth, no UI login per test (line 10); mock only the API boundary via `page.route()` (lines 11-12); `waitForResponse()` must be set up before the triggering action (lines 13-14).
- Test independence and "assert business outcome not implementation detail" are semantic/structural judgment calls, not pure grep — plausible for an LLM to reason about but not mechanically certain.

**Hard-to-check-from-a-diff-alone rules** (flag as out of scope for this reviewer, belongs to existing CI/test jobs instead): backend zero-warning build and "was `dotnet restore` actually run"; frontend AXE/WCAG AA compliance; e2e test independence/no-shared-state; anything requiring the app to actually run.

## Code References

- `packages/code-reviewer/src/review.ts:4-46` — CLI entry, diff-from-stdin, Agent SDK query call, output handling
- `packages/code-reviewer/src/review-schema.ts:3-25` — system prompt, Zod schema, JSON Schema conversion
- `packages/code-reviewer/package.json:1-22` — deps, `review`/`test` scripts
- `.github/workflows/backend-deploy.yml`, `frontend-deploy.yml`, `functions-deploy.yml`, `e2e-tests.yml` — existing push-to-develop workflow conventions (permissions, secrets, vars, setup-node usage)
- `infra/oidc.tf`, `infra/outputs.tf`, `infra/variables.tf:60-64` — Azure OIDC federation scoped to `develop`; GitHub secrets are NOT Terraform-managed
- `.claude/CLAUDE.md:11-20,35` — security guardrails, solo-dev/no-PR line (now stale), no-SQL rule
- `src/backend/.claude/CLAUDE.md`, `src/frontend/.claude/CLAUDE.md`, `src/e2e-tests/CLAUDE.md` — full rule sets above
- `context/changes/ci-cd-code-review/requirements.md` — the drafted spec this research grounds, including the unresolved `{{CR_CRITERIA}}` placeholder

## Architecture Insights

- **Composite-action-in-`packages/`** is a new pattern for this repo — no existing composite action exists anywhere (`.github/actions/` doesn't exist). Requirements.md explicitly wants the composite action to live at `packages/code-reviewer` (not `.github/actions/`), referenced from the workflow via a relative `uses: ./packages/code-reviewer`. Since `packages/code-reviewer` is not part of an npm workspace, the composite action's steps will need their own `npm ci`/`working-directory: packages/code-reviewer` setup, mirroring how `frontend-deploy.yml` and `e2e-tests.yml` scope `npm ci` to their own `working-directory`.
- **Diff generation isn't solved yet anywhere in this repo.** All existing workflows check out and build/test the full repo; none compute a `git diff` against a PR base. The new workflow will need `actions/checkout` with enough `fetch-depth` (0 or at least 2) to diff `${{ github.event.pull_request.base.sha }}` against the PR head, since `review.ts` expects a diff via stdin.
- **GITHUB_TOKEN scope is currently minimal.** Only `frontend-deploy.yml` references `secrets.GITHUB_TOKEN` at all (for the Static Web Apps deploy action), and no workflow declares `pull-requests: write`. The new workflow needs that permission to post comments and manage labels — this is new, not a copy-paste from elsewhere.
- **Cost/budget precedent exists in code, not yet in workflow design.** `review.ts` already caps `maxBudgetUsd: 0.7` and `maxTurns: 20` per invocation — the requirements.md's "cost tradeoff" question about including the PR description is best evaluated against this existing budget ceiling rather than an unbounded one.

## Historical Context (from prior changes)

- No prior `context/changes/**` or `context/archive/**` entry discusses GitHub Actions design decisions for code review, or a PR-based branching model — confirmed by full-text search across both trees. The closest CI history is testing/deployment-only: `git log --oneline --all -- .github/workflows` shows commits like `ci(backend): run dotnet test before publish` and `ci(e2e): scope CI run to Chrome only`, unrelated to code-review automation.
- The "commit directly to develop, no PR required" line in root CLAUDE.md dates to `f4b968b` (2026-05-25), right after initial scaffolding — a foundational MVP-era default, not a deliberated decision from any prior change. This change is the first to challenge it.
- `packages/code-reviewer` itself was added whole-cloth in `31b989c` (2026-07-29) with no preceding design change — its commit message documents the isolation rationale (`settingSources: []`, bypassed permissions) but nothing about how it would eventually be wired into CI.
- `context/changes/deployment/deployment-plan.md:423-435` is the only prior artifact documenting the "manual GitHub secret" convention — reusable directly for documenting `ANTHROPIC_API_KEY`.
- Sibling active changes: `context/changes/bootstrap-verification/`, `context/changes/deployment/` (no CI/code-review overlap found). Archive: 13 completed changes, none CI/code-review-related by title or content.

## Related Research

None — this is the first research document for this topic area.

## Open Questions

1. **`{{CR_CRITERIA}}` placeholder is unresolved** in `requirements.md` — needs explicit content (which of the CLAUDE.md rules above become reviewer-enforced criteria vs. left to existing lint/test/build CI) or an explicit decision to defer entirely to the LLM's judgment plus `review-schema.ts`'s 5 fixed criteria.
2. **PR description inclusion is an open cost tradeoff** (flagged verbatim in requirements.md) — decide against the existing `maxBudgetUsd: 0.7` / `maxTurns: 20` ceiling in `review.ts`.
3. **CLAUDE.md's "no PR required" line is now stale** once this workflow ships — plan should include updating `.claude/CLAUDE.md:20`.
4. **Label lifecycle on retry** — requirements.md specifies `ai-review:review` triggers a retry and `ai-review:passed`/`ai-review:failed` are the outcome labels, but doesn't say whether the previous outcome label (and the `ai-review:review` trigger label itself) should be removed automatically after a re-run; needs a decision before planning the labeling step.
5. **Node version for the new workflow's `setup-node` step** — `@types/node ^26.1.2` suggests Node 26, but nothing enforces it at runtime (lockfile `engines` only require `>=18`, local dev machine runs v24.18.0); confirm whether `lts/*` (current convention) is acceptable or whether the version should be pinned.
6. **PR base branch is `develop`** (confirmed) — this reverses the "no PR required" MVP default; worth flagging in the plan as a deliberate workflow-model change, not just a CI addition.
