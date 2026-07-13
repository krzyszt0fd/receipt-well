---
date: 2026-06-24T19:27:49+02:00
researcher: Krzysztof Dudzik
git_commit: 6a1d3730b35a306556209b8eb3f7570132beab0f
branch: develop
repository: receipt-well
topic: "Access control oracle for Test Plan Phase 1 — IDOR ownership scoping (Risk #1) and the auth gate (Risk #2)"
tags: [research, codebase, access-control, authentication, authorization, idor, oid, webapplicationfactory]
status: complete
last_updated: 2026-06-24
last_updated_by: Krzysztof Dudzik
---

# Research: Access control oracle for Test Plan Phase 1 (Risks #1 & #2)

**Date**: 2026-06-24T19:27:49+02:00
**Researcher**: Krzysztof Dudzik
**Git Commit**: 6a1d3730b35a306556209b8eb3f7570132beab0f
**Branch**: develop
**Repository**: receipt-well

## Research Question

Phase 1 of the test rollout ("Backend test harness + access control", `test-plan.md` §3) must prove two things and bootstrap the project that proves them:

- **Risk #1 (IDOR):** a request authenticated as user A never returns or serves user B's receipts or blob — ownership is checked beyond "is authenticated".
- **Risk #2 (auth gate):** an unauthenticated, mis-mapped-claim, or new-but-unguarded endpoint cannot reach a protected receipt operation — a missing/invalid token (and an empty/missing `oid`) is rejected with **401, not 200, not 500**.

The oracle — *what the code should do* — must come from the PRD, the test plan's Risk Response rows, and `lessons.md`, **not** from mirroring the implementation. This document grounds where identity is extracted, where every data path is scoped by it, where the gate is applied, and how a new endpoint opts in — so the Phase 1 plan can pick the cheapest test per risk.

## Summary

**The identity key is the `oid` claim.** It is extracted in exactly one place (`ClaimsPrincipalExtensions.GetUserId()`) and threaded as `userId` into every service. Three endpoints exist and every one is meant to be owner-scoped:

| Endpoint | Handler | Ownership mechanism | Risk |
|----------|---------|---------------------|------|
| `POST /receipts/staging-slot` | `ReceiptBlobService.CreateStagingSlotAsync` | Staging blob name is `{userId}/{guid}`; SAS is **write-only**, single-blob | #1 |
| `POST /receipts/confirm` | `ReceiptConfirmService.ConfirmUploadAsync` | **Explicit guard:** rejects if `stagingBlobName` doesn't start with `{userId}/` → `Forbidden` (403) | #1 (prime target) |
| `GET /receipts` | `ReceiptQueryService.GetReceiptsAsync` | Azure Search filter `UserId eq '{userId}'` | #1 |

**The auth gate is global and secure-by-default** via an Authorization `FallbackPolicy` requiring an authenticated user (`Program.cs:51-56`). A new endpoint is protected *unless it explicitly opts out* with `.AllowAnonymous()`. Only `/health` and (dev-only) the OpenAPI doc opt out. This is the strong answer to the test plan's challenge "the gate is global so every endpoint is automatically covered" — here it genuinely is.

**Two real divergences from the oracle:**

1. **Missing-`oid` → 500, not 401 (oracle says 401). DECIDED: Phase 1 ships the fix** alongside the tests (confirmed 2026-06-24). Today all three handlers call `GetUserId()` *outside* their `try` block, and `GetUserId()` throws `InvalidOperationException` when `oid` is absent (`ClaimsPrincipalExtensions.cs:8`), so a token that authenticates but carries no `oid` yields an unhandled 500 — while the Risk #2 oracle (`test-plan.md` §Risk Response #2) requires 401. The Phase 1 test asserts the *target* contract and the fix makes it pass. See [Open Questions](#open-questions) for the mechanism sub-decision (401 vs 403 semantics).
2. **No "detail" / blob-serving endpoint exists yet.** Risk #1 names "list, detail, or blob"; only *list* + *confirm* + *staging* exist. The detail/blob face has **no API surface to test** today. `ReceiptSummary` deliberately omits `BlobUrl`, so the list does not leak blob URLs either.

**No backend test project exists.** Phase 1 bootstraps one. The non-obvious bootstrap facts: `Program` is implicitly internal under top-level statements (needs `public partial class Program` for `WebApplicationFactory<Program>`), central package management and lock files are ON (`dotnet restore` after adding versions), and `Directory.Build.props` applies `net9.0` + nullable + `RootNamespace=ReceiptWell` to the test project too.

## Detailed Findings

### Identity extraction — the single scoping key (`oid`)

`src/backend/ReceiptWell.Web/Extensions/ClaimsPrincipalExtensions.cs:7-8`:

```csharp
public static string GetUserId(this ClaimsPrincipal user) =>
    user.FindFirstValue("oid") ?? throw new InvalidOperationException("oid claim missing");
```

- **Oracle (claim→scope mapping):** the per-user scope value is the JWT `oid` claim verbatim. `oid` resolves by its short name only because `Program.cs:47` sets `MapInboundClaims = false` — this is the recorded lesson "JwtBearer: set MapInboundClaims = false" (`lessons.md`). If that flag regresses, `FindFirstValue("oid")` returns null and *every* call throws.
- Cheapest test (unit): a `ClaimsPrincipal` carrying `oid` returns it; one without `oid` throws `InvalidOperationException`. This pins the mapping independently of any endpoint.
- All three endpoints call `httpContext.User.GetUserId()` and pass the result as `userId`: `Program.cs:145, 165, 203`.

### Auth gate — global FallbackPolicy (Risk #2)

`src/backend/ReceiptWell.Web/Program.cs:44-56`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = builder.Configuration["AzureExternalId:Authority"];
        options.Audience = builder.Configuration["AzureExternalId:ClientId"];
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
```

Middleware order: `UseAuthentication()` then `UseAuthorization()` (`Program.cs:117-118`).

- **How a new endpoint opts in:** it doesn't need to. The `FallbackPolicy` applies to any endpoint with no other authorization metadata, so a newly mapped endpoint is **protected by default**. Opting *out* is the explicit act: `.AllowAnonymous()`.
- **Opt-outs in the codebase (the complete allow-list):**
  - `Program.cs:112` — `app.MapOpenApi().AllowAnonymous()` (only inside `if (app.Environment.IsDevelopment())`).
  - `Program.cs:137` — `MapHealthChecks("/health", …).AllowAnonymous()`.
  - The three receipt endpoints carry **no** `.AllowAnonymous()` → covered by the fallback policy.
- **Oracle (Risk #2, from `test-plan.md` §Risk Response #2 + PRD FR-001):**
  - No `Authorization` header → **401**.
  - Invalid / expired / wrong-issuer / wrong-audience token → **401** (JwtBearer rejects; note the `lessons.md` CIAM-issuer trap — wrong `Authority` rejects *all* consumer tokens).
  - Valid token, authenticated → the protected endpoint runs.
- **Regression this catches:** someone deletes the `FallbackPolicy` (endpoints silently become anonymous) or adds a stray `.AllowAnonymous()`. A test that hits each protected route with **no token** and asserts 401 is the cheapest guard — it short-circuits in middleware before any Azure service is touched, so it needs **no real infra**.
- **Anti-pattern to avoid (named in plan):** asserting only the happy authenticated case. A single-token test would pass even if the gate were removed.

### Risk #1, face A — confirm ownership guard (prime target)

`src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:38-42`:

```csharp
if (!stagingBlobName.StartsWith($"{userId}/", StringComparison.Ordinal))
{
    LogOwnershipViolation(logger, userId, stagingBlobName);
    return new ReceiptConfirmResult.Forbidden();
}
```

- This is the **strongest IDOR defense and the highest-signal test**. Without it, user B could pass `userA/<guid>` as `stagingBlobName` and the service would copy user A's staged image into B's receipts container and index it under B. The endpoint maps `Forbidden → Results.Forbid()` (403) at `Program.cs:173`.
- The guard runs **before any blob/search/queue call**, so the Forbidden path touches no Azure client.
- **Oracle (from PRD guardrail "paragony w pełni izolowane" + Non-Goal "no sharing"):** a confirm whose `stagingBlobName` prefix ≠ caller's `oid/` returns **403 and performs no copy, no index write, no enqueue**.
- Note on the trailing `/`: the `Ordinal` prefix check includes the slash, so it correctly rejects sibling-prefix collisions (`oid` `"abc"` does not match a blob owned by `"abcd"`). A test with two ids sharing a prefix is a worthwhile edge.
- **Cheapest layer:** because the guard short-circuits before infra, this is testable as a **unit test** of `ConfirmUploadAsync` with never-touched mocked clients, *or* as an integration test through `POST /receipts/confirm` proving endpoint→service→403 wiring. The two-identity integration test is the canonical Risk #1 proof; the unit test is a cheaper companion that pins the guard logic.
- **Anti-pattern:** a single-user fixture hides the leak entirely (plan §Risk Response #1). The test needs two distinct `oid` values.

### Risk #1, face B — list query scoping

`src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:12-19`:

```csharp
var options = new SearchOptions
{
    Filter = $"UserId eq '{userId}'",
    Size = 1000
};
options.OrderBy.Add("UploadedAt desc");
var response = await searchClient.SearchAsync<ReceiptDocument>("*", options, cancellationToken);
```

- **Oracle:** `GET /receipts` as user A returns only documents whose `UserId == A.oid`; user B's documents never appear. `UserId` is `IsFilterable` on the index (`ReceiptDocument.cs:10-11`), so the filter is valid.
- **Two faces of this risk:**
  - *Real face:* does the filter actually exclude other users? Proving this well needs a fake/seeded index returning two users' docs and asserting only the caller's return — but `SearchClient` is awkward to fake (sealed-style Azure SDK client). A pragmatic cheaper signal is a stub `SearchClient`/`SearchResults` that **captures the `SearchOptions.Filter`** and asserts it equals `UserId eq '<oid>'`. Beware: asserting the exact filter string risks a *mirror test* (re-stating the implementation). Derive the expected filter from the requirement ("scope by the authenticated user's id"), and prefer asserting *behavior* (B's doc absent from results) where the fake allows it.
  - *Safe face:* the filter is string-interpolated (`$"UserId eq '{userId}'"`). `userId` is the IdP-issued `oid` (a GUID), not free user input, so OData-filter injection is not reachable through the API today. Worth a one-line note, not a test — flagging it keeps the assumption visible if `oid` ever stops being a GUID.

### Risk #1, face C — staging slot naming

`src/backend/ReceiptWell.Web/Services/ReceiptBlobService.cs:16-22, 32`:

- Staging blob name is `$"{userId}/{Guid.NewGuid()}"`; the returned SAS is **write-only** (`BlobSasPermissions.Write`) and scoped to that single blob with a 10-minute expiry. A user cannot mint a readable SAS for someone else's blob here. Ownership is then *re-validated* at confirm time by the prefix guard above — defense in depth.
- Lower test priority than the confirm guard: the staging slot grants only write to a fresh random blob; the security-critical decision is the confirm-time prefix check.

### What does NOT exist (scope boundaries for the oracle)

- **No `GET /receipts/{id}` detail endpoint and no blob-serving endpoint.** Endpoint inventory is exactly: `staging-slot`, `confirm`, `GET /receipts`, plus `/health` (`Program.cs:120,139,158,196`). Risk #1's "detail / blob" face has no surface to test at this commit — do not write a test for an endpoint that doesn't exist; note it as a future trigger.
- `ReceiptSummary` (`Models/ReceiptSummary.cs`) intentionally omits `BlobUrl`, so the list response carries no blob pointer to leak. FR-006 thumbnails are not yet served via the API.

### Test-harness bootstrap facts (non-obvious)

No test project exists (`ReceiptWell.sln` contains `ReceiptWell.Core`, `ReceiptWell.Web`, `ReceiptWell.Functions` only). To stand up `WebApplicationFactory`-based integration tests:

- **`WebApplicationFactory<Program>` needs a referencable `Program` type.** With top-level statements, `Program` is `internal`. Add `public partial class Program { }` to the bottom of `Program.cs` (or `[assembly: InternalsVisibleTo]`). This is a hard prerequisite for the integration layer.
- **Central Package Management is ON** (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`). Add test package *versions* there (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `Microsoft.AspNetCore.Mvc.Testing`, a mocking lib such as `NSubstitute`); reference them in the test `.csproj` **without** a `Version` attribute. Backend CLAUDE.md.
- **Lock files with content hashes** (`RestorePackagesWithLockFile=true` in `Directory.Build.props`). Run `dotnet restore` after editing versions, before committing.
- `Directory.Build.props` applies `net9.0`, `Nullable=enable`, `ImplicitUsings=enable`, `RootNamespace=ReceiptWell` to the test project automatically — no `using System;` etc., annotate nullables, expect zero warnings.
- Backend CLAUDE.md: **use a global-usings file in the test project** containing only the test-framework, assertion, and project-wide helper namespaces.
- **Injecting a test identity:** the cheapest way to exercise auth/ownership without a real CIAM token is a test `AuthenticationHandler` registered as the default scheme in the factory's `ConfigureTestServices`, producing a `ClaimsPrincipal` with a chosen `oid`. Two-identity tests = two principals with different `oid`s. The no-token 401 test simply omits the auth header (and must not register the always-authenticate handler for that case).
- **Infra dependency for ownership tests:** the confirm-Forbidden path and the no-token-401 path touch **no** Azure client, so they need no Azurite/Search. Tests that go *past* the guard (a successful confirm, or the list returning seeded docs) would need stubbed `BlobServiceClient`/`SearchClient`/`QueueClient` — register fakes in `ConfigureTestServices`. Per `test-plan.md` §1 cost×signal, prefer asserting the guard/gate boundaries that need no infra first.

## Code References

- `src/backend/ReceiptWell.Web/Extensions/ClaimsPrincipalExtensions.cs:7-8` — `oid` extraction; throws when missing (the claim→scope mapping)
- `src/backend/ReceiptWell.Web/Program.cs:44-56` — JwtBearer + `FallbackPolicy` global auth gate
- `src/backend/ReceiptWell.Web/Program.cs:112,137` — the only `.AllowAnonymous()` opt-outs (OpenAPI dev-only, `/health`)
- `src/backend/ReceiptWell.Web/Program.cs:145,165,203` — `GetUserId()` called *outside* the try block in all three handlers (missing-`oid` → 500)
- `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:38-42` — prime IDOR guard; `Forbidden` when prefix mismatch
- `src/backend/ReceiptWell.Web/Program.cs:173` — `Forbidden → Results.Forbid()` (403)
- `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:12-19` — list scoped by `UserId eq '{userId}'`
- `src/backend/ReceiptWell.Web/Services/ReceiptBlobService.cs:16-32` — `{userId}/{guid}` staging name, write-only SAS
- `src/backend/ReceiptWell.Core/ReceiptDocument.cs:10-11` — `UserId` filterable index field
- `src/backend/ReceiptWell.Web/Models/ReceiptSummary.cs` — list DTO; no `BlobUrl` exposed
- `src/backend/Directory.Build.props`, `src/backend/Directory.Packages.props` — net9.0/nullable/CPM/lock-file constraints the test project inherits

## Architecture Insights

- **Secure-by-default authorization.** The `FallbackPolicy` inverts the usual `[Authorize]` opt-in into an opt-out model: forgetting to annotate a new endpoint leaves it *protected*, not open. The test that has lasting value asserts the *opt-out list stays small* (no-token → 401 on each receipt route).
- **Ownership is enforced at two independent layers** for the upload path: the SAS is write-only/single-blob at staging, and the prefix guard re-checks at confirm. The confirm guard is the one that actually prevents cross-user theft and is therefore the highest-signal unit/integration target.
- **Ownership is data-plane, not just API-plane.** List scoping lives in the Search filter, not in C# post-filtering — so the test must reach the query construction (filter capture) or a seeded index, not just the endpoint return shape.
- **`oid` is load-bearing and singular.** One extension method is the entire claim→scope contract. It is a perfect, cheap unit-test target and a good candidate for the `contract-surfaces.md` registry.

## Historical Context (from prior changes)

- `context/foundation/lessons.md` — "JwtBearer: set `MapInboundClaims = false`" is the reason `FindFirstValue("oid")` works; a regression here breaks every scoped call. "Use GUID-based ciamlogin.com authority" explains why a wrong `Authority` rejects only consumer tokens (silently passing internal accounts) — relevant to the Risk #2 invalid-token oracle.
- `context/foundation/test-plan.md` §2 Risk Map (#1, #2) and §Risk Response rows are the binding oracle source for this phase; §3 fixes Phase 1 scope to Risks #1 & #2, types unit + integration.

## Related Research

- None yet — this is the first research artifact under `context/changes/`. Subsequent phases (`test-plan.md` §3 Phases 2–5) will add their own.

## Open Questions

1. **Missing-`oid` contract — RESOLVED: Phase 1 ships a fix (confirmed 2026-06-24).** A token that authenticates but lacks `oid` currently yields an unhandled `InvalidOperationException` → HTTP 500, because `GetUserId()` is called outside each handler's `try` (`Program.cs:145,165,203`). The Risk #2 oracle (`test-plan.md` §Risk Response #2) says this must be rejected cleanly (**401, not 200, not 500**). Phase 1 will change the behavior, not just observe it; the test asserts the target contract and must go green with the fix in the same change.

   **Open mechanism sub-decision for `/10x-plan` (changes the asserted status code):**
   - **(a) Reject at the auth/token layer → 401.** Treat a token without `oid` as an insufficient/invalid token (RFC 6750 `invalid_token`), e.g. add a JwtBearer token-validation check (or `OnTokenValidated` event) that fails authentication when `oid` is absent. Matches the oracle's literal **401** and keeps `GetUserId()` a safe non-throwing read downstream. **Recommended** — it aligns the asserted code with the oracle verbatim.
   - **(b) Require the claim in the authorization policy → 403.** Add `.RequireClaim("oid")` to the `FallbackPolicy`. Simpler, but an *authenticated* principal failing a claim requirement produces **403 Forbidden**, not 401 — so the test oracle would have to be 403, a deviation from the test plan's "401" wording. If chosen, update `test-plan.md` §Risk Response #2 to say "401 (no/invalid token) / 403 (authenticated but no `oid`)".

   Either way the regression caught is identical: a structurally-authenticated-but-unusable token must not reach a handler and must not 500. Pick (a) unless there's a reason to prefer policy-layer enforcement; the plan should state the chosen status code so the test asserts it directly (not mirror the implementation).
2. **List-scoping test layer.** `SearchClient` is hard to fake faithfully. Choose between (a) a stub that captures and asserts the `SearchOptions.Filter` (cheap, but guard against writing a mirror test) and (b) a seeded fake/real index proving B's docs are absent (stronger signal, higher setup cost). Resolve in `/10x-plan` per cost×signal.
3. **Detail / blob-serving face of Risk #1 is currently untestable** (no such endpoint). Flag as a trigger: when a `GET /receipts/{id}` or a blob/thumbnail endpoint ships (FR-006), it must carry an ownership check and gets its own Risk #1 test.
