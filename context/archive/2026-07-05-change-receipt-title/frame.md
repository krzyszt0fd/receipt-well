# Frame Brief: Receipt Title Editing

> Framing step before /10x-plan. This document captures what is *actually*
> at issue, separated from what was initially assumed.

## Reported Observation

MVP requires a full CRUD flow for receipts. Today the backend has Create
(`POST /receipts/staging-slot` + `POST /receipts/confirm`), Read (`GET
/receipts`), and Delete (`DELETE /receipts/{id}`, shipped in `receipt-delete`).
No `MapPut`/`MapPatch` exists anywhere in `Program.cs` — Update is the one
CRUD operation still missing.

## Initial Framing (preserved)

- **User's stated cause**: the missing Update operation should be implemented
  as an inline-editable receipt **title**, defaulting to the uploaded file
  name.
- **User's proposed direction**: add title editing to close the CRUD gap.
- **Pre-dispatch narrowing**: user confirmed (a) this is the intended framing,
  (b) "title" would be a brand-new, display-only field distinct from any
  existing extracted field, and (c) "full CRUD" is a *written* requirement,
  not the user's own inference.

## Dimension Map

The Update gap could be closed at any of these dimensions:

1. **Field scope** — which field(s) become editable. ← initial framing lands
   here, proposing a new field
2. **Surface** — where editing happens (list-view inline vs. a new detail
   page)
3. **Identifier/fallback role** — what a receipt's editable display label
   defaults to when AI extraction is incomplete
4. **Data-model footprint** — how much new schema/endpoint surface the change
   requires

## Hypothesis Investigation

| Hypothesis | Evidence | Verdict |
| --- | --- | --- |
| The documented CRUD gap names *storeName/purchaseDate/tags* as the editable fields, not a new "title" | `context/changes/receipt-delete/plan.md:39` — "Update (editing store name/date/tags after upload) — a separate CRUD gap, not part of this change." Also `plan-brief.md:7` frames the gap generically as "a user has no way to [operate on] a receipt they uploaded" with no mention of title. | STRONG |
| "Title" is a genuinely new field with no precedent in the data model | `ReceiptDocument.cs:6-45` and `ReceiptSummary.cs:3-11` fields: `Id, UserId, BlobUrl, FileName, FileSize, Status, UploadedAt, StoreName, PurchaseDate, Tags, TagsPl`. No `Title` anywhere in backend or frontend except this change's own `change.md`. | STRONG |
| `FileName` already plays the "identifying label when AI data is missing" role the proposed title would duplicate | `ReceiptConfirmService.cs:98` sets `FileName = originalFileName` at confirm time (from the browser's `File.name`, `receipt.service.ts:33-38`); `list.component.html:76` renders `fileName` unconditionally for every row, while `storeName`/`purchaseDate` render only when present (`list.component.html:91,94`) — i.e. `FileName` is already the always-present fallback identifier. | STRONG |
| No detail/edit surface exists yet — editing must happen inline in the list, and no single-item `GET /receipts/{id}` exists either way | `app.routes.ts` registers only `receipts/upload` and `receipts` (list) — no `receipts/:id`. `Program.cs` has no `MapGet("/receipts/{id}")`. This constrains *surface* (dimension 2) but doesn't bear on *field scope* (dimension 1), so it doesn't favor either framing. | STRONG (neutral to the reframe) |

## Narrowing Signals

- User confirmed "title" is meant as a **new, display-only field**, not a
  rename of an existing one (rules out "title = product name" reading).
- User confirmed "full CRUD" is a **written requirement** — pointing directly
  at the one place it's actually written down: `receipt-delete/plan.md:39`,
  which names different fields than the current proposal.

## Cross-System Convention

The only other place in this codebase that closed a CRUD gap (`receipt-delete`)
did so by explicitly citing the technical review's field list and scoping
narrowly to it — it even carved Update out by name to avoid scope creep
(`plan.md:39`). The convention here is: CRUD-completeness work operates on
the fields the technical review already named, not on newly invented fields.
The current proposal breaks that convention by introducing `Title`, a field
the review never mentioned.

## Reframed Problem Statement (revised after discussion)

> **The actual problem to plan around is**: implement Update by letting a
> user rename the existing `FileName` field in place — not by editing
> `storeName`/`purchaseDate`/`tags`, and not by introducing a new, separate
> `Title` field.

The first-pass reframe above (edit `storeName`/`purchaseDate`/`tags`, per the
documented CRUD gap in `receipt-delete/plan.md:39`) missed a dimension: those
three fields are written asynchronously by the background AI extraction
pipeline. `ReceiptStore.SetReadyAsync` (`ReceiptStore.cs:25-50`) performs a
**partial merge** (`MergeOrUploadDocumentsAsync`) that writes only
`StoreName`, `PurchaseDate`, `Tags`, `TagsPl`, `Status` — the code comment at
`ReceiptStore.cs:72-73` explicitly confirms `FileName` (among others) is "left
untouched." If a user edited `storeName` while a receipt was still `Pending`,
a subsequent extraction completion would silently overwrite that edit with
the AI-derived (possibly null) value — a real silent-data-loss race that
would need its own design work (block edits until `Ready`, or track
per-field "user edited" flags) before storeName/date/tags could be safely
editable. That's not low-UI-effort, and it isn't scoped by this change.

`FileName` has zero exposure to that race (the background writer never
touches it) and has no other technical role — blob paths key off
`{userId}/{receiptId}`, not the name (`ReceiptConfirmService.cs:98`,
confirmed no other reader depends on its value). So the lowest-effort,
race-free way to give the user an Update operation is to make the existing
`FileName` field itself editable, rather than inventing a parallel `Title`
field that would just duplicate it under a second name.

## Confidence

**HIGH** — grounded in the background-writer's own partial-merge document
definition and its inline comment (`ReceiptStore.cs:72-73`), plus the
data-model/UI trace confirming `FileName` has no other technical role. The
race condition is not hypothetical — it's the direct, documented behavior of
`SetReadyAsync`'s merge semantics.

## What Changes for /10x-plan

The plan should scope Update as: a `PATCH`/`PUT /receipts/{id}` endpoint that
renames `FileName`, plus inline list-row editing (no detail route needed,
mirroring the delete pattern). No new field, no schema change, no
concurrency guard needed against the AI pipeline. Editing
`storeName`/`purchaseDate`/`tags` remains a real, separate, harder CRUD gap
(now with a documented race-condition prerequisite) that this change does
not attempt to close.

## References

- Source files:
  - `src/backend/ReceiptWell.Core/ReceiptDocument.cs:6-45`
  - `src/backend/ReceiptWell.Web/Models/ReceiptSummary.cs:3-11`
  - `src/backend/ReceiptWell.Web/Services/ReceiptConfirmService.cs:98`
  - `src/frontend/src/app/receipts/list/list.component.html:66-109`
  - `src/frontend/src/app/app.routes.ts:18-23`
  - `src/backend/ReceiptWell.Web/Program.cs:141,160,198,220`
- Related prior decisions:
  - `context/changes/receipt-delete/plan.md:5,39`
  - `context/changes/receipt-delete/plan-brief.md:7`
