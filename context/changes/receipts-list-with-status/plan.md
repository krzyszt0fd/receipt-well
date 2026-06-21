# Receipts List with Status (S-02) Implementation Plan

## Overview

Add a receipts list screen that shows every receipt the signed-in user has uploaded, with its processing status (`pending` / `ready` / `error`) and — now that S-03 already populates them in production — the extracted store name, purchase date, and tags. The backend gains a single read endpoint over the existing Azure AI Search index; the frontend gains a list page reachable from the existing upload flow. Upload stays the default `/home` landing; the two screens are bridged with simple links rather than a persistent nav bar.

## Current State Analysis

- **No read endpoint exists.** `src/backend/ReceiptWell.Web/Program.cs` only declares `POST /receipts/staging-slot` (`:138-156`) and `POST /receipts/confirm` (`:158-195`). Nothing queries the index for display.
- **The index already has everything this slice needs.** `src/backend/ReceiptWell.Core/ReceiptDocument.cs` declares `UserId` (`IsFilterable`), `UploadedAt` (`IsFilterable, IsSortable`), `Status` (`IsFilterable`), and the S-03 enrichment fields `StoreName?`, `PurchaseDate?`, `Tags`. No schema change is needed — this is a pure read slice.
- **Status values are exhaustive in production today.** `src/backend/ReceiptWell.Core/ReceiptStatus.cs` defines `Pending` / `Ready` / `Error`; all three are reachable now that S-03 ships (`ai-extraction-and-enrichment/change.md` notes `error` was verified live via a non-receipt image upload, but nothing renders it yet).
- **Existing endpoint pattern**: extract `oid` from the JWT (`Program.cs:144-145, :165-166`), delegate to a scoped service, catch-all `Exception` → log Error → `Results.Problem(statusCode: 500)`. `SearchClient` is already registered as a singleton with retry (`Program.cs:91-95`); no new DI wiring needed for search itself.
- **Frontend has exactly one route.** `src/frontend/src/app/app.routes.ts` only wires `/home/upload`; there is no list page, no nav bar, no logout — S-01's plan explicitly deferred "no logout or navigation bar" to this slice, but only the minimum needed to bridge upload ↔ list is in scope here (see What We're NOT Doing).
- **Frontend conventions already established**: Angular Material (indigo-pink theme), signals + `OnPush`, standalone components, `inject()`, reactive error handling distinguishing retriable vs non-retriable failures (`upload.component.ts:130-143`), shared `.page` / `.page-card` layout classes and `_colors.scss` variables (`styles.scss:15-27`, `_colors.scss`) — `$bg-page`, `$primary`, `$error`, `$success`, `$text-secondary`, `$border-subtle`. No `pending`/amber color exists yet.
- **Testing convention**: component specs mock the service via DI override (`upload.component.spec.ts`), no HTTP in unit tests; backend has no test project (by prior decision in `ai-extraction-and-enrichment/plan.md`) — verification is build + manual.

## Desired End State

A signed-in user can navigate from the upload screen to a receipts list and see every receipt they've uploaded, newest first, each showing a color-coded status chip and (when available) store name, purchase date, and up to 5 tags with a "+K more" indicator for the rest. A user with zero receipts sees a friendly empty state with a button back to upload. A manual refresh button always works; while any receipt is still `pending`, the list also quietly re-fetches in the background until everything settles, without flashing a spinner or an error during a transient background failure.

**Verification:** upload a receipt, navigate to the list, see it as `pending` with no metadata; once S-03's pipeline finishes, watch it flip to `ready` (or `error`) with metadata populated — either via the manual refresh button or, once Phase 3 lands, automatically.

### Key Discoveries:

- `ReceiptDocument.cs` — `UserId`/`UploadedAt`/`Status` are already `IsFilterable`/`IsSortable`; no index change needed for `Filter = "UserId eq '{oid}'"` + `OrderBy = ["UploadedAt desc"]`.
- `ai-extraction-and-enrichment/change.md:14-19` — `error` status is real in production now; this slice is the one that must render it (closes checkbox 6.6).
- `upload.component.ts:130-143` — the established retriable-vs-not error pattern; this slice's GET has no request body to validate, so it only needs the retriable branch (network/5xx).
- `_colors.scss` — no amber/pending color exists yet; one new variable is needed for the `pending` chip.
- `styles.scss:23-27` — `.page-card` is capped at `max-width: 480px`, sized for the single-card upload form; a multi-row list needs a wider card.

## What We're NOT Doing

- **No real pagination.** Single fetch, ordered newest-first, capped at 1000 results server-side (Azure AI Search's per-request max). User explicitly deferred proper (client-side "load more") pagination to a post-MVP slice.
- **No manual retry/reprocess action for `error` receipts** — out of scope per `ai-extraction-and-enrichment/plan.md` ("No manual-retry UI for `error` receipts").
- **No persistent nav bar or tab strip** — upload remains the `/home` default; only two contextual links are added ("View my receipts" on the upload-confirmed panel, "Upload receipt" on the list) to bridge the two screens.
- **No logout button** — still out of scope; deferred again, not part of this slice's user decisions.
- **No receipt thumbnails** — S-05.
- **No tag-based search or filtering** — S-04.
- **No expand/collapse interaction for overflow tags** — capped display only, no "show all" toggle.
- **No WebSocket/SignalR push** — live updates are short-polling only.

## Implementation Approach

Three phases, each independently shippable — matching the user's stated fallback ladder (status + manual refresh is must-have; auto-polling is the cuttable enhancement):

1. **Backend** — a single `GET /receipts` endpoint querying the existing index, no schema or infra changes.
2. **Frontend (must-have)** — the list page itself: states (loading / empty / error / populated), status chips, metadata + capped tags, manual refresh, and the two nav links.
3. **Frontend (nice-to-have)** — a self-stopping background poll that only runs while a receipt is still `pending`, layered on top of Phase 2 without changing its contract.

## Critical Implementation Details

- **Search filter scoping.** `oid` is an Entra-issued GUID claim — it cannot contain a `'` — so `Filter = $"UserId eq '{userId}'"` is safe to build directly without OData escaping. Do not add a quote-escaping helper for this; it would be dead code for a claim whose format is guaranteed upstream.
- **`.page-card` width override.** The shared `.page-card` class (`styles.scss:23-27`) is capped at `max-width: 480px`, tuned for the upload form's single card. The list needs more horizontal room for chips + metadata + tags on one row. Add a local modifier class (e.g. `.page-card.receipts-card { max-width: 720px; }`) in the list component's scss rather than widening the shared class — the upload card must stay narrow.
- **List rows: skip `MatListModule`.** Material's list-item API is built around single-line (or rigid multi-line `mat-line`) rows and doesn't comfortably fit a status chip + metadata line + wrapping tag chips per row. Render each receipt as a plain flex/grid `<div>` inside one outer `mat-card`, using `MatChipsModule` only for the status chip and tag chips — not `MatListModule`.
- **Polling vs. initial-load state (Phase 3).** The `loading` signal must only flip during the very first fetch and explicit manual refreshes — not during background polls. A background poll silently replaces `receipts()` on success; on a transient failure it does nothing visible (no error flash) and simply tries again on the next tick. Only the initial/manual fetch path sets the visible error state.
- **Refresh timer ownership (Phase 3).** The manual refresh button must clear any pending poll timeout before issuing its own fetch — otherwise a manual click during an active poll cycle can race a second in-flight request or leave two timers scheduled. One timer handle, always cleared before a new fetch starts and on component destroy.
- **Cancel in-flight fetch before starting a new one (Phase 3).** Clearing the *scheduled* timeout isn't enough: if a background poll's HTTP request is already in flight when the user clicks manual refresh, two GET requests race — whichever resolves second silently overwrites `receipts()`, and both completion handlers may independently schedule a new poll timeout into the single stored handle, leaking the other. Store the active fetch as a cancellable `Subscription` (alongside the timer handle) and `unsubscribe()` any in-flight one — manual or poll — before starting a new fetch. This removes the race outright and prevents the duplicate-timer leak as a side effect.

## Phase 1: Backend — GET /receipts endpoint

### Overview

A single endpoint that queries the existing Azure AI Search index for the caller's own receipts, newest first, and returns the full row (status + metadata + tags) — no new infrastructure, no schema change.

### Changes Required:

#### 1. Receipt summary DTO

**File**: `src/backend/ReceiptWell.Web/Models/ReceiptSummary.cs` (new)

**Intent**: A response-shape record distinct from the index schema (`ReceiptDocument` lives in `ReceiptWell.Core` and stays the frozen index contract; this DTO is API-only).

**Contract**: `record ReceiptSummary(string Id, string FileName, long FileSize, string Status, DateTimeOffset UploadedAt, string? StoreName, DateTimeOffset? PurchaseDate, IReadOnlyList<string> Tags)`. ASP.NET Core's default minimal-API JSON options camelCase the property names automatically — no manual mapping needed beyond constructing the record.

#### 2. Query service

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs` (new)

**Intent**: Encapsulate the Search query, mirroring the existing scoped-service pattern (`ReceiptBlobService`, `ReceiptConfirmService`).

**Contract**: `GetReceiptsAsync(string userId, CancellationToken cancellationToken)` builds `SearchOptions { Filter = $"UserId eq '{userId}'", Size = 1000 }` with `OrderBy.Add("UploadedAt desc")`, calls `SearchClient.SearchAsync<ReceiptDocument>("*", options, cancellationToken)`, and projects each result's `Document` into a `ReceiptSummary`. Returns an empty list when there are no matches (not an error).

#### 3. Endpoint registration

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: `GET /receipts` — extract `oid` exactly like the existing two endpoints, delegate to `ReceiptQueryService`, return the list.

**Contract**: Mirrors the existing endpoints' shape: extract `userId` via `FindFirstValue("oid")`, call the service, `Results.Ok(summaries)` on success, catch-all `Exception` → `LogError` + `Results.Problem(statusCode: 500)`. Register `ReceiptQueryService` as scoped in DI alongside the other two services. Relies on the existing global `FallbackPolicy` (`RequireAuthenticatedUser`) — no explicit `[Authorize]` needed, consistent with the other two endpoints.

#### 4. HTTP examples

**File**: `receipt-well.http`

**Intent**: Keep the example collection in sync per backend convention.

**Contract**: Add `GET {{receipt_well_HostAddress}}/receipts` with `Authorization: Bearer {{token}}`.

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`

#### Manual Verification:

- Confirming a receipt then calling `GET /receipts` shows it with `status: "pending"` and `storeName`/`purchaseDate` null, `tags: []`.
- Once S-03 finishes processing that receipt (or it already has processed receipts in the index), `GET /receipts` reflects `ready`/`error` and the populated fields.
- A second test account's receipts never appear in another account's `GET /receipts` response (filter correctness).
- `GET /receipts` without a bearer token returns 401.
- A brand-new user with zero receipts gets `200 OK` with an empty array, not an error.

**Implementation Note**: Pause for manual confirmation before proceeding.

---

## Phase 2: Frontend — Receipts list page (must-have)

### Overview

The list page itself: fetch on load, render status chips + metadata + capped tags, handle empty/error states, manual refresh, and the two nav links bridging upload and list. This phase alone satisfies the roadmap's stated S-02 outcome and the user's must-have fallback line.

### Changes Required:

#### 1. New pending color variable

**File**: `src/frontend/src/styles/_colors.scss`

**Intent**: The status chip needs a third color (amber) alongside the existing `$error`/`$success`; add it once, shared, rather than hardcoding a hex in component scss.

**Contract**: Add `$pending: #ff9800;` (Material amber, consistent with the existing Material-named comment style in this file).

#### 2. Receipt summary type + service method

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Add the typed shape the backend returns and a method to fetch it, following the existing `getStagingSlot()`/`confirmUpload()` pattern.

**Contract**: Export `interface ReceiptSummary { id: string; fileName: string; fileSize: number; status: 'pending' | 'ready' | 'error'; uploadedAt: string; storeName: string | null; purchaseDate: string | null; tags: string[]; }`. Add `getReceipts(): Observable<ReceiptSummary[]>` → `HttpClient.get<ReceiptSummary[]>(`${environment.apiUrl}/receipts`)`.

#### 3. Receipt list component

**File**: `src/frontend/src/app/receipts/list/list.component.ts` (new), `list.component.html` (new), `list.component.scss` (new)

**Intent**: Standalone, lazy-loaded, `OnPush` component rendering the four states (loading / error / empty / populated). Fetches on `ngOnInit`; a manual refresh button re-runs the same fetch.

**Contract**:
- Class `ReceiptListComponent`, selector `app-receipt-list`.
- Signals: `receipts = signal<ReceiptSummary[]>([])`, `loading = signal(true)`, `error = signal<string | null>(null)`.
- `loadReceipts()`: sets `loading(true)`, clears `error`, subscribes to `receiptService.getReceipts()`; on success sets `receipts` and `loading(false)`; on any error sets a fixed retriable message (no 400-vs-5xx branching needed — this GET has no request body to validate) and `loading(false)`.
- `ngOnInit()` calls `loadReceipts()`. Refresh button (icon button, `mat-icon` "refresh") calls `loadReceipts()` directly.
- Template states, in order: `loading()` → spinner; else `error()` → retriable error card ("Try again" button calling `loadReceipts()`); else `receipts().length === 0` → empty-state card (icon + "No receipts yet" + "Upload your first receipt" button, `routerLink="/home/upload"`); else the populated list.
- Populated list: one outer `mat-card.page-card.receipts-card` (see Critical Implementation Details for the width override) containing a header ("My Receipts" + refresh icon button + an "Upload receipt" button, `routerLink="/home/upload"`, so the populated state — not just the empty state — bridges back to upload) and one row per receipt — plain flex/grid divs, not `MatListModule` (see Critical Implementation Details).
- Each row: status chip (`mat-chip`, class bound to `pending`/`ready`/`error` for color via the new `$pending`/`$error`/`$success` vars), file name, `uploadedAt` formatted via `DatePipe` (`'short'`), and — only when present — `storeName`, `purchaseDate` (`DatePipe`, `'mediumDate'`), and tags.
- Tags: a helper method caps display — `visibleTags(tags) => tags.slice(0, 5)` and `extraTagCount(tags) => Math.max(0, tags.length - 5)` — rendered as small chips plus a plain "+K more" text node when `extraTagCount > 0`. No expand interaction.
- Imports: `MatCardModule`, `MatChipsModule`, `MatButtonModule`, `MatIconModule`, `MatProgressSpinnerModule`, `RouterLink`, `DatePipe`.

#### 4. Route wiring

**File**: `src/frontend/src/app/app.routes.ts`

**Intent**: Add the list as a lazy-loaded sibling of `upload` under `/home`. Upload remains the default (`redirectTo: 'upload'` is unchanged).

**Contract**: Add `{ path: 'receipts', loadComponent: () => import('./receipts/list/list.component').then(m => m.ReceiptListComponent) }` to the existing `children` array.

#### 5. Nav link — upload → list

**File**: `src/frontend/src/app/receipts/upload/upload.component.ts`, `upload.component.html`

**Intent**: From the confirmed-upload panel, let the user jump to the list to see the receipt they just uploaded.

**Contract**: Add `RouterLink` to the component's imports. In the `confirmed` state's `mat-card-actions`, add a second button: `routerLink="/home/receipts"`, label "View my receipts", alongside the existing "Upload another" button.

#### 6. HTTP examples

**File**: `receipt-well.http`

(Already covered in Phase 1 — no further change here; listed for completeness only if Phase 1's addition needs no update.)

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm --prefix src/frontend run build`
- Frontend unit tests pass: `npm --prefix src/frontend test`

#### Manual Verification:

- Navigating to `/home/receipts` shows the populated list with correct status chip colors, metadata, and capped tags for a receipt with more than 5 tags ("+K more" shown).
- A brand-new account with zero receipts sees the empty-state card; its button navigates to `/home/upload`.
- Stopping the backend and clicking refresh shows the retriable error card with a working "Try again".
- From the upload-confirmed panel, "View my receipts" navigates to `/home/receipts`; from the list, "Upload receipt" navigates to `/home/upload`.
- AXE check passes on the list page in all four states (loading / error / empty / populated).
- Mobile viewport: rows remain readable without horizontal scroll.

**Implementation Note**: Pause for manual confirmation before proceeding. This phase alone satisfies the must-have fallback line — Phase 3 is the first cut candidate if time runs short.

---

## Phase 3: Frontend — Auto-poll while pending (nice-to-have)

### Overview

Layer a self-stopping background poll on top of Phase 2: while any row is `pending`, silently re-fetch every few seconds; stop as soon as none are. No visible loading state during background polls; no error flash on a transient background failure.

### Changes Required:

#### 1. Polling logic

**File**: `src/frontend/src/app/receipts/list/list.component.ts`

**Intent**: After every successful fetch, check whether any receipt is still `pending`; if so, schedule a silent re-fetch; if not, stop.

**Contract**: Add `hasPending = computed(() => this.receipts().some(r => r.status === 'pending'))`. After a successful `loadReceipts()` resolves (initial, manual, or background), if `hasPending()` is true, schedule `setTimeout(() => this.pollReceipts(), 5000)` and store the handle. `pollReceipts()` is a silent variant of the fetch path that does **not** touch the `loading` signal and, on failure, does nothing visible (see Critical Implementation Details — "Polling vs. initial-load state"). Every fetch path (`ngOnInit`'s initial call, manual refresh, and `pollReceipts()`) must, before subscribing: (1) clear any existing poll timeout via the stored handle (see Critical Implementation Details, "Refresh timer ownership"), and (2) `unsubscribe()` any previously stored in-flight `Subscription` (see Critical Implementation Details, "Cancel in-flight fetch before starting a new one"), then store the new `Subscription` returned by `.subscribe(...)`. Clear both the timeout and the subscription in `ngOnDestroy()` as well.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm --prefix src/frontend run build`
- Frontend unit tests pass (including a `fakeAsync`/`tick`-based test asserting polling continues while a row is `pending` and stops once all rows are settled): `npm --prefix src/frontend test`

#### Manual Verification:

- Upload a receipt, immediately open the list: the `pending` chip flips to `ready`/`error` automatically within a few poll cycles, with no manual action.
- Once every row is settled, confirm (via browser DevTools → Network) that no further `GET /receipts` calls happen.
- Clicking manual refresh while a poll is in flight does not produce two overlapping requests or duplicate timers.
- Navigating away from the list page stops the timer (no console errors, no further network calls after navigation).

**Implementation Note**: This is the final phase. If cut for time, Phase 2's manual refresh remains the supported way to see status changes.

---

## Testing Strategy

### Unit Tests:

- `list.component.spec.ts`: mock `ReceiptService` (pattern from `upload.component.spec.ts`) — renders the populated state correctly, renders the empty state for `[]`, renders the error state on a thrown/errored observable, caps tags at 5 with the correct "+K more" count, and (Phase 3) polls while pending and stops once settled (`fakeAsync`/`tick`).

### Integration Tests:

- None — no backend test project exists (prior project decision); the GET endpoint is verified manually against a live or Azurite-backed index per Phase 1's manual steps.

### Manual Testing Steps:

1. Confirm a receipt, call `GET /receipts` directly (or via the new list page) — verify `pending` with null/empty metadata.
2. Wait for (or already have) an S-03-processed receipt — verify `ready`/`error` with populated fields shows correctly.
3. Empty account → empty-state card → "Upload your first receipt" button works.
4. Stop the backend → refresh → retriable error card → restart backend → "Try again" succeeds.
5. Receipt with 6+ tags → exactly 5 chips shown + "+1 more" (or correct count).
6. (Phase 3) Upload then watch the list without touching anything — status flips automatically; network tab shows polling stop once settled.

## Performance Considerations

- **Single-request cap (`Size = 1000`)** comfortably covers the PRD's stated small/MVP data volume; a future slice adds real (client-side "load more") pagination if a user exceeds this.
- **Poll interval (5s)** while pending is negligible Search query cost at MVP scale and stops itself as soon as nothing is pending — no idle background cost once a user's receipts have all settled.

## Migration Notes

No data migration — this is a pure read addition over the existing index; no schema change.

## References

- Roadmap: `context/foundation/roadmap.md` §S-02
- PRD: `context/foundation/prd.md` §US-01 (AC: processing status visible), §NFR (status distinguishable)
- S-03 deferred rendering note: `context/changes/ai-extraction-and-enrichment/change.md` ("6.6 split: S-02 rendering deferred")
- S-03 plan (frozen schema, status values): `context/changes/ai-extraction-and-enrichment/plan.md`
- S-01 plan (existing patterns, deferred nav/logout): `context/changes/receipt-upload-confirm/plan.md`
- Existing endpoint pattern: `src/backend/ReceiptWell.Web/Program.cs:138-195`
- Existing error-handling pattern: `src/frontend/src/app/receipts/upload/upload.component.ts:130-143`
- Shared layout/colors: `src/frontend/src/styles.scss`, `src/frontend/src/styles/_colors.scss`
- Lessons (400 vs transient errors): `context/foundation/lessons.md` ("400 errors are not retriable")

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend — GET /receipts endpoint

#### Automated

- [x] 1.1 Solution builds with zero warnings (`dotnet build src/backend/ReceiptWell.sln`) — 1217bca

#### Manual

- [x] 1.2 New receipt shows `pending` with null/empty metadata via `GET /receipts` — 1217bca
- [x] 1.3 Processed receipt shows `ready`/`error` with populated fields — 1217bca
- [x] 1.4 Cross-account isolation verified (filter correctness) — 1217bca
- [x] 1.5 `GET /receipts` without token returns 401 — 1217bca
- [x] 1.6 New user with zero receipts gets `200 OK` with an empty array — 1217bca

### Phase 2: Frontend — Receipts list page (must-have)

#### Automated

- [ ] 2.1 Frontend builds (`npm --prefix src/frontend run build`)
- [ ] 2.2 Frontend unit tests pass (`npm --prefix src/frontend test`)

#### Manual

- [ ] 2.3 Populated list shows correct chip colors, metadata, and capped tags
- [ ] 2.4 Empty state shown for zero receipts; button navigates to upload
- [ ] 2.5 Retriable error card on fetch failure; "Try again" works
- [ ] 2.6 Nav links work both directions (upload ↔ list)
- [ ] 2.7 AXE check passes in all four states
- [ ] 2.8 Mobile viewport remains readable without horizontal scroll

### Phase 3: Frontend — Auto-poll while pending (nice-to-have)

#### Automated

- [ ] 3.1 Frontend builds (`npm --prefix src/frontend run build`)
- [ ] 3.2 Frontend unit tests pass, including polling start/stop test (`npm --prefix src/frontend test`)

#### Manual

- [ ] 3.3 Pending receipt flips status automatically without manual action
- [ ] 3.4 Polling stops once all rows are settled (verified via Network tab)
- [ ] 3.5 Manual refresh during active poll does not double-fetch or duplicate timers
- [ ] 3.6 Navigating away stops the timer cleanly
