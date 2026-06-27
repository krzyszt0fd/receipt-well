# Test Plan Refresh 2026-06-27 — Plan Brief

> Full plan: `context/changes/test-plan-refresh-2026-06-27/plan.md`
> Research: `context/changes/test-plan-refresh-2026-06-27/research.md`

## What & Why

Bring `context/foundation/test-plan.md` in sync with what shipped since 2026-06-23: tag-search TDD tests delivered out-of-phase Risk #6 partial coverage, and three per-edit agent hooks plus a lefthook pre-commit gate replaced the earlier placeholder row. Without this update the Quality Gates table is inaccurate and Phase 4 looks fully open when it already has search-retrieval coverage.

## Starting Point

Test plan last reviewed 2026-06-23 (Phase 2 complete). PR #7 merged since then: added `ReceiptSearchTests.cs`, `ReceiptSearchEndpointTests.cs`, 5 frontend specs, and committed `.claude/check_cs_build.py`, `.claude/check_frontend_lint.py`, `.claude/check_csproj_restore.py`, and `lefthook.yml`. None of these appear in §3, §5, or §6.6.

## Desired End State

`context/foundation/test-plan.md` accurately describes current testing infrastructure: §3 Phase 4 names both the covered Risk #6 sub-risk and what remains; §5 lists four real gates (3 per-edit + 1 pre-commit); §6.6 captures the wildcard/analyzer trap and the Risk #6 split; §8 shows 2026-06-27.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
|----------|--------|------------------|--------|
| lefthook glob filter | Document gap, don't fix | Low priority; not blocking test-plan accuracy | Research |
| Frontend commit gate | Defer to Phase 5 | Phase 5 is the dedicated frontend quality-gates wiring phase | Research |
| Phase 4 split (4a/4b) | Keep combined | Normalization test is small; splitting adds a change folder for a one-file addition | Research / Plan |
| TagNormalizer automated test | Phase 4 obligation | No automated coverage today; requirement-derived oracle approach already specified in §2 | Research |

## Scope

**In scope:**
- §3 Phase 4 row — annotate covered vs. remaining Risk #6 sub-risks
- §5 Quality Gates — replace placeholder row with 3 per-edit + 1 pre-commit rows
- §6.6 — add "Tag-search + hooks (out-of-band, 2026-06-27)" note
- §8 Freshness Ledger — update date to 2026-06-27

**Out of scope:** lefthook glob filter fix, frontend commit gate, `TagNormalizer` tests, any other test-plan sections.

## Architecture / Approach

Four surgical edits to one markdown file in document order. No code changes. Research pre-determined all content; plan structures the edit sequence and success criteria.

## Phases at a Glance

| Phase | What it delivers | Key risk |
|-------|-----------------|----------|
| 1. Update test-plan.md | Accurate §3/§5/§6.6/§8 in one session | Inadvertent edit to another section; broken table alignment |

**Prerequisites:** None — research complete, decisions settled above.
**Estimated effort:** ~1 session, single phase.

## Open Risks & Assumptions

- §3 Phase 4 Status must remain "not started" — only the Goal annotation changes.
- Four new §5 rows must maintain pipe-count alignment with the existing table.

## Success Criteria (Summary)

- §5 no longer says "post-edit hook (run affected tests)"
- §3 Phase 4 names Risk #6 covered sub-risk and remaining obligations
- §6.6 has a "Tag-search + hooks" note with the three lessons captured
