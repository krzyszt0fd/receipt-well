# Original Receipt Download — Plan Brief

> Full plan: `context/changes/receipt-original-download/plan.md`
> Frame brief: `context/changes/receipt-original-download/frame.md`

## What & Why

Add a per-row **download** action to the receipt list so the owner can retrieve the
original receipt image. This is the one moment the image matters most — a
return/warranty claim, the PRD's core problem — where today the proof of purchase is
unreachable from the list.

## Starting Point

Confirmed receipts already live as private blobs at `{userId}/{receiptId}`, but no
read path exists: the container is private, `BlobUrl` is never exposed, and the only
SAS the frontend gets is a write-only staging slot. The row shows status, filename,
date, store, purchase date, and tags — but no way to open the file.

## Desired End State

Every receipt row shows a download icon beside rename/delete. Clicking it downloads
the original image (named with the receipt's *current* file name) via a short-lived
read-SAS URL. Non-owners, missing receipts, and unauthenticated callers are rejected;
failures show a snackbar consistent with existing error UX.

## Key Decisions Made

| Decision            | Choice                                | Why (1 sentence)                                                                 | Source   |
| ------------------- | ------------------------------------- | -------------------------------------------------------------------------------- | -------- |
| Need to serve       | Retrieve original, not thumbnail      | Primary need is proof of purchase at claim time, not glance identification.       | Frame    |
| Read mechanism      | Short-lived read SAS                  | Mirrors existing `GenerateReadSasAsync`; no image bytes flow through the API.      | Plan     |
| Click behavior      | Download the file                     | Matches the user's proposal and the reklamacja/zwrot need; blob is `attachment`.  | Plan     |
| Availability        | Icon on every row, error on failure   | Blob exists for every confirmed receipt (incl. `pending`); matches error UX.       | Plan     |
| Filename on rename  | Override SAS `Content-Disposition`    | Blob keeps the *original* name; override honors the user's later rename.           | Plan     |
| Test coverage       | Backend integration + E2E click flow  | Covers the security-critical scoping and the full user path.                       | Plan     |
| FR-006 / S-05       | Dropped (superseded)                  | Thumbnail serves a non-primary need the row already largely covers.                | Frame    |

## Scope

**In scope:** new read-SAS endpoint + service, download button on each row, service
method, backend integration tests, one E2E test, `.http` example.

**Out of scope:** inline thumbnail (FR-006), in-app preview, streaming proxy,
exposing raw `BlobUrl`, any change to upload/confirm/extraction/search/delete/rename.

## Architecture / Approach

Thin vertical slice. Backend: `ReceiptDownloadService` reuses the delete/rename
lookup+ownership flow and the confirm-service read-SAS builder (plus a
`Content-Disposition` override), surfaced as `GET /receipts/{id}/download-url` →
`{ downloadUri }`. Frontend: `getDownloadUrl(id)` + a download button that navigates
to the URI; the blob's `attachment` disposition makes the browser download. The image
bytes flow browser↔Azure directly — never through the API.

## Phases at a Glance

| Phase                          | What it delivers                                   | Key risk                                            |
| ------------------------------ | -------------------------------------------------- | --------------------------------------------------- |
| 1. Backend read-SAS endpoint   | `GET /receipts/{id}/download-url` + ownership + tests | SAS/delegation-key fallback + filename override      |
| 2. Frontend download action    | Download button + service method + error snackbar   | Accessibility (AXE/WCAG AA); download trigger        |
| 3. E2E click-flow test         | Playwright test asserting the download event        | Download-event assertion flakiness                  |

**Prerequisites:** S-04 (done). Local: Azurite + `start:local` + auth `storageState`
for E2E.
**Estimated effort:** ~1 session across 3 phases (each phase small; pattern reuse is high).

## Open Risks & Assumptions

- Assumes the receipts blob's copied `Content-Disposition` is `attachment` (set at
  staging upload, copied at confirm) — verify during Phase 1; the SAS override makes
  the filename correct regardless.
- Playwright download-event assertions can be finicky; the plan stubs the API for
  determinism and waits on `page.waitForEvent('download')`.

## Success Criteria (Summary)

- An owner can download any of their receipts' original images from the list.
- The downloaded file uses the receipt's current (possibly renamed) file name.
- Non-owners / unauthenticated callers cannot obtain a download URL (403/401).
