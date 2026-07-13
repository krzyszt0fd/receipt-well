# Testing Access Control — Plan Brief

> Full plan: `context/changes/testing-access-control/plan.md`
> Research: `context/changes/testing-access-control/research.md`

## What & Why

Bootstrap the backend test project (none exists today) and prove the two highest-priority risks from `test-plan.md` §2: **Risk #1 (IDOR)** — a request authenticated as user A never reaches user B's receipts — and **Risk #2 (auth gate)** — a missing/invalid token is rejected with 401 and an authenticated-but-`oid`-less token with 403, never 200, never 500. A small hardening fix ships with the tests.

## Starting Point

Three endpoints (`staging-slot`, `confirm`, `GET /receipts`) are owner-scoped by the `oid` claim, behind a global secure-by-default `FallbackPolicy`. But there is **no backend test project**, and a token that authenticates without an `oid` claim currently 500s (because `GetUserId()` runs outside each handler's `try`) instead of being cleanly rejected.

## Desired End State

A `ReceiptWell.Tests` xUnit project builds clean and `dotnet test` is green, covering the claim→scope mapping, the no-token/no-`oid` gate, the cross-user confirm guard (with no side effects), and the list query scoping. The missing-`oid` path returns 403, the cookbook and oracle wording are updated, and the backend testing conventions are documented.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
| --- | --- | --- | --- |
| Missing-`oid` mechanism | `.RequireClaim("oid")` on the FallbackPolicy → **403** | One declarative line; an authenticated-but-claimless principal is forbidden in middleware before any handler — and the real policy is exercised by the test. | Plan |
| List-scoping test layer | **Filter-capture unit** (assert the query is scoped to the caller's `oid`) | Cheap, hermetic, CI-runnable; a fake can't honestly prove "B absent" (that needs a real index, which has no local emulator). | Plan |
| Substitute library | **NSubstitute** | Concise `.DidNotReceive()` is exactly what the confirm-403 "no side effects" oracle needs. | Plan |
| Integration scope | **Security boundaries only** | Highest signal-per-cost; happy-path confirm needs full client stubbing and overlaps later rollout phases. | Plan |
| `oid` fix oracle wording | Reword `test-plan.md` §Risk Response #2 to "401 no/invalid token / 403 no-`oid`" | Keeps the asserted 403 honest instead of mirroring the code later. | Plan |

## Scope

**In scope:** test-project bootstrap (CPM + lock files + sln); `public partial class Program`; `WebApplicationFactory` fixture with a configurable `TestAuthHandler` and offline boot; `.RequireClaim("oid")` fix; unit (GetUserId, filter-capture) + integration (gate, ownership) tests; cookbook/docs/sync.

**Out of scope:** happy-path successful-confirm test; real-index list test; `GET /receipts/{id}`/blob test (no such endpoint); 401-via-`OnTokenValidated`; Functions/frontend tests; CI-gate wiring; Azure SDK internals; infra/Terraform.

## Architecture / Approach

A custom `AuthenticationHandler` registered as the default scheme in `ConfigureTestServices` injects identity from request headers — one factory serves a chosen `oid`, an `oid`-less principal, or no auth (→401). The factory boots **offline**: it removes the `SearchIndexInitializer` hosted service and supplies dummy values for the three config keys read eagerly at startup, and replaces the Azure clients with NSubstitute substitutes. Every Phase 1 assertion is reachable without real infra (middleware short-circuits, confirm guard short-circuits before any Azure call, list scoping is a direct unit).

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Harness bootstrap | Test project + offline `WebApplicationFactory` + smoke test | Startup hits Azure (hosted service / eager config reads) if not neutralized |
| 2. Auth gate + fix | `.RequireClaim("oid")` + no-token→401 / no-`oid`→403 tests | Test becomes a tautology if it doesn't actually guard the fix |
| 3. Ownership | GetUserId unit + cross-user confirm→403 (no side effects) + filter-capture | Writing a mirror test for the list filter |
| 4. Cookbook + docs | `test-plan.md` §6 cookbook, status sync, backend CLAUDE.md "Testing" section | Cookbook too vague to write the next test from |

**Prerequisites:** .NET 9 SDK; access to add packages and run `dotnet restore` (lock files). No cloud access needed — tests are hermetic.
**Estimated effort:** ~2–3 sessions across 4 phases; Phase 1 is the heaviest (harness).

## Open Risks & Assumptions

- `SearchModelFactory.SearchResults<T>(...)` overload is version-sensitive — confirm against the pinned `Azure.Search.Documents` version when writing the filter-capture stub.
- The 403 mechanism diverges from the test-plan's original "401" wording; Phase 2 reconciles it by editing the oracle (not the test).
- Assumes the only startup `IHostedService` is `SearchIndexInitializer` (verified at this commit).

## Success Criteria (Summary)

- A no-token request → 401 and an authenticated-no-`oid` request → 403 on every receipt route; the 500 path is gone.
- A confirm as user A against user B's blob → 403 with no copy, no index write, no enqueue.
- `GET /receipts` provably scopes its query to the caller's `oid`; the whole suite runs green and hermetically via `dotnet test`.
