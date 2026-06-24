# Testing Access Control — Backend Test Harness + Access-Control Coverage (Test Plan Phase 1)

## Overview

Bootstrap the backend test project (none exists today) and prove the two highest-priority risks from `test-plan.md` §2 hold:

- **Risk #1 (IDOR):** a request authenticated as user A never reaches user B's receipts — ownership is checked beyond "is authenticated."
- **Risk #2 (auth gate):** a missing/invalid token → **401**, and an authenticated-but-`oid`-less token → **403** (never 200, never 500), on every protected receipt route.

A behavior fix ships with the tests: today a token that authenticates but carries no `oid` claim yields an unhandled `InvalidOperationException` → **500** (because `GetUserId()` runs outside each handler's `try`). This change adds `.RequireClaim("oid")` to the global `FallbackPolicy`, so a claimless principal is rejected with **403 in middleware** before any handler runs.

## Current State Analysis

- **No backend test project exists.** `ReceiptWell.sln` contains only `ReceiptWell.Web`, `ReceiptWell.Core`, `ReceiptWell.Functions`.
- **Identity is a single key (`oid`).** `ClaimsPrincipalExtensions.GetUserId()` (`ClaimsPrincipalExtensions.cs:7-8`) reads `oid` and throws if absent; it is threaded as `userId` into all three handlers (`Program.cs:145,165,203`). `oid` resolves by short name only because `MapInboundClaims = false` (`Program.cs:47`, recorded in `lessons.md`).
- **Auth gate is global, secure-by-default.** The `FallbackPolicy` requires an authenticated user (`Program.cs:51-56`); a new endpoint is protected unless it explicitly `.AllowAnonymous()`. The only opt-outs are `/health` (`Program.cs:137`) and the dev-only OpenAPI doc (`Program.cs:112`).
- **Ownership is enforced at two layers** for upload: write-only single-blob SAS at staging, and the prefix guard at confirm (`ReceiptConfirmService.cs:38-42`, `Forbidden` → `Results.Forbid()` 403 at `Program.cs:173`). The confirm guard runs **before any Azure client call**.
- **List scoping** lives in the Search filter `UserId eq '{userId}'` (`ReceiptQueryService.cs:12-19`), not C# post-filtering.
- **The 500 divergence:** `GetUserId()` is called outside the `try` in all three handlers; a claimless-but-valid token throws → 500, while the Risk #2 oracle requires a clean reject.
- **Bootstrap constraints:** `WebApplicationFactory<Program>` needs `public partial class Program`; Central Package Management is ON (versions in `Directory.Packages.props`, references omit `Version`); `RestorePackagesWithLockFile=true` (run `dotnet restore` after editing versions); `Directory.Build.props` applies `net9.0` + nullable + `RootNamespace=ReceiptWell` to the test project too; zero-warning policy.

## Desired End State

A `ReceiptWell.Tests` xUnit project builds clean and `dotnet test` is green, covering:

- `GetUserId()` returns `oid` when present and throws when absent (claim→scope mapping unit).
- No-token request → **401**; authenticated-no-`oid` request → **403** — on each of the three receipt routes (auth gate integration).
- Cross-user confirm (`stagingBlobName` prefix ≠ caller's `oid/`) → **403**, performing **no** blob copy, **no** index write, **no** enqueue (ownership integration + side-effect assertion).
- `GET /receipts` builds a Search query scoped to the caller's `oid` (filter-capture unit).

The fix is live (`.RequireClaim("oid")`), `test-plan.md` §6 cookbook and §Risk Response #2 are updated, and the backend testing conventions are documented.

### Key Discoveries:

- `ClaimsPrincipalExtensions.cs:7-8` — `oid` extraction; throws when missing (the claim→scope contract).
- `Program.cs:51-56` — global `FallbackPolicy`; this is where `.RequireClaim("oid")` lands.
- `ReceiptConfirmService.cs:38-42` — prime IDOR guard, short-circuits before any Azure client.
- `Program.cs:88-89,65` — `AzureSearch:ServiceUri`, `AzureSearch:ApiKey`, `AzureStorage:ExtractionQueueName` are read **eagerly at startup** (outside any lambda) — the test host must supply dummy values or boot throws.
- `Program.cs:98` — `AddHostedService<SearchIndexInitializer>()` runs at startup and resolves the Search clients → would hit Azure; the test factory must remove it.
- `ReceiptQueryService.cs:12-19` — list scoping filter, the filter-capture target.

## What We're NOT Doing

- **No happy-path successful-confirm test** (valid confirm → 200). That needs full Blob+Search+Queue stubbing for a path that is not the Risk #1/#2 concern; it overlaps Phase 2/3 of the rollout. (Q4 decision: security boundaries only.)
- **No real-index list test.** Proving "user B's docs are absent" end-to-end needs a live Azure Search service (no local emulator exists). Phase 1 pins the *query scoping decision* via filter-capture; true end-to-end scoping is validated manually / deferred. (Q2 decision.)
- **No `GET /receipts/{id}` / blob-serving test.** That endpoint does not exist at this commit; flagged as a future trigger (Risk #1 detail/blob face).
- **No 401-via-`OnTokenValidated` mechanism.** We chose policy-layer `.RequireClaim("oid")` → 403, not auth-layer rejection → 401. (Q1 decision.)
- **No Functions / frontend tests, no CI-gate wiring.** Those are rollout Phases 2–5.
- **No Azure SDK internals tested**, no infra/Terraform (per `test-plan.md` §7).

## Implementation Approach

Four phases: stand up the harness (environment), then the auth gate + fix (Risk #2), then ownership (Risk #1), then cookbook/docs/sync. Tests follow `test-plan.md` §1 cost×signal — every Phase 1 assertion is reachable without real infra: the no-token/no-`oid` paths short-circuit in middleware, the confirm-403 path short-circuits before any Azure call, the list scoping is a direct unit on `ReceiptQueryService` with a substituted `SearchClient`, and `GetUserId()` is a pure unit.

The test harness injects identity with a custom `AuthenticationHandler` registered as the default scheme in `ConfigureTestServices`. It authenticates from request headers so one factory serves every case: a chosen `oid`, an `oid`-less principal, or no authentication at all (no token → 401). This exercises the **real** authorization policy (`RequireAuthenticatedUser` + `RequireClaim("oid")`) — only the authentication step is simulated — so the 401/403 distinction comes from production code.

## Critical Implementation Details

- **Eager config reads gate startup.** `Program.cs:65,88-89` read `AzureStorage:ExtractionQueueName`, `AzureSearch:ServiceUri`, `AzureSearch:ApiKey` with the null-forgiving `!` *outside* any DI lambda. The test factory MUST supply dummy values (in-memory config) for at least these three, or `WebApplicationFactory` boot throws a `NullReferenceException`/`ArgumentNullException` before any test runs. Lambda-scoped reads (`BlobServiceUri`, `QueueServiceUri`, `IndexName`) are lazy and only matter when those clients are resolved.
- **Remove the hosted service.** `AddHostedService<SearchIndexInitializer>()` (`Program.cs:98`) runs on host start and resolves `SearchIndexClient`/`SearchClient`, calling Azure. `ConfigureTestServices` must remove that `IHostedService` descriptor (it is the only explicit hosted service) so the test host boots offline.
- **`SearchModelFactory` overload is version-sensitive.** `SearchResults<T>` has no public constructor; build it with `SearchModelFactory.SearchResults<T>(...)` for the filter-capture stub's return value. The exact overload (and whether `rawResponse`/`Response.FromValue` tolerate a null response) **shifts between SDK versions** — confirm against the pinned `Azure.Search.Documents` version in `Directory.Packages.props` at implementation time. The factory pattern is stable; the argument list is not.
- **403 vs 401 ordering in the policy.** With `RequireAuthenticatedUser().RequireClaim("oid")`, an unauthenticated request fails the first requirement → **401 challenge**; an authenticated principal missing `oid` passes authentication but fails the claim → **403 forbid**. Both come from the real policy — assert both.

---

## Phase 1: Backend Test Harness Bootstrap

### Overview

Create the `ReceiptWell.Tests` xUnit project, wire it into the solution under CPM + lock files, make `Program` referencable, and build a `WebApplicationFactory` fixture that boots offline with a configurable test identity. Ends with a smoke test proving the host starts.

### Changes Required:

#### 1. Test package versions

**File**: `src/backend/Directory.Packages.props`

**Intent**: Declare the test stack centrally so the test `.csproj` can reference packages without `Version` attributes.

**Contract**: Add `<PackageVersion>` entries for `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.AspNetCore.Mvc.Testing`, and `NSubstitute`. Pin latest stable versions compatible with `net9.0`; align `Microsoft.AspNetCore.Mvc.Testing` to the `9.0.x` band matching the framework (`9.0.4` is used elsewhere). Run `dotnet restore` afterward to refresh `packages.lock.json`.

#### 2. Test project

**File**: `src/backend/ReceiptWell.Tests/ReceiptWell.Tests.csproj`

**Intent**: A `Microsoft.NET.Sdk` test project referencing `ReceiptWell.Web`, inheriting `Directory.Build.props`.

**Contract**: `<IsPackable>false</IsPackable>`, `<IsTestProject>true</IsTestProject>`; `<ProjectReference Include="..\ReceiptWell.Web\ReceiptWell.Web.csproj" />`; `<PackageReference>` entries (no `Version`) for the five packages above. No `TargetFramework`/`Nullable`/`RootNamespace` — those come from `Directory.Build.props`.

#### 3. Solution wiring

**File**: `src/backend/ReceiptWell.sln`

**Intent**: Register the test project so `dotnet test` and the build discover it.

**Contract**: `dotnet sln add ReceiptWell.Tests/ReceiptWell.Tests.csproj`.

#### 4. Global usings

**File**: `src/backend/ReceiptWell.Tests/GlobalUsings.cs`

**Intent**: Project-wide test usings per backend CLAUDE.md (test framework, assertion, NSubstitute, project-wide test helpers only).

**Contract**: `global using Xunit;`, `global using NSubstitute;`, and the test-infrastructure namespace. Nothing else.

#### 5. Make `Program` referencable

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Top-level-statement `Program` is implicitly `internal`; `WebApplicationFactory<Program>` needs a public type.

**Contract**: Append `public partial class Program { }` at the end of the file.

#### 6. Test authentication handler

**File**: `src/backend/ReceiptWell.Tests/Infrastructure/TestAuthHandler.cs`

**Intent**: Inject a controllable identity so one factory serves every auth case without a real CIAM token.

**Contract**: An `AuthenticationHandler<AuthenticationSchemeOptions>` for a `"Test"` scheme. It reads request headers to decide the outcome: a header carrying an `oid` → `AuthenticateResult.Success` with a `ClaimsPrincipal` holding that `oid`; a header signalling "authenticate but omit `oid`" → success with no `oid` claim; no test header → `AuthenticateResult.NoResult()` (models "no token" → the fallback policy challenges with 401). Keep claim short-names (no `MapInboundClaims` remapping in test).

#### 7. Web application factory

**File**: `src/backend/ReceiptWell.Tests/Infrastructure/ReceiptWellWebFactory.cs`

**Intent**: Boot the real app offline with the test scheme as default and no Azure contact at startup.

**Contract**: `WebApplicationFactory<Program>` overriding `ConfigureWebHost`. In `ConfigureAppConfiguration`, add in-memory dummy values for the eager-read keys (`AzureSearch:ServiceUri`, `AzureSearch:ApiKey`, `AzureStorage:ExtractionQueueName`) plus any needed by resolved services. In `ConfigureTestServices`: register the `"Test"` auth scheme and set it as `DefaultAuthenticateScheme`/`DefaultChallengeScheme`; remove the `SearchIndexInitializer` `IHostedService` descriptor; replace `BlobServiceClient`, `SearchClient`, `QueueClient`, `SearchIndexClient`, and `DelegationTokenProvider` registrations with NSubstitute substitutes (exposed to tests so they can assert interactions). Provide a hook for tests to supply per-test substitutes/identities.

#### 8. Smoke test

**File**: `src/backend/ReceiptWell.Tests/HarnessSmokeTests.cs`

**Intent**: Prove the factory boots offline before any behavioral test depends on it.

**Contract**: Create a client from the factory and request a nonexistent route (or assert `factory.Services` resolves) — expect a normal HTTP response (e.g. 404), not a startup exception. Touches no infra and no health checks.

### Success Criteria:

#### Automated Verification:

- Lock file refreshed: `dotnet restore src/backend/ReceiptWell.sln` succeeds.
- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`.
- Test project is discovered and the smoke test passes: `dotnet test src/backend/ReceiptWell.sln`.

#### Manual Verification:

- The factory boots without any network call to Azure (no hang, no timeout) when offline.
- `Directory.Packages.props` is the only place test package versions are declared; the test `.csproj` carries no `Version` attributes.

**Implementation Note**: After this phase and all automated verification passes, pause for manual confirmation before proceeding.

---

## Phase 2: Risk #2 — Auth Gate + Missing-`oid` Fix

### Overview

Add `.RequireClaim("oid")` to the global policy (closing the 500 path), reword the test-plan oracle to match, and prove the gate: no token → 401, authenticated-no-`oid` → 403, on every protected receipt route.

### Changes Required:

#### 1. Require the `oid` claim globally

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Reject an authenticated-but-`oid`-less principal at the policy layer (403) before any handler runs, eliminating the 500.

**Contract**: Add `.RequireClaim("oid")` to the `FallbackPolicy` builder (`Program.cs:51-56`), after `.RequireAuthenticatedUser()`.

#### 2. Update the oracle wording

**File**: `context/foundation/test-plan.md`

**Intent**: The chosen mechanism yields 403 for the no-`oid` case; the oracle must say so honestly so the test isn't reworded to mirror the code later.

**Contract**: In §Risk Response, Risk #2 row, change the "What would prove protection" cell to: "Protected endpoints reject a missing/invalid token with **401** and an authenticated token lacking `oid` with **403** — never 200, never 500."

#### 3. Auth gate tests

**File**: `src/backend/ReceiptWell.Tests/AuthGateTests.cs`

**Intent**: Prove the gate rejects unauthenticated and claimless requests on every receipt route — not just the happy authenticated case.

**Contract**: A `[Theory]` over the three protected routes (`POST /receipts/staging-slot`, `POST /receipts/confirm`, `GET /receipts`): no auth header → **401**; authenticated principal without `oid` → **403**. One positive control: an authenticated principal *with* `oid` is **not** rejected (not 401/403) on a route that short-circuits cheaply. These paths touch no Azure client.

### Success Criteria:

#### Automated Verification:

- Build zero warnings: `dotnet build src/backend/ReceiptWell.sln`.
- Gate tests pass: `dotnet test src/backend/ReceiptWell.sln`.
- (Selective, post-phase) mutation check on the policy/extension is available: `npx stryker run` is **not** wired here — noted for the optional gate, not run in CI.

#### Manual Verification:

- Removing `.RequireClaim("oid")` makes a no-`oid` test fail (the test actually guards the fix, not a tautology).
- `test-plan.md` §Risk Response #2 wording matches the asserted 401/403 split.

**Implementation Note**: Pause for manual confirmation before proceeding.

---

## Phase 3: Risk #1 — Ownership Scoping

### Overview

Prove ownership beyond authentication: the claim→scope mapping, the confirm prefix guard (with no side effects), and the list query scoping.

### Changes Required:

#### 1. Claim→scope mapping unit

**File**: `src/backend/ReceiptWell.Tests/ClaimsPrincipalExtensionsTests.cs`

**Intent**: Pin the single identity contract independent of any endpoint; a regression here (e.g. `MapInboundClaims` flips back to `true`) breaks every scoped call.

**Contract**: `GetUserId()` on a `ClaimsPrincipal` carrying `oid` returns that value; on a principal without `oid` it throws `InvalidOperationException`. Pure unit, no factory.

#### 2. Confirm ownership guard (two identities)

**File**: `src/backend/ReceiptWell.Tests/ReceiptConfirmOwnershipTests.cs`

**Intent**: Prove user A cannot confirm a blob owned by user B, and that the rejected path performs no cross-user side effect.

**Contract**: Through `POST /receipts/confirm` as user A with `stagingBlobName = "userB/<guid>"` → **403**; assert the substituted `BlobServiceClient`, `SearchClient`, `QueueClient` received **no** call (`DidNotReceive()` on container access, `MergeOrUploadDocumentsAsync`, `SendMessageAsync`). Include a parametrized edge: a sibling-prefix collision (caller `"abc"`, blob `"abcd/<guid>"`) is still **403** — the `Ordinal` check includes the trailing `/`. Two distinct `oid` values are mandatory (a single-user fixture hides the leak).

#### 3. List query scoping (filter-capture)

**File**: `src/backend/ReceiptWell.Tests/ReceiptQueryScopingTests.cs`

**Intent**: Prove `GET /receipts` builds a Search query scoped to the caller's identity — the scoping decision, not the engine.

**Contract**: Unit on `ReceiptQueryService.GetReceiptsAsync(userId, …)` with a substituted `SearchClient` capturing the `SearchOptions.Filter`; the stub returns an empty `SearchResults<ReceiptDocument>` via `SearchModelFactory` (see Critical Implementation Details for the version caveat). Assert the captured filter scopes to the caller's `oid` — **requirement-derived** (filter references the `UserId` field and contains the caller's id), **not** an exact-string equality that would mirror the implementation.

### Success Criteria:

#### Automated Verification:

- Build zero warnings: `dotnet build src/backend/ReceiptWell.sln`.
- All ownership tests pass: `dotnet test src/backend/ReceiptWell.sln`.

#### Manual Verification:

- The filter assertion reads as a requirement (scoped-by-caller), not a copy of `$"UserId eq '{userId}'"` — confirm it is not a mirror test.
- The confirm test uses two different `oid` values and asserts the no-side-effect negative, not only the 403 status.

**Implementation Note**: Pause for manual confirmation before proceeding.

---

## Phase 4: Cookbook, Docs, and Sync

### Overview

Capture the reusable patterns and conventions so the next phase (and the next contributor) can add tests without rediscovering the harness, and advance the rollout state.

### Changes Required:

#### 1. Cookbook patterns

**File**: `context/foundation/test-plan.md`

**Intent**: Fill the §6 cookbook entries this phase earned.

**Contract**: §6.1 (backend unit) — the `GetUserId()` claim→scope pattern and the filter-capture unit pattern (requirement-derived assertion). §6.2 (backend integration) — the `WebApplicationFactory` + `TestAuthHandler` two-identity pattern and the no-token/no-`oid` gate pattern. §6.6 — a 2–3 line per-phase note on anything surprising (eager config reads, hosted-service removal, `SearchModelFactory` version sensitivity).

#### 2. Rollout status

**File**: `context/foundation/test-plan.md`

**Intent**: Reflect that Phase 1 shipped.

**Contract**: §3 Phase 1 row Status → `complete`.

#### 3. Change identity

**File**: `context/changes/testing-access-control/change.md`

**Intent**: Close out the change record.

**Contract**: `status: complete` (or per `/10x-implement` convention), `updated:` today's date.

#### 4. Testing conventions doc

**File**: `src/backend/.claude/CLAUDE.md` (and a short `src/backend/ReceiptWell.Tests/README.md` if useful)

**Intent**: Document the backend test stack and conventions per the user's request, so agents and contributors know how to run and extend the tests.

**Contract**: Add a "Testing" section: framework (xUnit), `Microsoft.AspNetCore.Mvc.Testing` / `WebApplicationFactory<Program>` for API integration, NSubstitute for client substitutes, the `TestAuthHandler` identity-injection convention, the hosted-service-removal + dummy-config boot requirement, and how to run (`dotnet test src/backend/ReceiptWell.sln`). Keep consistent with the existing global-usings note.

### Success Criteria:

#### Automated Verification:

- Full suite green: `dotnet test src/backend/ReceiptWell.sln`.
- Build zero warnings: `dotnet build src/backend/ReceiptWell.sln`.

#### Manual Verification:

- `test-plan.md` §6.1/§6.2 entries are concrete enough to write the next test from (no "TBD").
- The backend CLAUDE.md "Testing" section lets a fresh agent run and extend the suite without reading this plan.

**Implementation Note**: Final phase — confirm the full suite is green and docs are accurate.

---

## Testing Strategy

### Unit Tests:

- `GetUserId()` returns `oid` / throws when absent (claim→scope mapping; guards the `MapInboundClaims=false` dependency).
- `ReceiptQueryService` builds a caller-scoped Search filter (filter-capture, requirement-derived).

### Integration Tests (WebApplicationFactory, offline):

- No token → 401 and authenticated-no-`oid` → 403 on all three receipt routes.
- Cross-user confirm → 403 with no blob copy / index write / enqueue; sibling-prefix edge still 403.
- Positive control: authenticated-with-`oid` is not gate-rejected.

### Manual Testing Steps:

1. Temporarily remove `.RequireClaim("oid")` → confirm the no-`oid` test goes red (anti-tautology check).
2. Temporarily delete the `FallbackPolicy` → confirm the no-token tests go red.
3. Temporarily weaken the confirm prefix guard → confirm the two-identity test goes red and the side-effect assertions fire.

## Performance Considerations

None. All Phase 1 tests are hermetic and infra-free; the suite is fast by construction (middleware short-circuits and direct unit calls, no network).

## Migration Notes

`.RequireClaim("oid")` is a behavior change: any client presenting a valid token without an `oid` claim now receives 403 instead of a 500. This is the intended hardening; no existing legitimate caller is affected (every CIAM consumer token carries `oid`).

## References

- Research: `context/changes/testing-access-control/research.md`
- Test plan (oracle): `context/foundation/test-plan.md` §2 Risk Map (#1, #2), §Risk Response, §3 Phase 1
- Lessons: `context/foundation/lessons.md` (`MapInboundClaims=false`; CIAM authority)
- Prime IDOR guard: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:38-42`
- Auth gate: `src/backend/ReceiptWell.Web/Program.cs:51-56`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend Test Harness Bootstrap

#### Automated

- [x] 1.1 Lock file refreshed: `dotnet restore src/backend/ReceiptWell.sln` — ffd8ed6
- [x] 1.2 Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln` — ffd8ed6
- [x] 1.3 Test project discovered and smoke test passes: `dotnet test src/backend/ReceiptWell.sln` — ffd8ed6

#### Manual

- [x] 1.4 Factory boots offline with no Azure network call — ffd8ed6
- [x] 1.5 Test package versions live only in `Directory.Packages.props`; `.csproj` carries no `Version` — ffd8ed6

### Phase 2: Risk #2 — Auth Gate + Missing-`oid` Fix

#### Automated

- [x] 2.1 Build zero warnings: `dotnet build src/backend/ReceiptWell.sln` — b3d2ac1
- [x] 2.2 Gate tests pass: `dotnet test src/backend/ReceiptWell.sln` — b3d2ac1

#### Manual

- [x] 2.3 Removing `.RequireClaim("oid")` makes a no-`oid` test fail (guards the fix) — b3d2ac1
- [x] 2.4 `test-plan.md` §Risk Response #2 wording matches the 401/403 split — b3d2ac1

### Phase 3: Risk #1 — Ownership Scoping

#### Automated

- [x] 3.1 Build zero warnings: `dotnet build src/backend/ReceiptWell.sln` — 7a86a02
- [x] 3.2 All ownership tests pass: `dotnet test src/backend/ReceiptWell.sln` — 7a86a02

#### Manual

- [x] 3.3 Filter assertion is requirement-derived, not a mirror of `$"UserId eq '{userId}'"` — 7a86a02
- [x] 3.4 Confirm test uses two `oid` values and asserts the no-side-effect negative — 7a86a02

### Phase 4: Cookbook, Docs, and Sync

#### Automated

- [x] 4.1 Full suite green: `dotnet test src/backend/ReceiptWell.sln` — 6475e4f
- [x] 4.2 Build zero warnings: `dotnet build src/backend/ReceiptWell.sln` — 6475e4f

#### Manual

- [x] 4.3 §6.1/§6.2 cookbook entries are concrete (no "TBD") — 6475e4f
- [x] 4.4 Backend CLAUDE.md "Testing" section lets a fresh agent run/extend the suite — 6475e4f
