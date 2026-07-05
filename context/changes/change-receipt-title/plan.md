# Inline-Editable Receipt Filename (Update / CRUD completion) Implementation Plan

## Overview

Close the last missing CRUD operation — **Update** — by making a receipt's existing
`FileName` field inline-editable in the list view. A new `PUT /receipts/{id}` endpoint
renames the field via a Search-index partial merge (no new schema field, no blob
mutation, no concurrency guard), and the list row gains a pencil-button inline editor.
The change mirrors the just-shipped `receipt-delete` pattern on both the backend
(service + endpoint + result union) and the frontend (per-row action + local signal
update).

## Current State Analysis

- **Backend CRUD surface** lives entirely in `Program.cs` minimal-API handlers:
  Create (`POST /receipts/staging-slot` + `POST /receipts/confirm`), Read
  (`GET /receipts`), Delete (`DELETE /receipts/{id}`). No `MapPut`/`MapPatch` exists —
  Update is the one gap.
- **The Web project talks to `SearchClient` directly** — there is no "store"
  abstraction in the Web project. (`ReceiptStore.cs` referenced in the frame lives in
  `ReceiptWell.Functions`, the *background* AI writer, a separate project.) Writes go
  through `SearchClient.MergeOrUploadDocumentsAsync`, which performs a **partial merge**:
  `ReceiptConfirmService.cs:106` uses it to write a document, and the background writer
  uses the same call to write only `StoreName`/`PurchaseDate`/`Tags`/`TagsPl`/`Status`.
- **`FileName` is race-free.** The background extraction pipeline never writes
  `FileName` (frame brief, `ReceiptStore.cs:72-73` in Functions). It is set once at
  confirm time (`ReceiptConfirmService.cs:98`) and is the always-present fallback
  identifier rendered unconditionally in every list row (`list.component.html:76`).
  Renaming it needs no guard against the AI writer.
- **`FileName` has no other technical role.** Blob paths key off `{userId}/{receiptId}`
  (`ReceiptConfirmService.cs:85`), not the name. The blob's
  `x-ms-blob-content-disposition` carries the original name for download but no reader
  depends on the Search `FileName` value matching it. Renaming is therefore
  **Search-index-only** — strictly simpler than delete, which also mutates blob storage.
- **Delete is a complete template.** `ReceiptDeleteService.cs:24-69` is the exact
  service shape (`GetDocumentAsync` → 404 catch → ownership check → mutate → result
  union `Success`/`NotFound`/`Forbidden`). `Program.cs:220-245` is the endpoint +
  `switch` template. `ReceiptDeleteTests.cs` is the integration-test template
  (NSubstitute on `factory.SearchClient`, `TestAuthHandler.OidHeader`, per-branch
  assertions). Frontend `list.component.ts:162-176` + `list.component.html:76-85` is the
  per-row-action + local-signal-update template.
- **No detail/edit route exists** (`app.routes.ts` registers only `receipts/upload` and
  `receipts`) and no `GET /receipts/{id}`. Editing must happen inline in the list row —
  which is the intended design, not a limitation.

## Desired End State

A signed-in user viewing their receipt list can click a pencil button on any row, edit
the filename inline, and save (Enter / blur / check button) or cancel (Esc / X button).
On save the row updates instantly; on failure it reverts and a snackbar explains. The
backend exposes `PUT /receipts/{id}` that renames only `FileName`, enforcing ownership
(403), existence (404), and a non-empty name (400). Verify by: renaming a receipt and
seeing the new name persist across a refresh; confirming another user's receipt cannot
be renamed; confirming an empty name is rejected; `dotnet test` and `npm test` green.

### Key Discoveries:

- Partial-merge write mechanism: `ReceiptConfirmService.cs:106`
  (`MergeOrUploadDocumentsAsync` merges only supplied fields — writing `{ Id, FileName }`
  touches nothing else).
- Service/result-union template: `ReceiptDeleteService.cs:8-69`.
- Endpoint/switch template: `Program.cs:220-245`; DI registration site:
  `Program.cs:100-103`.
- Backend integration-test template: `ReceiptDeleteTests.cs` (whole file).
- Frontend per-row-action + local-signal-update template: `list.component.ts:162-176`,
  `list.component.html:66-86`.
- Frontend error-snackbar precedent: `list.component.ts:173`.
- `.http` collection must be updated per `src/backend/.claude/CLAUDE.md` ("Maintain
  example endpoints collection"); delete entry at `receipt-well.http:69`.

## What We're NOT Doing

- **Not** introducing a new `Title` field — we rename the existing `FileName`
  (frame-settled decision).
- **Not** making `storeName`/`purchaseDate`/`tags` editable — those are written
  asynchronously by the AI pipeline and carry a documented silent-overwrite race that
  needs its own design; explicitly a separate, harder CRUD gap.
- **Not** mutating blob storage or the blob's content-disposition — rename is
  Search-index-only.
- **Not** adding a detail route or `GET /receipts/{id}` — editing is inline in the list.
- **Not** adding a concurrency guard, edit-lock, or "user edited" per-field flags — the
  AI writer never touches `FileName`.
- **Not** blocking edits while a receipt is `pending` — `FileName` is safe to edit in
  any status.

## Implementation Approach

Mirror the delete change end to end. Backend first (Phase 1): a `ReceiptRenameService`
with the same result-union shape as `ReceiptDeleteService`, a `PUT /receipts/{id}`
handler that maps results to `200`/`404`/`403` and rejects an empty name with `400`, DI
registration, `.http` entry, and integration tests covering every result branch. Then
the frontend (Phase 2): a `renameReceipt` service method, a pencil-button inline editor
in the list row driven by a per-row edit-mode signal, optimistic local update with
revert-on-error, and a component spec. Each phase ends at a green test suite.

## Critical Implementation Details

- **Partial merge is the whole trick.** The rename must send a `ReceiptDocument` (or
  equivalent) containing only `Id` and `FileName` to `MergeOrUploadDocumentsAsync`.
  Sending a fuller object would overwrite AI-written fields with defaults. Use
  `MergeDocumentsAsync` or a minimally-populated doc — do not round-trip the whole
  fetched document back as an upload, since fields not re-set would be clobbered. (The
  fetched document is used only for the ownership check.)
- **Ownership check before mutate.** Fetch via `GetDocumentAsync<ReceiptDocument>(id)`
  first; a `RequestFailedException` with `Status == 404` → `NotFound`; a `UserId`
  mismatch → `Forbidden` (logged at Warning, mirroring `ReceiptDeleteService.cs:75-77`).
  Only then merge.
- **Empty-name defense in depth.** The client blocks empty/whitespace, but the endpoint
  must independently reject an empty/whitespace `fileName` as a `ValidationProblem`
  (400) — an empty filename would destroy the always-present fallback identifier the
  whole feature depends on.
- **A11y focus management.** Entering edit mode must move focus into the input; leaving
  edit mode (save or cancel) must return focus to a stable control (the pencil button).
  Required to pass the project's AXE / WCAG AA gate.

## Phase 1: Backend — Rename Service, `PUT` Endpoint & Tests

### Overview

Add the server-side Update operation: a rename service, a `PUT /receipts/{id}` endpoint,
DI wiring, the `.http` example, and integration tests covering all four result branches.

### Changes Required:

#### 1. Rename service

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptRenameService.cs` (new)

**Intent**: Encapsulate the rename operation with the same result-union + ownership-guard
shape as `ReceiptDeleteService`, mutating only the Search index.

**Contract**: `public abstract record ReceiptRenameResult` with nested
`Success`/`NotFound`/`Forbidden` records (mirror `ReceiptDeleteResult`).
`public partial class ReceiptRenameService(SearchClient searchClient, ILogger<...> logger)`
exposing `Task<ReceiptRenameResult> RenameAsync(string receiptId, string userId, string newFileName)`.
Flow: `GetDocumentAsync<ReceiptDocument>` (catch 404 → `NotFound`), `UserId` mismatch →
`Forbidden` (log Warning), else `MergeOrUploadDocumentsAsync` with a document carrying
only `Id` + `FileName` → `Success` (log Information). Source-generated `[LoggerMessage]`
methods per the backend logging convention (log identity id + receipt id). No blob
client dependency.

#### 2. `PUT /receipts/{id}` endpoint + request DTO

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Expose the rename operation, mapping service results to HTTP and rejecting an
empty name at the boundary.

**Contract**: `app.MapPut("/receipts/{id}", ...)` mirroring the `MapDelete` handler
(`Program.cs:220-245`): resolve `userId` via `httpContext.User.GetUserId()`, bind a
`RenameRequest(string FileName)` record body. If `FileName` is null/whitespace →
`Results.ValidationProblem` with a `["fileName"]` message (400). Otherwise
`switch` on the result: `Success` → `Results.Ok`/`NoContent`, `NotFound` →
`Results.NotFound()`, `Forbidden` → `Results.Forbid()`. `catch` → honest
`Results.Problem(statusCode: 500)` with an Error log. Add
`record RenameRequest(string FileName);` alongside `ConfirmRequest`
(`Program.cs:249`).

#### 3. DI registration

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Register the new scoped service.

**Contract**: `builder.Services.AddScoped<ReceiptRenameService>();` beside the other
receipt services (`Program.cs:100-103`).

#### 4. Example request collection

**File**: `receipt-well.http`

**Intent**: Keep the endpoint collection current per the backend CLAUDE.md rule.

**Contract**: Add a `PUT {{hostAddress}}/receipts/{{receiptId}}` entry with a JSON body
`{ "fileName": "..." }`, placed near the `DELETE` entry (`receipt-well.http:69`).

#### 5. Backend integration tests

**File**: `src/backend/ReceiptWell.Tests/ReceiptRenameTests.cs` (new)

**Intent**: Cover every result branch, mirroring `ReceiptDeleteTests`.

**Contract**: `IClassFixture<ReceiptWellWebFactory>`. Tests:
(a) rename of another user's receipt → 403, and `MergeOrUploadDocumentsAsync` never
called (`DidNotReceiveWithAnyArgs`);
(b) rename of a nonexistent receipt (mock `GetDocumentAsync` throws
`RequestFailedException(404)`) → 404;
(c) rename of an owned receipt → 200/204 and `MergeOrUploadDocumentsAsync` received once
with a document whose `FileName` is the new value and other AI fields unset;
(d) empty/whitespace `fileName` → 400 and no merge call.
Use `TestAuthHandler.OidHeader`, `factory.SearchClient` NSubstitute mocks, and
`HttpMethod.Put` requests as in `ReceiptDeleteTests.cs`.

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- Backend tests pass: `dotnet test src/backend/ReceiptWell.sln`
- New `ReceiptRenameTests` cover 200, 404, 403, and 400 branches (all green)

#### Manual Verification:

- `PUT /receipts/{id}` with a valid `{ "fileName": "..." }` via `receipt-well.http`
  returns success and the new name appears in a subsequent `GET /receipts`
- Renaming another user's receipt returns 403 with no change
- An empty `fileName` returns 400
- AI-extracted fields (`storeName`/`purchaseDate`/`tags`) on the renamed receipt are
  unchanged after the rename

**Implementation Note**: After completing this phase and all automated verification
passes, pause for manual confirmation before proceeding to Phase 2.

---

## Phase 2: Frontend — Inline-Edit UI & Tests

### Overview

Add the inline filename editor to the list row: a service method, a per-row edit-mode
signal, the input + save/cancel controls with keyboard behavior, optimistic update with
revert-on-error, accessibility, and a component spec.

### Changes Required:

#### 1. Rename service method

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Call the new endpoint.

**Contract**: `renameReceipt(id: string, fileName: string): Observable<...>` issuing
`this.http.put(`${environment.apiUrl}/receipts/${id}`, { fileName })`, mirroring
`deleteReceipt` (`receipt.service.ts:40-42`). Return type matches the endpoint's success
body (or `void` if `NoContent`).

#### 2. List component — edit-mode state & save/cancel logic

**File**: `src/frontend/src/app/receipts/list/list.component.ts`

**Intent**: Track which row is being edited and drive save/cancel with optimistic update
and revert-on-error.

**Contract**: A signal identifying the row in edit mode (e.g. `editingId = signal<string | null>(null)`)
and a bound draft value (a `FormControl` or signal). Methods: `startEdit(receipt)`
(set editing id, seed draft, focus input), `cancelEdit()` (clear editing id, return
focus to pencil button), `saveEdit(receipt)`:
trim the draft; if empty/whitespace, exceeds ~255 chars, or equals the current name,
block (no API call — for unchanged, just exit edit mode); otherwise optimistically
`receipts.update` the row's `fileName`, call `renameReceipt`, and on error revert the
signal to the prior value and `snackBar.open(...)` (mirror `deleteReceipt`
error handling, `list.component.ts:171-174`). Track the subscription for cleanup in
`ngOnDestroy`. Add a `MAX_FILENAME_LENGTH` constant.

#### 3. List row template — pencil button, input, save/cancel controls

**File**: `src/frontend/src/app/receipts/list/list.component.html`

**Intent**: Render the editor inline, replacing the filename span when the row is in
edit mode.

**Contract**: In `receipt-row__primary` (`list.component.html:66-86`): when
`editingId() !== receipt.id`, render the existing filename span plus a new pencil
`mat-icon-button` (`aria-label="Rename {{ receipt.fileName }}"`, `edit` icon) beside the
delete button. When editing, render a `matInput` bound to the draft with
`(keydown.enter)`=save, `(keydown.escape)`=cancel, `(blur)`=save, plus explicit check
(`aria-label="Save"`) and close (`aria-label="Cancel"`) `mat-icon-button`s. Use native
control flow (`@if`), `class`/`style` bindings (no `ngClass`/`ngStyle`), and
`ChangeDetectionStrategy.OnPush` (already set). Import `ReactiveFormsModule` is present;
add any new Material modules to the component `imports` if needed.

#### 4. Component spec

**File**: `src/frontend/src/app/receipts/list/list.component.spec.ts`

**Intent**: Cover the inline-edit interactions.

**Contract**: Tests for: entering edit mode via the pencil button reveals the input;
Enter/check saves and calls `renameReceipt` with the trimmed value and updates the row;
Esc/cancel exits without calling the service and restores the original; empty/whitespace
draft blocks the save call; a service error reverts the row and shows the snackbar.
Mock `ReceiptService` per the existing spec's setup.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm run build` (in `src/frontend`)
- Lint passes: `npm run lint` (in `src/frontend`)
- Component tests pass: `npm test` (in `src/frontend`)
- New spec cases cover enter / save / cancel / validation-block / error-revert

#### Manual Verification:

- Pencil button enters edit mode; focus lands in the input
- Enter, blur, and the check button all save; Esc and the X button all cancel
- Saved name persists after a manual refresh (`GET /receipts`)
- Clearing the name and attempting to save is blocked (no request sent)
- Simulating a server error (e.g. offline) reverts the row and shows the snackbar
- AXE reports no violations in edit mode (labels, focus, contrast)

**Implementation Note**: After completing this phase and all automated verification
passes, pause for manual confirmation.

---

## Testing Strategy

### Unit / Component Tests:

- Backend (`ReceiptRenameTests`): 403 ownership (no merge side effect), 404 not-found,
  200/204 happy path (merge received once with only `FileName` set), 400 empty name.
- Frontend (`list.component.spec`): enter edit mode, save (Enter/check) updates row and
  calls service with trimmed value, cancel (Esc/X) restores original with no call,
  empty-name block, error-revert + snackbar.

### Integration Tests:

- Backend tests run against the real app via `ReceiptWellWebFactory` (offline boot,
  NSubstitute Azure clients) — the same harness `ReceiptDeleteTests` uses.

### Manual Testing Steps:

1. Rename a receipt; confirm the new name shows immediately and after a refresh.
2. Rename to a name with leading/trailing spaces; confirm it's trimmed.
3. Clear the name and try to save; confirm the save is blocked and no request fires.
4. Press Esc mid-edit; confirm the original name returns.
5. Go offline and save; confirm the row reverts and a snackbar appears.
6. Rename a receipt still in `pending`; confirm it works and AI fields later populate
   without clobbering the new name.
7. Tab through a row in edit mode; confirm focus order and labels (AXE clean).

## Performance Considerations

Negligible. One partial-merge write per rename; no blob I/O; optimistic UI avoids a
list refetch, preserving the active search query and pending-poll state.

## Migration Notes

None. No schema change (`FileName` already exists on `ReceiptDocument`), no data
migration, no backfill. Existing receipts are renamable immediately.

## References

- Frame brief: `context/changes/change-receipt-title/frame.md`
- Delete service template: `src/backend/ReceiptWell.Web/Services/ReceiptDeleteService.cs`
- Delete endpoint template: `src/backend/ReceiptWell.Web/Program.cs:220-245`
- Partial-merge write: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:106`
- Backend test template: `src/backend/ReceiptWell.Tests/ReceiptDeleteTests.cs`
- Frontend row-action template: `src/frontend/src/app/receipts/list/list.component.ts:162-176`

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles.

### Phase 1: Backend — Rename Service, PUT Endpoint & Tests

#### Automated

- [x] 1.1 Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- [x] 1.2 Backend tests pass: `dotnet test src/backend/ReceiptWell.sln`
- [x] 1.3 New `ReceiptRenameTests` cover 200, 404, 403, and 400 branches (all green)

#### Manual

- [ ] 1.4 `PUT /receipts/{id}` with a valid body succeeds; new name appears in `GET /receipts`
- [ ] 1.5 Renaming another user's receipt returns 403 with no change
- [ ] 1.6 An empty `fileName` returns 400
- [ ] 1.7 AI-extracted fields unchanged after the rename

### Phase 2: Frontend — Inline-Edit UI & Tests

#### Automated

- [ ] 2.1 Frontend builds: `npm run build`
- [ ] 2.2 Lint passes: `npm run lint`
- [ ] 2.3 Component tests pass: `npm test`
- [ ] 2.4 New spec cases cover enter / save / cancel / validation-block / error-revert

#### Manual

- [ ] 2.5 Pencil button enters edit mode; focus lands in the input
- [ ] 2.6 Enter, blur, and check all save; Esc and X all cancel
- [ ] 2.7 Saved name persists after a manual refresh
- [ ] 2.8 Clearing the name and saving is blocked (no request sent)
- [ ] 2.9 Simulated server error reverts the row and shows the snackbar
- [ ] 2.10 AXE reports no violations in edit mode
