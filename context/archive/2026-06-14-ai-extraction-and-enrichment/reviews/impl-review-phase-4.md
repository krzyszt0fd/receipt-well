<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: AI Extraction & Enrichment (S-03)

- **Plan**: context/changes/ai-extraction-and-enrichment/plan.md
- **Scope**: Phase 4 of 6 (Azure Functions Extraction Worker)
- **Date**: 2026-06-17
- **Verdict**: APPROVED
- **Findings**: 0 critical, 1 warning, 3 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | PASS |
| Success Criteria | PASS |

## Findings

### F1 — Blob-download failures always classified terminal, even transient ones

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: src/backend/ReceiptWell.Functions/ExtractReceiptFunction.cs:52-58
- **Detail**: The blob-download `catch (RequestFailedException ex)` block unconditionally set `Status = error` and returned — no transient/terminal split, unlike the LLM call three lines below which has a dedicated `IsTransient` helper. A transient Blob Storage hiccup (503/throttling/network blip) would have been permanently parked at `error`, with no manual-retry UI in scope to recover it.
- **Fix**: Reuse the existing `IsTransient(ex)` helper on the blob catch via a `catch (RequestFailedException ex) when (IsTransient(ex)) { ...; throw; }` guard ahead of the terminal catch-all.
  - Strength: Helper already existed in this file for the LLM call — zero new concepts.
  - Tradeoff: None meaningful.
  - Confidence: HIGH.
  - Blind spot: None significant.
- **Decision**: FIXED — added a transient-guarded catch clause and a `LogTransientBlobDownloadFailure` log message above the terminal catch-all.

### F2 — HttpRequestException treated as always-transient, no status check

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: src/backend/ReceiptWell.Functions/ExtractReceiptFunction.cs:87 (pre-fix line)
- **Detail**: `IsTransient`'s `HttpRequestException => true` branch ignored the exception's own `StatusCode`. A wrapped 400 would be retried up to `maxDequeueCount` before landing on the poison queue, instead of failing fast (no receipt left stuck either way).
- **Fix**: Check `HttpRequestException.StatusCode` when present; only treat as transient when null or ≥500/429.
- **Decision**: FIXED — `IsTransient` now pattern-matches `HttpRequestException { StatusCode: null }` as transient and checks `>= InternalServerError || == TooManyRequests` otherwise.

### F3 — Unplanned supporting changes (VS Code configs, CLAUDE.md exception, appsettings defaults, frontend script)

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Scope Discipline
- **Location**: .vscode/*, src/backend/.claude/CLAUDE.md, src/backend/ReceiptWell.Web/appsettings.json, src/frontend/package.json
- **Detail**: None of these files were named in Phase 4's "Changes Required," but all are verified benign and directly enabling: VS Code launch/task configs to run+debug Web API + Functions + frontend together; a narrowly-scoped CLAUDE.md "Exception — Azurite" carve-out (verified: only well-known, publicly documented Azurite constants, no real resource identifiers or secrets); matching appsettings.json defaults; one frontend npm script wired to the new VS Code task.
- **Fix**: Add a one-line addendum to plan.md Phase 4 noting the local-dev tooling additions.
- **Decision**: SKIPPED — changes are self-evidently benign per user.

### F4 — NuGet lock files claimed by CLAUDE.md but none exist anywhere in the repo

- **Severity**: 👁️ OBSERVATION
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Success Criteria
- **Location**: src/backend/Directory.Packages.props (repo-wide, pre-existing — not introduced by Phase 4)
- **Detail**: CLAUDE.md stated the project uses NuGet lock files with content hashes, but no `packages.lock.json` existed anywhere and `RestorePackagesWithLockFile` wasn't set. Predated Phase 4 (Phases 1-3 made the same Progress claim); the new Functions project inherited the same gap.
- **Fix A ⭐ Recommended (chosen)**: Enable `RestorePackagesWithLockFile=true` in `Directory.Build.props` and commit the generated lock files.
  - Strength: Makes the CLAUDE.md claim true; reproducible restores, matters given pre-GA AI/Functions packages.
  - Tradeoff: One more file per project to keep in sync.
  - Confidence: MED.
- **Fix B**: Drop the lock-file sentence from CLAUDE.md to match actual practice.
- **Decision**: FIXED via Fix A — `RestorePackagesWithLockFile=true` added to `src/backend/Directory.Build.props`; `dotnet restore --force-evaluate` generated `packages.lock.json` for `ReceiptWell.Web`, `ReceiptWell.Core`, and `ReceiptWell.Functions`.

## Notes

- Automated verification: `dotnet restore src/backend/ReceiptWell.sln` passed. `dotnet build` initially failed locally only because `ReceiptWell.Core.dll` was locked by already-running `ReceiptWell.Web`/Functions host dev processes — an environment artifact, not a code defect.
- Strongest parts of this phase: the two highest-risk plan details — the `DateOnly?`→`DateTimeOffset?` UTC-midnight boundary and the two-function poison-queue design — were both implemented exactly as specified, and partial-merge documents correctly avoid clobbering unrelated fields.
