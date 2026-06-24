# Test Plan

> Phased test rollout for this project. Strategy is frozen at the top
> (§1–§5); cookbook patterns at the bottom (§6) fill in as phases ship.
> Read before writing any new test.
>
> Refresh: re-run `/10x-test-plan --refresh` when stale (see §8).
>
> Last updated: 2026-06-23 (Phase 1 change opened)

## 1. Strategy

Tests follow three non-negotiable principles for this project:

1. **Cost × signal.** The cheapest test that gives a real signal for the
   risk wins. Do not promote to e2e because e2e "feels safer." Do not put a
   vision model on top of a deterministic visual diff that already catches
   the regression.
2. **User concerns are first-class evidence.** Risks anchored in "the team
   is worried about X, and the failure would surface somewhere in `<area>`"
   carry the same weight as PRD lines or hot-spot data.
3. **Risks are scenarios, not code locations.** This plan documents *what
   could fail* and *why we believe it's likely* — drawn from documents,
   interview, and codebase *signal* (churn, structure, test base). It does
   NOT claim to know which line owns the failure. That knowledge is
   produced by `/10x-research` during each rollout phase. If the plan and
   research disagree about where the failure lives, research is the
   ground truth.

Hot-spot scope used for likelihood weighting: `src/backend/`, `src/frontend/`.

## 2. Risk Map

The top failure scenarios this project must protect against, ordered by
risk = impact × likelihood. Risks are failure scenarios in user / business
terms, not test names. The Source column cites the *evidence that surfaced
this risk* — never a specific file as "where the failure lives" (that is
research's job, see §1 principle #3).

| # | Risk (failure scenario) | Impact | Likelihood | Source (evidence — not anchor) |
|---|-------------------------|--------|------------|--------------------------------|
| 1 | Logged-in user reaches **another user's** receipts (list, detail, or blob) — ownership is not checked beyond "is authenticated" (IDOR) | High | High | PRD §Access Control + guardrail "paragony w pełni izolowane"; interview Q1, Q3 (`oid`-claim mapping bug = the scoping key); hot-spot dir `src/backend/ReceiptWell.Web/Services/` (9 commits/30d) |
| 2 | Unauthenticated or mis-mapped-claim request reaches a protected receipt endpoint (regression on a new endpoint, or Entra misconfig) | High | Medium | PRD FR-001; roadmap F-01 (complete-auth-gate); interview Q3 (Entra External ID config churn); hot-spot file `src/backend/Program.cs` (11 commits/30d) |
| 3 | Server accepts an oversized or wrong-type file because validation is client-side only (untrusted input / resource abuse) | Medium | Medium | PRD NFR (≤10 MB; JPEG/PNG/HEIC); FR-002; hot-spot dir `src/frontend/src/app/receipts/upload/` (11 commits/30d) — abuse lens |
| 4 | Original photo is **lost** when AI extraction fails — violates guardrail "zdjęcie nigdy nie ginie" | High | Medium | PRD §Guardrails + FR-003; roadmap S-01 (receipt-upload-confirm) |
| 5 | Receipt stuck in **'pending' forever**, or the poison path silently drops it — processing status never resolves | High | Medium | PRD NFR (async processing + visible status); US-01 AC; roadmap S-03 (ai-extraction-and-enrichment) |
| 6 | Generated tags are **not normalized to Polish** → tag-search (north star S-04) returns nothing for "rower" | High | Medium | PRD §Business Logic + FR-004/FR-005 (stand-or-fall together); roadmap S-03 |
| 7 | An infra-boundary failure (Blob/Queue/Search/Function) surfaces as an **opaque 500 with no diagnostic, or a dishonest 200 that masks a failed side effect** | High | High | interview Q2 (lived incident: opaque 500, no logs in Azure); multi-component coupling; hot-spot dirs `src/backend/ReceiptWell.Web/Services/` + file `src/backend/Program.cs` |

**Impact × Likelihood rubric.** High = user loses access/data/money or failure
is publicly visible / area changes weekly or already burned. Medium = feature
degrades or workaround exists / touched occasionally, has been a bug source.
Low = cosmetic / stable code. Protect **High × High first**: Risk #1 (IDOR)
and Risk #7 (infra failure shape).

**Abuse / security lens.** The product has auth and accepts file uploads, so
the map carries abuse rows: #1 (authorization / ownership beyond
authentication), #2 (authentication presence), #3 (untrusted input + resource
abuse on upload).

**Out of the risk map by design.** The roadmap Open Question — *is AI extraction
good enough on faded receipts?* — is product-validation owned manually by the
developer on real receipts, not a CI test. See §7.

### Risk Response Guidance

| Risk | What would prove protection | Must challenge | Context `/10x-research` must ground | Likely cheapest layer | Anti-pattern to avoid |
|------|-----------------------------|----------------|--------------------------------------|-----------------------|-----------------------|
| #1 | A request authenticated as user A never returns or serves user B's receipt or blob | "Logged in ⇒ allowed to see it" — authentication is not ownership | How the user identity (`oid`) is extracted from the token and how every receipt query/blob fetch is scoped by it | Integration with two distinct identities + unit on claim→scope mapping | Happy-path-only (a single-user fixture hides the leak entirely) |
| #2 | Protected endpoints reject a missing/invalid token with **401** and an authenticated token lacking `oid` with **403** — never 200, never 500 | "The gate is global so every endpoint is automatically covered" | Where `[Authorize]` / the auth middleware is applied and how a new endpoint opts in | Integration (request with no token / bad token / valid token) | Asserting only the happy authenticated case |
| #3 | Server rejects oversized and disallowed-type uploads regardless of what the client sent | "The front-end validation is enough" | Where upload size/type is enforced server-side and what the persisted contract is | Unit/integration on the server validation boundary | Trusting the client check; mirroring client limits instead of asserting server behavior |
| #4 | Blob persists and survives an extraction failure; the receipt stays retrievable with its image | "Save-then-process is obviously already safe" | The order of blob write vs. queue/processing, and what a failed extraction does to the stored blob | Integration (force extraction failure, assert blob still intact and receipt retrievable) | Over-mocking the blob store so nothing is actually asserted |
| #5 | A failed extraction moves the receipt to a terminal/visible status (failed/partial), never stuck pending; the poison handler records it | "Final status 200 ⇒ it worked"; "no exception ⇒ done" | The status state machine and what the poison/dead-letter path actually does to the document | Unit on status transitions + integration on the trigger/poison path | Asserting only the happy enrichment path |
| #6 | Multilingual inputs collapse to the expected PL tags ("bike"/"bicycle" → "rower") | "Expected value equals whatever the normalizer currently outputs" (oracle problem) | The normalization rule's source of truth (requirements), independent of the implementation | Unit on the normalizer with a **requirements-derived** oracle | Expected values lifted from the implementation under test (tautological green) |
| #7 | A thrown dependency error yields a clean 5xx and **no** silent success / no partial persist | "Retry/health 200 ⇒ fine"; "a swallowed exception ⇒ success" | Where each external client is called and how its errors are (or are not) translated at the API boundary | Integration with a failing fake dependency | Meaningless snapshot; e2e where integration suffices; asserting status code only while ignoring the side effect |

## 3. Phased Rollout

Each row is a discrete rollout phase that will open its own change folder
via `/10x-new`. Status moves left-to-right through the values below; the
orchestrator updates Status as artifacts appear on disk.

| # | Phase name | Goal (one line) | Risks covered | Test types | Status | Change folder |
|---|------------|-----------------|---------------|------------|--------|---------------|
| 1 | Backend test harness + access control | Bootstrap the backend test project (none today) and prove ownership scoping and the auth gate hold | #1, #2 | unit + integration | change opened | context/changes/testing-access-control/ |
| 2 | Infra-boundary failure shape | A failing Blob/Queue/Search/Function dependency surfaces a clean, honest 5xx — never a silent success | #7 | integration | not started | — |
| 3 | Upload integrity + input validation | Photo survives an extraction failure; the server enforces size/type itself | #4, #3 | integration + unit | not started | — |
| 4 | Async extraction + business rules | Failed extraction reaches a visible terminal status (not stuck) and the poison path; tags normalize to PL | #5, #6 | unit + integration | not started | — |
| 5 | Frontend integration + quality-gates wiring | Cover status rendering, guarded routes, and upload-validation UX where they add signal; wire CI gates | #1–#6 surface checks | Angular unit/integration + gates | not started | — |

**Status vocabulary** (fixed — parser literals): `not started` → `change opened`
→ `researched` → `planned` → `implementing` → `complete`.

No AI-native phase: the only candidate (an LLM-judge eval of extraction
quality) loses on cost × signal — non-deterministic and expensive, and that
validation is already owned manually per the roadmap Open Question (see §7).

## 4. Stack

The classic test base for this project. AI-native tools (if any) carry a
`checked:` date so future readers can see which lines need re-verification.

| Layer | Tool | Version | Notes |
|-------|------|---------|-------|
| backend unit + integration | none yet — see §3 Phase 1 | — | No test project exists. Phase 1 bootstraps **xUnit** + `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`) for API integration |
| backend Functions tests | none yet — see §3 Phase 4 | — | Isolated-worker .NET 9 Functions tested via direct handler invocation with faked bindings |
| frontend unit + integration | Vitest (via `@angular/build:unit-test`) | 4.0.8 | Already wired; real specs in `src/frontend/src/app/receipts/` (list, upload). Run with `ng test` |
| e2e | none yet — not scheduled | — | No critical flow currently justifies the e2e cost over integration; revisit if S-04 search ships a multi-step UI |
| (optional) AI-native | none | n/a | Extraction-quality eval intentionally excluded — see §3 note and §7 |

**Stack grounding tools (current session):**
- Docs: Context7 MCP — available; not queried this session (stack is .NET 9 / Angular 21, both well-known); checked: 2026-06-23
- Search: Exa.ai MCP — available; not used; checked: 2026-06-23
- Runtime/browser: none detected — no Playwright/browser MCP in session; not used; checked: 2026-06-23
- Provider/platform: none detected — no GitHub/Azure MCP in session; CI gates (§5) configured via existing GitHub Actions; checked: 2026-06-23

Backend is .NET 9 (ASP.NET Core Web API + isolated-worker Azure Functions);
frontend is Angular 21. Use Context7 for current xUnit / `WebApplicationFactory`
and Angular/Vitest test-setup APIs when Phase 1/5 plans are written.

## 5. Quality Gates

The full set of gates that must pass before a change reaches production.
"Required after §3 Phase N" means the gate is enforced once that rollout
phase lands; before that, the gate is `planned`.

| Gate | Where | Required? | Catches |
|------|-------|-----------|---------|
| lint + typecheck (frontend) + build (backend) | local + CI (GitHub Actions) | required | syntactic / type drift |
| backend unit + integration | local + CI | required after §3 Phase 1 | access-control & logic regressions |
| frontend unit (Vitest) | local + CI | required after §3 Phase 5 | component/UI logic regressions |
| infra-boundary failure tests | CI | required after §3 Phase 2 | opaque 500 / silent success at infra edges |
| post-edit hook (run affected tests) | local (agent loop) | recommended | regressions at edit time |
| e2e on critical flows | CI on PR | optional — not scheduled | broken critical user paths (revisit when search UI ships) |

Existing CI lives in GitHub Actions (backend → Azure App Service, frontend →
Azure Static Web Apps). Phases 1, 2, and 5 wire their gates into that pipeline.

## 6. Cookbook Patterns

How to add new tests in this project. Each sub-section is filled in once the
relevant rollout phase ships; before that, it reads "TBD — see §3 Phase N."

### 6.1 Adding a backend unit test

- TBD — see §3 Phase 1 (claim→scope mapping; status-transition logic; tag normalization to PL with a requirements-derived oracle).

### 6.2 Adding a backend integration test (API endpoint)

- TBD — see §3 Phase 1 (two-identity ownership test) and §3 Phase 2 (failing-dependency / honest-5xx test).

### 6.3 Adding an Azure Functions test

- TBD — see §3 Phase 4 (extraction-failure → terminal status + poison path).

### 6.4 Adding a frontend unit/integration test

- Reference test today: `src/frontend/src/app/receipts/list/list.component.spec.ts` (184 lines), `src/frontend/src/app/receipts/upload/upload.component.spec.ts`.
- Run locally: `ng test` (Vitest via `@angular/build:unit-test`). Fuller conventions land in §3 Phase 5.

### 6.5 Adding a server-side upload-validation test

- TBD — see §3 Phase 3 (size/type enforcement independent of the client).

### 6.6 Per-rollout-phase notes

(Optional. After each phase lands, `/10x-implement` appends a 2–3 line note
here capturing anything surprising the rollout phase taught.)

## 7. What We Deliberately Don't Test

Exclusions agreed during the rollout (Phase 2 interview, Q5). Future
contributors should respect these unless the underlying assumption changes.

- **Frontend visual / snapshot tests for landing & marketing pages** — brittle, catch nothing of value. Re-evaluate if a marketing page gains real logic. (Source: Phase 2 interview Q5.)
- **Azure SDK clients themselves** — the SDK is Microsoft's to test; we test our usage at the boundary, not the client internals. (Source: Phase 2 interview Q5.)
- **Infra / Terraform** — deploy-time concern, not application-test surface. (Source: Phase 2 interview Q5.)
- **Logging / observability configuration** — we assert the failure *shape* (Risk #7: clean 5xx, no silent success), not whether logs physically land in Azure. That is an ops/observability task, not a unit test. (Source: Phase 2 interview Q2, reframed.)
- **AI extraction *quality* eval** — whether extraction is "good enough" on faded receipts is product validation the developer runs manually on real receipts (roadmap Open Question), not a CI gate. (Source: roadmap + cost × signal.)

## 8. Freshness Ledger

- Strategy (§1–§5) last reviewed: 2026-06-23
- Stack versions last verified: 2026-06-23
- AI-native tool references last verified: 2026-06-23

Refresh (`/10x-test-plan --refresh`) when:

- a new top-3 risk surfaces from the roadmap or archive,
- a recommended tool's `checked:` date is older than three months,
- the project's tech stack changes (new framework, new test runner),
- §7 negative-space no longer matches what the team believes.
