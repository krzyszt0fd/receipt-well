# Complete Auth Gate — Plan Brief

> Full plan: `context/changes/complete-auth-gate/plan.md`

## What & Why

Wire the authorization gate for ReceiptWell so that every backend endpoint rejects unauthenticated requests and every Angular route requiring authentication redirects through Entra External ID. This is F-01 — the privacy guardrail that isolates each user's receipts. Without it, no downstream slice (upload, list, search) can be safely deployed.

## Starting Point

Auth middleware is partially wired: JWT bearer + Authorization pipeline exist in `Program.cs` but no global policy is set, so all endpoints are currently public. On the frontend, `MsalService`, `MsalGuard`, and `MsalBroadcastService` are provided in `app.config.ts`, but no interceptor is registered, no redirect route exists, and `app.routes.ts` is empty.

## Desired End State

Unauthenticated users land on a branded Sign In page at `/`. Clicking Sign In triggers Entra External ID login and returns the user to `/home` (the protected shell). All HTTP requests from Angular to the backend API carry a Bearer token automatically. The backend rejects any request without a valid token with HTTP 401, except the OpenAPI schema endpoint in development.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
|---|---|---|---|
| Backend enforcement | Global fallback policy (default-deny) | New endpoints are protected without per-endpoint ceremony. | Plan |
| OpenAPI in dev | Explicitly anonymous | Preserves schema exploration without a token during development. | Plan |
| Interceptor scope | Only requests to `environment.apiUrl` | Tokens never leak to third-party URLs. | Plan |
| Unauthenticated UX | Landing page with Sign In button | Gives a branded first impression before handing off to Entra. | Plan |
| Protected route structure | Single parent `/home` with `canActivate: [MsalGuard]` | Guard declared once; all future feature routes inherit protection as children. | Plan |
| MSAL redirect route | Dedicated `/auth` route with `MsalRedirectComponent` | Isolates token processing from the app shell lifecycle. | Plan |
| 401 recovery | MSAL default silent renewal | Zero extra code; MSAL interceptor handles token refresh natively. | Plan |

## Scope

**In scope:**
- Backend global fallback authorization policy
- OpenAPI endpoint marked anonymous (dev only)
- `MSAL_GUARD_CONFIG` and `MSAL_INTERCEPTOR_CONFIG` provider registration
- `MsalInterceptor` wired via `withInterceptorsFromDi()`
- `/auth` redirect route with `MsalRedirectComponent`
- `MsalBroadcastService.inProgress$` subscription in App component
- `LandingComponent` at `/` with Sign In button
- `ShellComponent` at `/home` as guard-bearing parent shell
- Final four-route structure in `app.routes.ts`
- Angular scaffold (`app.html`) replaced with `<router-outlet />`
- Unit test fixed with MSAL mock providers

**Out of scope:**
- Logout UI
- Custom 401 error toast
- Navigation bar or authenticated layout (comes in S-02)
- Role-based authorization
- Terraform or CI/CD changes

## Architecture / Approach

Backend change is a single-line policy configuration. Frontend is assembled in two sub-phases: first the MSAL plumbing (interceptor, configs, redirect route), then the UI shell (landing page, protected shell, route structure). The route hierarchy — `MsalGuard` on the `/home` parent — means every future feature route added as a child is automatically protected without re-declaring the guard.

## Phases at a Glance

| Phase | What it delivers | Key risk |
|---|---|---|
| 1. Backend default-deny | All endpoints return 401 without a token; OpenAPI anonymous | None — one config change with immediate verification |
| 2. Frontend MSAL wiring | Bearer token on API requests; `/auth` redirect route; broadcast subscription | `redirectUri: '/auth'` must match Entra app registration |
| 3. Frontend route structure | Landing page, protected shell, full route hierarchy | `MsalGuard` auto-redirects to Entra (not landing page) when `/home` is accessed without auth — correct behavior, may surprise during testing |

**Prerequisites:** Entra External ID app registration must have `http://localhost:4200/auth` as an allowed redirect URI (local dev). API scope must be configured in user secrets (`dotnet user-secrets`).

**Estimated effort:** ~1 session across 3 short phases.

## Open Risks & Assumptions

- Entra External ID app registration redirect URIs must be updated locally before Phase 3 manual testing — `redirectUri: '/auth'` won't work until the registration allows it.
- `environment.externalId.apiScope` is an empty string in `environment.ts` — local dev requires it to be set via user secrets or environment override; the interceptor will send an empty scope otherwise.

## Success Criteria (Summary)

- `GET /weatherforecast` without an `Authorization` header returns HTTP 401.
- Visiting `http://localhost:4200/` shows a landing page with a Sign In button; clicking it reaches the Entra login page.
- After login, the user lands on `/home` and all API requests carry a valid `Authorization: Bearer` header.
