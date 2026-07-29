# Original Receipt Download Implementation Plan

## Overview

Add a per-row **download action** to the receipt list so an owner can retrieve the
original receipt image — the proof of purchase needed at a return/warranty claim
(the PRD's core problem). Clicking a download icon (beside rename/delete) fetches a
short-lived, owner-scoped **read SAS** URL from a new backend endpoint and triggers
a browser download of the original file.

This supersedes roadmap slice **S-05** (`receipt-thumbnail-in-search`) and
deliberately leaves **FR-006** (glance-level visual identification via thumbnail)
unaddressed — see `frame.md` for the framing rationale (Confidence: HIGH).

## Current State Analysis

- **No read path to the original blob exists.** `ReceiptDocument.BlobUrl`
  (`ReceiptDocument.cs:14-15`) points into a **private** container; the raw URL 403s
  without a SAS. `ReceiptSummary` (`ReceiptSummary.cs`) never exposes it. The only
  SAS the frontend ever gets is a **write-only** staging slot
  (`ReceiptBlobService.CreateStagingSlotAsync`, `ReceiptBlobService.cs:16-48`).
- **A read-SAS generator already exists and is the template.**
  `ReceiptConfirmService.GenerateReadSasAsync` (`ReceiptConfirmService.cs:157-175`)
  builds a 5-minute read SAS with `BlobSasPermissions.Read`, including the
  `CanGenerateSasUri` → delegation-key fallback (`DelegationTokenProvider`). The new
  endpoint mirrors this against the receipts container.
- **Ownership + lookup pattern is settled.** `ReceiptDeleteService.cs:24-42`:
  `GetDocumentAsync<ReceiptDocument>(receiptId)` → 404 ⇒ `NotFound`;
  `document.UserId != userId` ⇒ `Forbidden`. Blob name is deterministic:
  `{userId}/{receiptId}` in `AzureStorage:ReceiptsContainerName`.
- **The blob already carries `Content-Disposition: attachment; filename`.** Staging
  upload sets it (`receipt.service.ts:54`); `SyncCopyFromUriAsync`
  (`ReceiptConfirmService.cs:90`) copies blob properties to the receipts container.
  So a plain read SAS already downloads with a filename — but that filename is the
  **original upload name**, not any later rename (rename updates only the search-doc
  `FileName`, never the blob).
- **Blob content-type is validated to png/jpeg/webp at confirm**
  (`ReceiptConfirmService.cs:26-27,59-63`) — the served file is always a known image.
- **Endpoints are minimal-API in `Program.cs`**; services are `AddScoped`
  (`Program.cs:100-104`). Frontend is signals-based standalone Angular; all HTTP
  lives in `receipt.service.ts`; row action buttons are at
  `list.component.html:104-121`.

## Desired End State

An owner viewing their receipt list sees a **download icon** on every row. Clicking
it downloads the original image file (named with the receipt's **current** file
name) via a short-lived read SAS. Non-owners, missing receipts, and unauthenticated
callers are rejected by the backend (403/404/401). A failed download surfaces a
snackbar, consistent with the existing delete/rename error UX.

Verify: log in, open the list, click download on a row → the original image
downloads with the current file name; the API never streams image bytes (URL points
directly at Azure Blob storage with a SAS query string).

### Key Discoveries:

- Read-SAS template to mirror: `ReceiptConfirmService.cs:157-175`.
- Ownership/lookup template to mirror: `ReceiptDeleteService.cs:24-42`.
- Deterministic blob name: `{userId}/{receiptId}` in `ReceiptsContainerName`
  (`ReceiptConfirmService.cs:32-33,85`).
- Blob already has `attachment` Content-Disposition, but with the **original**
  filename — override via SAS `ContentDisposition` to honor renames.
- E2E exemplar + `page.route` determinism: `src/e2e-tests/tests/seed.spec.ts`.

## What We're NOT Doing

- **No inline thumbnail** (FR-006) — intentionally dropped per the frame.
- **No in-app/open-in-tab preview** — the action is download-only.
- **No streaming proxy** through the API — bytes go browser↔Azure via SAS.
- **No exposure of raw `BlobUrl`** on `ReceiptSummary`.
- **No change to upload, confirm, extraction, search, delete, or rename** behavior.

## Implementation Approach

A thin vertical slice mirroring existing patterns: (1) a new scoped
`ReceiptDownloadService` that looks up the doc, checks ownership, and returns a
short-lived read-SAS URI with an overridden `Content-Disposition`; a `GET
/receipts/{id}/download-url` endpoint returning `{ downloadUri }`; (2) a
`getDownloadUrl(id)` service method and a download button on each row that navigates
to the URI to trigger the download; (3) an E2E test driving the click.

## Critical Implementation Details

**Filename correctness on rename.** The receipts blob's stored `Content-Disposition`
carries the *original* upload filename; `PUT /receipts/{id}` updates only the
search-doc `FileName`. To download with the user's current name, set
`BlobSasBuilder.ContentDisposition` to `attachment; filename*=UTF-8''<encoded
current FileName>` when generating the SAS (SAS response-header override). Use the
`FileName` from the fetched `ReceiptDocument`. Mirror the RFC 5987 encoding already
used at `receipt.service.ts:54`.

## Phase 1: Backend read-SAS download endpoint

### Overview

Add an owner-scoped endpoint that returns a short-lived read-SAS URL for the
caller's receipt image.

### Changes Required:

#### 1. Download service

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptDownloadService.cs` (new)

**Intent**: Look up the receipt by id, enforce ownership, and return a short-lived
read-SAS URI for `{userId}/{receiptId}` in the receipts container, with a
`Content-Disposition` override carrying the current file name. Return a discriminated
result mirroring `ReceiptDeleteResult`.

**Contract**: `Task<ReceiptDownloadResult> GetDownloadUrlAsync(string receiptId,
string userId)` where `ReceiptDownloadResult` is an abstract record with
`Success(Uri DownloadUri)`, `NotFound()`, `Forbidden()`. Reuse the lookup/ownership
flow from `ReceiptDeleteService.cs:24-42` and the SAS-build + delegation-key fallback
from `ReceiptConfirmService.cs:157-175`, adding `sasBuilder.ContentDisposition`. Uses
`BlobServiceClient`, `DelegationTokenProvider`, `IConfiguration`
(`AzureStorage:ReceiptsContainerName`), source-generated logging (log userId +
receiptId; ownership violation at Warning per backend CLAUDE.md).

#### 2. Endpoint + DI registration

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Register the service and map the endpoint following the delete/rename
shape (result → `Results.Ok`/`Forbid`/`NotFound`, catch-all → `Results.Problem(500)`).

**Contract**: `builder.Services.AddScoped<ReceiptDownloadService>();` (near
`Program.cs:100-104`). `app.MapGet("/receipts/{id}/download-url", ...)` returning
`Results.Ok(new { downloadUri = uri.ToString() })` on success. Auth is enforced by
the existing `FallbackPolicy` (requires authenticated user + `oid`).

#### 3. Example request collection

**File**: `receipt-well.http`

**Intent**: Add an example `GET /receipts/{{receiptId}}/download-url` request, per
the backend CLAUDE.md rule to keep the collection current.

**Contract**: New named request block using the existing `{{hostAddress}}`,
`{{receiptId}}`, `{{token}}` variables, placed after the rename/delete blocks.

#### 4. Integration tests

**File**: `src/backend/ReceiptWell.Tests/ReceiptDownloadTests.cs` (new)

**Intent**: Cover the security-critical scoping on the new endpoint using the real
host (`ReceiptWellWebFactory` + `TestAuthHandler`).

**Contract**: Tests — owner receives a 200 with a non-empty `downloadUri` containing
a SAS query string; non-owner (doc `UserId` ≠ caller `oid`) → 403; unknown id → 404;
no `oid` header → 401. Follow `ReceiptDeleteTests.cs` and the ownership-test
conventions (NSubstitute `SearchClient`/`BlobServiceClient` from the factory;
requirement-derived assertions, not exact-string filter matches).

### Success Criteria:

#### Automated Verification:

- Build is warning-free: `dotnet build src/backend/ReceiptWell.sln`
- Tests pass: `dotnet test src/backend/ReceiptWell.sln`

#### Manual Verification:

- `GET /receipts/{id}/download-url` (via `receipt-well.http`) for an owned receipt
  returns a URL that downloads the original image with the current file name.
- The same request for another user's receipt id returns 403; an unknown id 404.

**Implementation Note**: After completing this phase and all automated verification
passes, pause here for manual confirmation before proceeding.

---

## Phase 2: Frontend download action

### Overview

Add a download icon to each receipt row that fetches the read-SAS URL and triggers
the browser download.

### Changes Required:

#### 1. Service method

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Add a method to fetch the download URL for a receipt.

**Contract**: `getDownloadUrl(id: string): Observable<{ downloadUri: string }>` →
`GET ${apiUrl}/receipts/${id}/download-url`. Follows the existing method style.

#### 2. Download button + handler

**File**: `src/frontend/src/app/receipts/list/list.component.html` and
`list.component.ts`

**Intent**: Add a download `mat-icon-button` beside the rename/delete buttons
(`list.component.html:104-121`, non-editing branch) with an accessible label. On
click, call `getDownloadUrl` and navigate to the returned URI to trigger the
download; on error, show a snackbar matching the delete/rename pattern
(`list.component.ts:200-213`).

**Contract**: New handler `downloadReceipt(receipt: ReceiptSummary): void` that
subscribes to `getDownloadUrl(receipt.id)`, and on success triggers the download by
assigning the URI to `window.location.href` (the blob's `attachment`
Content-Disposition makes the browser download rather than navigate). Manage the
subscription like the existing `deleteSubscription` and unsubscribe in `ngOnDestroy`.
Button: `aria-label="Download {{ receipt.fileName }}"`, `mat-icon` `download`,
`aria-hidden` on the icon — passes AXE/WCAG AA per frontend CLAUDE.md.

**Addendum (during manual verification)**: `window.location.href` navigation was
found to replace the whole SPA with the browser's native error page when the blob
URL is unreachable (expired SAS, deleted blob, storage outage) — there's no response
to intercept via Content-Disposition when the request never completes. Adapted to
open a hidden `<a target="_blank" rel="noopener">` anchor instead
(`triggerDownload()` in `list.component.ts`), confining that failure to a new tab.
Also added, at the user's request: a `.receipt-row__actions` wrapper span around the
rename/download/delete (and save/cancel) buttons, with a scoped `!important` CSS
override tightening their box size — Material's touch-target overlay stays at 48px
independent of the visual box, so accessibility is unaffected.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm run build` (in `src/frontend`)
- Lint passes: `npm run lint` (in `src/frontend`)

#### Manual Verification:

- Each row shows an accessible download icon beside rename/delete.
- Clicking it downloads the original image with the current file name.
- Renaming a receipt then downloading yields a file named with the new name.
- A simulated backend failure shows the error snackbar; the list is unaffected.
- Keyboard focus reaches the button and the label is announced by a screen reader.

**Implementation Note**: After completing this phase and all automated verification
passes, pause here for manual confirmation before proceeding.

---

## Phase 3: E2E click-flow test

### Overview

A Playwright test that drives the download button end-to-end and asserts a browser
download occurs.

### Changes Required:

#### 1. Download spec

**File**: `src/e2e-tests/tests/receipt-download.spec.ts` (new)

**Intent**: Verify the row's download action requests the URL endpoint and produces a
browser download, using route stubbing for determinism (no real Azure).

**Contract**: Following `seed.spec.ts` — stub `GET /receipts*` with one `ready`
receipt, stub `GET /receipts/*/download-url` to return `{ downloadUri }` pointing at
a routed data/download URL, locate the button via `getByRole('button', { name:
/download/i })`, click, and assert via `page.waitForEvent('download')` (state, never
`waitForTimeout`). Unique ids with a timestamp suffix; standalone setup + cleanup per
`src/e2e-tests/CLAUDE.md`. Auth via existing `storageState`.

### Success Criteria:

#### Automated Verification:

- Spec passes: `npx playwright test tests/receipt-download.spec.ts --project=chrome --no-deps` (in `src/e2e-tests`)

#### Manual Verification:

- The spec reliably passes across repeated runs (no flake) with the app started via
  `start:local`.

**Implementation Note**: After completing this phase and all automated verification
passes, pause for final manual confirmation.

---

## Testing Strategy

### Unit / Integration Tests:

- Backend integration (Phase 1): 200 owner (URL has SAS), 403 non-owner, 404 unknown,
  401 unauthenticated — the auth-policy boundary only the real host exercises.

### End-to-end Tests:

- Playwright (Phase 3): download button click → `download` event, with routed API
  responses for determinism.

### Manual Testing Steps:

1. Log in, open the receipt list, click download on a `ready` receipt → original
   image downloads.
2. Rename a receipt, then download → file uses the new name.
3. Download a `pending` receipt (blob exists pre-extraction) → still downloads.
4. Attempt `download-url` for a foreign receipt id via `receipt-well.http` → 403.

## Performance Considerations

Image bytes never traverse the API — the browser fetches the blob directly from
Azure via SAS, so App Service bandwidth/latency is unaffected. SAS TTL is short
(≈5 min) to bound the bearer-capability window.

## Migration Notes

None. No schema, index, or data changes. All confirmed receipts already have a blob
at `{userId}/{receiptId}` and a stored `Content-Disposition`.

## References

- Frame brief: `context/changes/receipt-original-download/frame.md`
- Read-SAS template: `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:157-175`
- Ownership/lookup template: `src/backend/ReceiptWell.Web/Services/ReceiptDeleteService.cs:24-42`
- E2E exemplar: `src/e2e-tests/tests/seed.spec.ts`
- Roadmap: `context/foundation/roadmap.md` (S-06)

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend read-SAS download endpoint

#### Automated

- [x] 1.1 Build is warning-free: `dotnet build src/backend/ReceiptWell.sln`
- [x] 1.2 Tests pass: `dotnet test src/backend/ReceiptWell.sln`

#### Manual

- [x] 1.3 Owned-receipt request returns a URL that downloads the original with the current file name
- [x] 1.4 Foreign receipt id → 403; unknown id → 404

### Phase 2: Frontend download action

#### Automated

- [x] 2.1 Frontend builds: `npm run build` — 2653f9d
- [x] 2.2 Lint passes: `npm run lint` — 2653f9d

#### Manual

- [x] 2.3 Each row shows an accessible download icon beside rename/delete — 2653f9d
- [x] 2.4 Clicking downloads the original image with the current file name — 2653f9d
- [x] 2.5 Rename then download yields a file named with the new name — 2653f9d
- [x] 2.6 Simulated backend failure shows the error snackbar; list unaffected — 2653f9d
- [x] 2.7 Button is keyboard-reachable and its label is announced — 2653f9d

### Phase 3: E2E click-flow test

#### Automated

- [x] 3.1 Spec passes: `npx playwright test tests/receipt-download.spec.ts --project=chrome --no-deps`

#### Manual

- [x] 3.2 Spec reliably passes across repeated runs (no flake)
