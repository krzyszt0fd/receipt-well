# Receipt Delete — Plan Brief

> Full plan: `context/changes/receipt-delete/plan.md`

## What & Why

Add a user-initiated delete for receipts. This closes the CRUD gap flagged in the MVP technical review: Create and Read exist, but a user has no way to remove a receipt they uploaded. Deleting permanently removes both the Search index document and the Blob Storage image.

## Starting Point

Today the backend exposes `POST /receipts/staging-slot`, `POST /receipts/confirm`, and `GET /receipts` — no `MapDelete`/`MapPut` exists anywhere. The frontend list view (`list.component.html`) has no per-row action affordance. The codebase already has strong, reusable precedent for the two hard parts of this feature: ownership guarding (`ReceiptConfirmService`'s prefix check) and honest-5xx handling of non-atomic multi-store writes (`ReceiptConfirmFailureShapeTests`).

## Desired End State

A user can click delete on a receipt in their list, confirm in a dialog, and see it disappear immediately. The blob and Search document are both gone. Another user can never delete someone else's receipt (403). A receipt deleted while AI extraction is still processing it does not resurrect as an orphaned ghost row afterward.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) | Source |
|---|---|---|---|
| Delete semantics | Hard delete (blob + index doc removed) | The PRD's "photo never lost" guardrail protects against *accidental* loss, not deliberate user action; no evidence of a need for recovery/trash. | Plan (user Q&A) |
| Confirmation UX | Material dialog | Matches the project's existing Angular Material usage and accessibility (AXE/WCAG AA) conventions. | Plan (user Q&A) |
| Deleting a "pending" receipt | Allowed; extraction result is silently dropped | Simple, predictable UX; the resulting ghost-document risk is closed separately (Phase 2). | Plan (user Q&A) |
| Non-atomic delete ordering | Delete Search doc first, then blob; honest 500 if blob delete fails | The user-visible half of "delete" (disappearing from the list) succeeds even if a rare blob-delete failure needs manual reconciliation later. | Plan (user Q&A) |
| Scope | Single-receipt delete only | Matches every other endpoint's scope; no bulk-management need in the PRD. | Plan (user Q&A) |
| Response contract | 204 No Content | Standard REST convention; frontend already knows the id it asked to delete. | Plan (user Q&A) |
| Risk tracking | Add Risk #8 to `test-plan.md` | Consistent with how every other IDOR/honest-5xx surface in this project is tracked. | Plan (user Q&A) |
| Test scope | Backend ownership + failure-shape + frontend component spec | Matches this repo's existing non-negotiable bar for every prior endpoint. | Plan (user Q&A) |

## Scope

**In scope:**
- `DELETE /receipts/{id}` endpoint with ownership guard and honest-5xx handling
- Ghost-document prevention in the AI extraction pipeline (`ReceiptStore`)
- Frontend confirmation dialog + delete button + list-state update
- `test-plan.md` Risk #8 entry

**Out of scope:**
- Soft delete / trash / restore
- Bulk delete
- Update (editing receipt metadata) — a separate gap
- Blocking delete of "pending" receipts
- An orphaned-blob cleanup job
- A full `ReceiptWell.Functions` test harness
- Adding this change to `roadmap.md`

## Architecture / Approach

The delete endpoint fetches the Search document first (its `UserId` field is the only way to check ownership, since the route only carries a receipt id), then deletes the Search document, then the blob — mirroring the existing confirm flow's non-atomic-write-with-honest-failure pattern. The extraction pipeline gets a narrow existence-check guard before every merge write, closing the specific race where a completed extraction resurrects a just-deleted receipt as an orphan document.

## Phases at a Glance

| Phase | What it delivers | Key risk |
|---|---|---|
| 1. Backend delete endpoint | `DELETE /receipts/{id}` with ownership + honest-5xx tests | Getting the ownership check right without an existing prefix-based shortcut |
| 2. Ghost-document prevention | Existence guard in `ReceiptStore` before merge writes | Race window is narrowed, not eliminated (accepted residual risk) |
| 3. Frontend delete UI | Confirm dialog + list wiring + snackbar error handling | First `MatDialog` usage in this codebase — no existing pattern to copy exactly |
| 4. Documentation | `test-plan.md` Risk #8 entry + phase note | None — low-risk documentation update |

**Prerequisites:** None — this is a self-contained addition to the existing receipt CRUD surface; no other in-flight change touches these files.
**Estimated effort:** ~1 session across 4 phases (small, well-precedented surface area).

## Open Risks & Assumptions

- The ghost-document race is narrowed, not eliminated — a delete landing in the exact window between the existence check and the merge call in `ReceiptStore` is still theoretically possible. Accepted as a rare, low-impact residual risk (orphan document, invisible to all users) rather than engineered away.
- A blob-delete failure after a successful Search-document delete leaves an orphaned blob with no automated cleanup — surfaced via an Error log for manual reconciliation, not a self-healing system.

## Success Criteria (Summary)

- A user can delete their own receipt and it disappears from their list and underlying storage.
- A user cannot delete another user's receipt.
- Deleting a receipt mid-extraction never leaves behind a ghost document.
