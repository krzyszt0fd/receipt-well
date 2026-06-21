<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Receipts List with Status (S-02)

- **Plan**: context/changes/receipts-list-with-status/plan.md
- **Scope**: Phase 1 of 3
- **Date**: 2026-06-21
- **Verdict**: REJECTED → both findings fixed during triage
- **Findings**: 1 critical, 1 warning, 0 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | PASS |
| Safety & Quality | FAIL (fixed) |
| Architecture | PASS |
| Pattern Consistency | WARNING (fixed) |
| Success Criteria | PASS |

## Findings

### F1 — Real JWT (with PII + tenant IDs) committed to receipt-well.http

- **Severity**: CRITICAL
- **Impact**: MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: receipt-well.http:2
- **Detail**: Commit 28f0021 (Phase 1's commit, unpushed — 3 ahead of origin/develop) filled the previously-empty `@token =` placeholder with a real bearer JWT used for manual testing. Decoded, it contained the Entra CIAM tenant ID/domain, the app's client/audience ID, the `azp` app ID, the user's `oid`, name, and email (PII) — violating both the global and backend CLAUDE.md security guardrails ("Never write... secrets, keys, tokens... or PII of any kind into any git-tracked file").
- **Fix A ⭐ Recommended**: Amend the unpushed commit to reset `@token =` to empty, removing the secret from history entirely (safe since not yet pushed).
- **Fix B**: Leave the commit as-is, add a new commit that empties the token (keeps history immutable, but the secret stays reachable in the old commit's tree).
- **Decision**: FIXED via Fix A. Token cleared in receipt-well.http; commit amended through several iterations (final SHA `1217bca`) as the fix and a second finding (F2) were folded in. Residual note: the original commit object is unreachable but not yet garbage-collected from local `.git` (expected, harmless — never pushed).

### F2 — ReceiptQueryService has no logging, unlike its siblings

- **Severity**: WARNING
- **Impact**: LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs
- **Detail**: Sibling services (`ReceiptConfirmService`, `ReceiptBlobService`) use source-generated `[LoggerMessage]` logging identity + entity IDs per backend CLAUDE.md ("Always use source-generated logging", "always log current identity ID and the entity ID being changed or accessed"). `ReceiptQueryService` took no logger and logged nothing.
- **Fix**: Added `ILogger<ReceiptQueryService>` via constructor injection, made the class `partial`, added an Info-level `[LoggerMessage]` logging `userId` and the returned receipt count.
- **Decision**: FIXED. Build verified zero warnings after the change; folded into the same amended commit.
