# Inline-Editable Receipt Filename — Plan Brief

> Full plan: `context/changes/change-receipt-title/plan.md`
> Frame brief: `context/changes/change-receipt-title/frame.md`

## What & Why

Implement Update — the one missing CRUD operation — by letting a user rename the
existing `FileName` field in place, not by editing `storeName`/`purchaseDate`/`tags`,
and not by introducing a new separate `Title` field. This closes the MVP's
full-CRUD requirement with the lowest-effort, race-free change available.

## Starting Point

The backend has Create, Read, and Delete but no `MapPut`/`MapPatch`. `FileName` is set
once at confirm time, rendered as the always-present fallback identifier in every list
row, and never touched by the background AI extraction pipeline — so it can be edited
with no concurrency guard. The `receipt-delete` change provides a complete
service + endpoint + test + UI template to mirror.

## Desired End State

A user viewing their receipts can click a pencil button on any row, edit the filename
inline, and save (Enter / blur / check) or cancel (Esc / X). The row updates instantly
and reverts with a snackbar on failure. The backend exposes `PUT /receipts/{id}` that
renames only `FileName`, enforcing ownership, existence, and a non-empty name.

## Key Decisions Made

| Decision              | Choice                                                        | Why (1 sentence)                                                              | Source |
| --------------------- | ------------------------------------------------------------ | ---------------------------------------------------------------------------- | ------ |
| Field to edit         | Rename existing `FileName`                                    | Race-free (AI writer never touches it) and no schema change vs a new field.  | Frame  |
| Edit affordance       | Pencil edit button per row                                   | Discoverable, a11y-clean, consistent with the adjacent delete icon-button.   | Plan   |
| Save / cancel         | Enter/blur save, Esc cancel, plus explicit check/X buttons   | Standard inline-edit convention, fully keyboard + touch accessible.          | Plan   |
| Validation            | Trim, reject empty/whitespace, ~255 cap, skip-if-unchanged   | Protects the fallback identifier; server re-checks non-empty (400).          | Plan   |
| Update strategy        | Optimistic signal update from response, revert on error      | Instant feedback, mirrors delete's local update, avoids refetch flicker.     | Plan   |
| HTTP contract         | `PUT /receipts/{id}` with `{ fileName }`                     | Broadly-understood update verb, symmetric with the existing delete handler.  | Plan   |
| Test coverage         | Mirror delete: backend integration + frontend component      | Exercises every result branch and the new inline-edit UX edge cases.         | Plan   |

## Scope

**In scope:** `PUT /receipts/{id}` rename endpoint (Search-index-only merge), rename
service with ownership/existence/validation guards, DI + `.http` entry, backend
integration tests, frontend `renameReceipt` + inline-edit UI with keyboard/a11y support,
component tests.

**Out of scope:** new `Title` field; editing `storeName`/`purchaseDate`/`tags` (AI-race
gap); blob/content-disposition mutation; detail route or `GET /receipts/{id}`;
concurrency guards or edit locks; blocking edits during `pending`.

## Architecture / Approach

Mirror `receipt-delete` end to end. Backend: `ReceiptRenameService` (result union
`Success`/`NotFound`/`Forbidden`) fetches the doc for an ownership check, then writes
only `{ Id, FileName }` via `MergeOrUploadDocumentsAsync` (partial merge — other fields
untouched); a `PUT` handler maps results to 200/404/403 and rejects an empty name with
400. Frontend: a per-row edit-mode signal swaps the filename span for a Material input
with save/cancel controls; save optimistically updates the `receipts` signal and reverts
on error.

## Phases at a Glance

| Phase                                   | What it delivers                                              | Key risk                                                             |
| --------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------------- |
| 1. Backend — service, endpoint, tests   | `PUT /receipts/{id}` + rename service + `.http` + tests       | Partial merge must send only `Id`+`FileName` or it clobbers AI data |
| 2. Frontend — inline-edit UI + tests    | Pencil-button inline editor with optimistic update + tests    | Focus management / AXE compliance in edit mode                      |

**Prerequisites:** None — `receipt-delete` is shipped; `FileName` already exists on the
model. Local E2E needs `npm run start:local` if manually verified in-browser.
**Estimated effort:** ~2 sessions, one per phase.

## Open Risks & Assumptions

- The partial-merge write must not round-trip the full fetched document as an upload —
  doing so would overwrite AI-written fields with defaults. Plan calls this out
  explicitly.
- Success response shape (`200` with body vs `204 NoContent`) is left to the implementer
  to match the frontend's expectations; either works with the optimistic-update flow.
- Assumes no other consumer relies on the Search `FileName` matching the blob's
  content-disposition (confirmed in the frame — download uses the blob header, set at
  upload, independent of the Search field).

## Success Criteria (Summary)

- A user can rename any of their receipts inline and the change persists across refresh.
- Another user's receipt cannot be renamed (403); an empty name is rejected (400).
- `dotnet test` and `npm test` pass with new coverage for every result branch and the
  inline-edit interactions.
