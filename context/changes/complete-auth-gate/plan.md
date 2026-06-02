# Complete Auth Gate — Implementation Plan

## Overview

Wire the full authorization gate for ReceiptWell: a backend default-deny policy (all endpoints require authentication unless explicitly opted out) and Angular route guard with MSAL interceptor, redirect handling, and a public landing page with a Sign In button. This is roadmap item F-01 and a prerequisite for every slice that follows.

## Current State Analysis

- **Backend**: `AddAuthentication`, `AddAuthorization`, `UseAuthentication`, `UseAuthorization` are already in the DI container and middleware pipeline (`src/backend/Program.cs:27–45`). The only endpoint `/weatherforecast` (`Program.cs:52`) has no `.RequireAuthorization()` and there is no global fallback policy — all endpoints are currently public.
- **Frontend**: `MsalService`, `MsalGuard`, and `MsalBroadcastService` are provided (`src/frontend/src/app/app.config.ts:5–37`). `MsalInterceptor` is not registered, no MSAL guard or interceptor config tokens exist, `app.routes.ts` is empty (`[]`), and `MsalRedirectComponent` has no route.
- **`app.html`**: still contains the Angular scaffold template (Angular logo, pill links, h1 "Hello, frontend") with a `<router-outlet />` appended at the bottom. The spec test (`app.spec.ts:17`) asserts this h1 text.

## Desired End State

- Every backend endpoint rejects unauthenticated requests (HTTP 401) except `MapOpenApi()` in development.
- Unauthenticated Angular users visiting `/` see a branded landing page with a Sign In button.
- Clicking Sign In initiates Entra External ID login redirect targeting `/home` post-auth.
- After authentication the browser returns to `/auth`, MSAL processes the token, and the user navigates to `/home`.
- All HTTP requests from Angular to `environment.apiUrl` carry a valid Bearer token automatically.
- Authenticated sessions survive page refresh (token already persists in LocalStorage per existing MSAL config).

### Key Discoveries

- `provideHttpClient(withInterceptorsFromDi())` is already in `app.config.ts:18` — class-based `MsalInterceptor` slots in as a plain provider without changing the `provideHttpClient` call.
- `environment.externalId.apiScope` exists in both environment files (`environment.ts:7`, `environment.prod.ts:7` as `'__API_SCOPE__'` placeholder).
- Central package management is ON (`src/backend/Directory.Packages.props`) — no `Version` attribute goes in `ReceiptWell.csproj`; versions go only in `Directory.Packages.props`. Run `dotnet restore` after any NuGet change.
- `UserSecretsId` is set in `ReceiptWell.csproj` — Entra Authority and ClientId for local dev go in user secrets, not in `appsettings.json`.

## What We're NOT Doing

- No role-based authorization — single user type, no roles (PRD §Access Control).
- No logout UI — not required by F-01; first slice that needs it is S-01+.
- No custom 401 interceptor or error toast — MSAL interceptor handles silent renewal natively.
- No navigation bar or authenticated layout — shell is a thin placeholder; navigation arrives in S-02.
- No Terraform or CI/CD changes — infra already has Entra External ID config wired.

## Implementation Approach

Three sequentially verifiable phases. Each phase has a hard stop with manual verification before the next begins:

1. **Backend** — one config change makes all endpoints default-deny; verifiable immediately without touching the frontend.
2. **Frontend MSAL wiring** — interceptor, guard config, redirect route, and broadcast subscription; verifiable by checking network requests carry `Authorization: Bearer`.
3. **Frontend route structure** — landing page + shell + final routes; verifiable end-to-end via the full sign-in flow.

## Critical Implementation Details

**MSAL redirect URI must match Entra app registration**: `redirectUri: '/auth'` set in `app.config.ts` must be registered in the Entra External ID application (done via Terraform/user secrets — not in git-tracked files). Failure here causes MSAL to throw `redirect_uri_mismatch` after login.

**`withInterceptorsFromDi()` must not be replaced**: `MsalInterceptor` is class-based and requires this setup. Switching to functional interceptors would break MSAL token attachment.

**`MsalGuard` default behavior**: by default `MsalGuard` initiates a login redirect to Entra when the user is not authenticated. `loginFailedRoute: '/'` applies only when the login interaction itself fails (e.g., user cancels). Visiting `/home` without auth triggers Entra redirect, not a landing page redirect.

---

## Phase 1: Backend — Default-Deny Authorization Policy

### Overview

Replace the bare `AddAuthorization()` call with a configured version that sets `FallbackPolicy` to require an authenticated user. Mark `MapOpenApi()` as anonymous so the OpenAPI schema remains accessible in development without a token.

### Changes Required

#### 1. Global fallback policy + anonymous OpenAPI

**File**: `src/backend/Program.cs`

**Intent**: Add a `FallbackPolicy` to `AddAuthorization` so every endpoint that does not explicitly opt out requires an authenticated user. Chain `.AllowAnonymous()` on `MapOpenApi()` to preserve dev-time schema exploration.

**Contract**: Replace the `builder.Services.AddAuthorization()` call at line 33 with an options lambda that sets `options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()`. Chain `.AllowAnonymous()` on the `app.MapOpenApi()` call inside the `IsDevelopment()` block. No changes needed on `/weatherforecast` — the fallback policy covers it automatically.

#### 2. Update HTTP requests collection

**File**: `receipt-well.http`

**Intent**: Reflect that `/weatherforecast` now requires a token so developers can still exercise the endpoint locally.

**Contract**: Add a `@token = ` variable at the top of the file and an `Authorization: Bearer {{token}}` header on the weatherforecast request.

### Success Criteria

#### Automated Verification

- Backend builds without warnings: `dotnet build src/backend/ReceiptWell.csproj`

#### Manual Verification

- `GET /weatherforecast` without `Authorization` header returns `401 Unauthorized`
- `GET /openapi/v1.json` in development (no header) returns HTTP 200 with the schema

**Pause here for manual confirmation before proceeding to Phase 2.**

---

## Phase 2: Frontend — MSAL Interceptor, Guard Config, Redirect Route

### Overview

Register `MsalInterceptor` and its config in `app.config.ts`, add the `/auth` redirect route for Entra callbacks, subscribe to MSAL broadcast events in `App`, and fix the existing unit test.

### Changes Required

#### 1. MSAL guard config, interceptor config, and MsalInterceptor provider

**File**: `src/frontend/src/app/app.config.ts`

**Intent**: Add three providers: `MSAL_GUARD_CONFIG` (redirect interaction type, API scope, fallback route), `MSAL_INTERCEPTOR_CONFIG` (protected resource map for the backend API URL), and `MsalInterceptor` as a class provider so `withInterceptorsFromDi()` picks it up. Update `redirectUri` in the `PublicClientApplication` config from `'/'` to `'/auth'`.

**Contract**:
- Import from `@azure/msal-angular`: `MSAL_GUARD_CONFIG`, `MsalGuardConfiguration`, `MSAL_INTERCEPTOR_CONFIG`, `MsalInterceptorConfiguration`, `MsalInterceptor`.
- Import `InteractionType` from `@azure/msal-browser`.
- `MSAL_GUARD_CONFIG` value: `{ interactionType: InteractionType.Redirect, authRequest: { scopes: [environment.externalId.apiScope] }, loginFailedRoute: '/' }`.
- `MSAL_INTERCEPTOR_CONFIG` value: `{ interactionType: InteractionType.Redirect, protectedResourceMap: new Map([[environment.apiUrl, [environment.externalId.apiScope]]]) }`.
- Add `MsalInterceptor` as a bare class entry in the `providers` array (not as a token/useClass pair — just the class, same as `MsalService` etc.).

#### 2. Redirect route for Entra callback

**File**: `src/frontend/src/app/app.routes.ts`

**Intent**: Add a public `/auth` route mounting `MsalRedirectComponent` so Entra redirect callbacks are processed and token state is initialized before Angular navigates to the post-login destination.

**Contract**: Import `MsalRedirectComponent` from `@azure/msal-angular`. Add `{ path: 'auth', component: MsalRedirectComponent }` as the first entry. No `canActivate`.

#### 3. MSAL broadcast subscription in App component

**File**: `src/frontend/src/app/app.ts`

**Intent**: Subscribe to `MsalBroadcastService.inProgress$` in the app shell so MSAL's interaction status signals are consumed. Remove the unused scaffold `title` signal.

**Contract**: Implement `OnInit`. Inject `MsalBroadcastService` via `inject()` and `DestroyRef` via `inject()`. In `ngOnInit`, subscribe to `inProgress$.pipe(filter(s => s === InteractionStatus.None), takeUntilDestroyed(destroyRef))` with an empty observer. Remove the `title` signal declaration. Imports: `OnInit`, `DestroyRef`, `inject` from `@angular/core`; `filter` from `rxjs/operators`; `takeUntilDestroyed` from `@angular/core/rxjs-interop`; `InteractionStatus` from `@azure/msal-browser`; `MsalBroadcastService` from `@azure/msal-angular`.

#### 4. Fix unit test

**File**: `src/frontend/src/app/app.spec.ts`

**Intent**: Provide minimal MSAL mocks so `App` can be instantiated in the test bed. Remove the stale `should render title` assertion — the h1 scaffold content is cleared in Phase 3.

**Contract**: Import `MsalBroadcastService` from `@azure/msal-angular` and `Subject` from `rxjs`. Add `{ provide: MsalBroadcastService, useValue: { inProgress$: new Subject() } }` to `TestBed` providers. Delete the `should render title` test block entirely. Keep `should create the app`.

### Success Criteria

#### Automated Verification

- Angular build succeeds: `ng build` (run from `src/frontend/`)
- Unit test passes: `ng test` — `should create the app` passes

#### Manual Verification

- Open browser DevTools Network tab, trigger a call to the backend (even a 401 response) — the request carries `Authorization: Bearer <token>` header when the user is authenticated
- Navigating to `http://localhost:4200/auth` does not throw a console error

**Pause here for manual confirmation before proceeding to Phase 3.**

---

## Phase 3: Frontend — Landing Page, Protected Shell, Route Structure

### Overview

Create a public `LandingComponent` with a Sign In button, a minimal `ShellComponent` as the protected parent for all future feature routes, and wire the final route structure. Replace the Angular scaffold content in `app.html` with only `<router-outlet />`.

### Changes Required

#### 1. Landing component

**File**: `src/frontend/src/app/landing/landing.ts` (new file)

**Intent**: Public component at `/`. If the user already has an active MSAL account, auto-redirects to `/home`. Otherwise renders a Sign In button that initiates MSAL login redirect targeting `/home` as the post-auth destination.

**Contract**: Standalone component with `ChangeDetectionStrategy.OnPush`. Inject `MsalService` and `Router` via `inject()`. In `ngOnInit` (implements `OnInit`): check `msalService.instance.getAllAccounts().length > 0` and if true call `router.navigate(['/home'])`. Sign In button handler calls `msalService.loginRedirect({ scopes: [environment.externalId.apiScope], redirectStartPage: '/home' })`. Button must have an accessible label for AXE compliance.

#### 2. Shell component

**File**: `src/frontend/src/app/shell/shell.ts` (new file)

**Intent**: Thin protected shell component that hosts `<router-outlet>` for all authenticated feature routes. No visual content of its own — guard-bearing parent for S-01, S-02, S-03, S-04 child routes.

**Contract**: Standalone component with `ChangeDetectionStrategy.OnPush`, `imports: [RouterOutlet]`, inline template containing only `<router-outlet />`.

#### 3. Final route structure

**File**: `src/frontend/src/app/app.routes.ts`

**Intent**: Replace the single `/auth` entry (from Phase 2) with the complete four-route structure: redirect handler, landing, protected shell, wildcard fallback.

**Contract**:
```typescript
export const routes: Routes = [
  { path: 'auth', component: MsalRedirectComponent },
  { path: '', component: LandingComponent },
  {
    path: 'home',
    component: ShellComponent,
    canActivate: [MsalGuard],
    children: []
  },
  { path: '**', redirectTo: '' }
];
```
`children: []` is intentional — S-01 adds the first child route.

#### 4. Clear app.html scaffold

**File**: `src/frontend/src/app/app.html`

**Intent**: Remove the Angular scaffold template (Angular logo, pill links, h1, styles). The App component has no visual content of its own — routing provides all content.

**Contract**: File contains only `<router-outlet />`.

#### 5. Clear app.ts scaffold remnant

**File**: `src/frontend/src/app/app.ts`

**Intent**: Ensure `RouterOutlet` is imported in the component (needed since `app.html` uses it) and no dead imports remain after removing the title signal in Phase 2.

**Contract**: `imports` array in `@Component` contains `RouterOutlet`. No other imports from `@angular/core` beyond what Phase 2 added.

### Success Criteria

#### Automated Verification

- Angular build succeeds: `ng build` (run from `src/frontend/`)
- Unit tests pass: `ng test`

#### Manual Verification

- `http://localhost:4200/` shows the landing page with a Sign In button (user not authenticated)
- Clicking Sign In redirects to the Entra External ID login page
- After completing login, browser navigates to `http://localhost:4200/home` (blank page with no errors is correct — no child routes yet)
- Visiting `/home` directly without being authenticated redirects to Entra login (MsalGuard fires)
- Page refresh while authenticated stays on `/home` (token persists in LocalStorage)
- No console errors during the full sign-in flow

---

## Testing Strategy

### Unit Tests

- `app.spec.ts`: `should create the app` — App instantiates with mocked `MsalBroadcastService`

### Manual Testing Steps

1. Start backend: `dotnet run --project src/backend` — confirm `/weatherforecast` returns 401 without a token
2. Start frontend: `ng serve` (from `src/frontend/`) — navigate to `http://localhost:4200/`
3. Confirm landing page renders with Sign In button
4. Click Sign In — Entra login page loads
5. Complete login — browser lands on `/home` (blank page, no errors)
6. Refresh — still on `/home`, still authenticated
7. Open Network tab — API requests from the app carry `Authorization: Bearer <token>`

## References

- Roadmap: `context/foundation/roadmap.md` §F-01 (complete-auth-gate)
- PRD: `context/foundation/prd.md` §Uwierzytelnianie, §Access Control
- Backend: `src/backend/Program.cs`
- Frontend config: `src/frontend/src/app/app.config.ts`
- Frontend routes: `src/frontend/src/app/app.routes.ts`
- NuGet central versioning: `src/backend/Directory.Packages.props`

---

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles.

### Phase 1: Backend — Default-Deny Authorization Policy

#### Automated

- [x] 1.1 Backend builds without warnings: `dotnet build src/backend/ReceiptWell.csproj`

#### Manual

- [x] 1.2 `GET /weatherforecast` without Authorization header returns 401
- [x] 1.3 `GET /openapi/v1.json` in development (no header) returns HTTP 200

### Phase 2: Frontend — MSAL Interceptor, Guard Config, Redirect Route

#### Automated

- [ ] 2.1 Angular build succeeds: `ng build` (from `src/frontend/`)
- [ ] 2.2 Unit test passes: `ng test` — `should create the app` passes

#### Manual

- [ ] 2.3 Authenticated API requests carry `Authorization: Bearer` header in DevTools Network tab
- [ ] 2.4 Navigating to `/auth` does not throw a console error

### Phase 3: Frontend — Landing Page, Protected Shell, Route Structure

#### Automated

- [ ] 3.1 Angular build succeeds: `ng build` (from `src/frontend/`)
- [ ] 3.2 Unit tests pass: `ng test`

#### Manual

- [ ] 3.3 `http://localhost:4200/` shows landing page with Sign In button (unauthenticated)
- [ ] 3.4 Clicking Sign In redirects to Entra External ID login page
- [ ] 3.5 After login, browser navigates to `/home` with no console errors
- [ ] 3.6 Visiting `/home` without auth redirects to Entra login
- [ ] 3.7 Page refresh while authenticated stays on `/home`
