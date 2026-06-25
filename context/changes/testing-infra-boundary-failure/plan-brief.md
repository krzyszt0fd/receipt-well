# Infra-boundary Failure Shape Tests (Phase 2) — Plan Brief

> Full plan: `context/changes/testing-infra-boundary-failure/plan.md`
> Research: `context/changes/testing-infra-boundary-failure/research.md`

## What & Why

Add integration tests proving **Risk #7**: a thrown Blob/Queue/Search dependency error surfaces a **clean, honest 5xx** (RFC 7807), never a silent success or a dishonest 200 that masks a failed side effect. The team has lived this incident (opaque 500, no logs) — these tests pin the failure *shape* and the side-effect commit boundary so a future regression is caught immediately. Test-only; no production code changes.

## Starting Point

All three receipt endpoints already translate exceptions to RFC 7807 500s via inline `try/catch` (`Program.cs:140–215`). `POST /receipts/confirm` is a non-atomic 4-step sequence (copy → search → queue → cleanup) where later failures leave earlier commits in place. The Phase 1 `ReceiptWellWebFactory` harness with NSubstitute Azure-client substitutes exists and boots the app offline.

## Desired End State

`dotnet test` runs new tests that, per failure, assert status + RFC 7807 body shape + the exact side-effect boundary (which clients did/didn't get called). The fire-and-forget staging-cleanup 200 is documented and pinned. Test-plan §3 Phase 2 reads `complete`; §6 cookbook carries the reusable recipe; the §5 CI gate is active (already wired into `dotnet test`).

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Test layer | Integration only (factory + NSubstitute) | Real infra can't easily force a mid-sequence failure; substitutes can, cheaply | Research |
| Extra cases beyond the core 5 | Add confirm Step-1 (blob-copy) fail; skip staging-slot SAS-fail | Completes the confirm step matrix while the chain is already built; SAS-fail descoped | Plan |
| Honest-5xx assertion depth | Status + side effects + RFC 7807 body shape | Pins the "opaque vs honest" half of Risk #7, not just the status int | Plan |
| Confirm-flow setup | Shared `ConfirmFlowHarness` builder | Writes the brittle SAS/magic-byte/chain setup once; tests declare only the failing step | Plan |
| SAS branch in harness | Bypass via `CanGenerateSasUri → true` | Avoids the `DelegationTokenProvider`/delegation-key base64-signing rabbit hole | Plan |
| Per-class accumulation | `ClearReceivedCalls()` + re-stub per method | Keeps `DidNotReceive` assertions honest under the shared per-class fixture | Plan |

## Scope

**In scope:** confirm Steps 1–4 failure shapes, staging-slot upload fail, list search fail, a positive-control happy-path 200, the shared harness + RFC 7807 helper, cookbook + status sync.

**Out of scope:** production code changes (global exception middleware), Azure Functions/poison-path tests (Phase 4), staging-slot SAS-fail case, logging assertions, new CI wiring, Stryker as a gate.

## Architecture / Approach

Build the brittle confirm-flow setup once as a `ConfirmFlowHarness` test helper with a `FailAt(step)` switch, prove it with a positive-control 200 test, then layer failure cases that each assert status + body + side-effect boundary. Single-step endpoints (staging-slot, list) need no harness. Close with docs/sync.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Infra & positive control | `ConfirmFlowHarness` + RFC 7807 helper + happy-path 200 test | Chained-substitute / SAS / version-sensitive model-factory brittleness |
| 2. Failure-shape tests | Confirm Steps 1–4 + staging-slot + list failure tests | Per-class call accumulation; asserting the real commit boundary (not an early exit) |
| 3. Docs & sync | Cookbook recipe, §3 status→complete, §6.6 note | Doc drift |

**Prerequisites:** Phase 1 harness (`ReceiptWellWebFactory`) exists; `Azure.Core` (transitive) provides `RequestFailedException`.
**Estimated effort:** ~1–2 sessions across 3 phases; Phase 1 is the bulk of the work.

## Open Risks & Assumptions

- Model-factory argument lists (`BlobsModelFactory`/`SearchModelFactory`/`QueuesModelFactory`) shift between Azure SDK versions — confirm against `Directory.Packages.props` if a builder fails to compile.
- Assumes the `CanGenerateSasUri → true` bypass faithfully reaches `SyncCopyFromUriAsync`; if Azure's `BlobClient` doesn't honor the stubbed virtual in this SDK version, fall back to stubbing the delegation-key path.

## Success Criteria (Summary)

- A thrown Blob/Queue/Search error returns an honest RFC 7807 500 with the asserted side-effect boundary, for every tested path.
- The intentional fire-and-forget cleanup 200 is pinned (regressing it to swallow a critical step turns a test red).
- `dotnet test` green with zero warnings; test-plan Phase 2 = `complete`.
