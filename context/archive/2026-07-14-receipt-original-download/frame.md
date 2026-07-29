# Frame Brief: Original-image access from the receipt list (reframes S-05 thumbnail)

> Framing step before /10x-plan. This document captures what is *actually*
> at issue, separated from what was initially assumed.

## Reported Observation

Roadmap slice **S-05** (`receipt-thumbnail-in-search`, PRD **FR-006**) states a
receipt-photo thumbnail should be visible next to each row's metadata in the
list/search view.

## Initial Framing (preserved)

- **User's stated cause or approach**: A receipt row already carries a lot of
  readable information (status, filename, date, store, purchase date, tags). A
  thumbnail would be too small to read anything on a receipt — it only consumes
  space without adding usable information.
- **User's proposed direction**: Drop the thumbnail. Add a **download icon**
  next to the rename/delete actions that triggers a download of the original
  file.
- **Pre-dispatch narrowing**: When reaching for the image, the primary need is
  **retrieving the original file** (proof of purchase for a return/warranty),
  and it matters most on **rows the user is actively acting on** — not glance
  identification across all rows.

## Dimension Map

The observation could originate at any of these dimensions (each is a candidate
place where FR-006's framing might break):

1. **Is the underlying need identification or retrieval?** — FR-006 assumes
   *visual identification without opening details*. If the real need is
   *retrieving the file for a claim*, a thumbnail is the wrong tool.  ← reframe
2. **Does the row already satisfy identification?** — the initial framing lands
   here: rows are information-rich, so a thumbnail is redundant.
3. **Presentation form** (thumbnail vs. tap-to-preview vs. download) — a
   *solution* axis, out of scope for framing; all three are downstream of the
   need in (1).
4. **Backend capability** — whatever the form, is there a read path to the
   owner's original blob? (Shared prerequisite for every option.)

## Hypothesis Investigation

| Hypothesis | Evidence | Verdict |
| --- | --- | --- |
| (1) Real need is **retrieval for a claim**, not glance-identification | PRD Vision names the core problem as reklamacja/zwrot with no proof of purchase (`prd.md:20`). User narrowing answers = "Retrieve the original file" + "Rows I'm acting on". | STRONG |
| (2) Row already carries enough to identify a receipt (initial framing) | Row renders status, filename, date, store, purchaseDate, up to 5 tags + delete/rename (`list.component.html:65-146`). BUT the poor-extraction fallback row shows only filename + date — no store/date/tags — which is exactly FR-006's stated case (`prd.md:82-83`). | PARTIAL |
| (3) Inline thumbnail is the right form | User rates identification-at-a-glance as the non-primary need; thumbnail readability objection is sound for physical receipts. | WEAK |
| (4) Backend can serve the original blob today | `ReceiptDocument.BlobUrl` is stored in a private container (`ReceiptDocument.cs:14-15`), but `ReceiptSummary` never exposes it (`receipt.service.ts:6-15`) and the only SAS is a **write-only** staging slot (`ReceiptBlobService.cs:16-48`). No read endpoint exists. | NONE (must be built) |

## Narrowing Signals

- **Primary need = "Retrieve the original file"** → rules out identification as
  the driver, which is the entire justification for a thumbnail (FR-006).
- **When it matters = "Rows I'm acting on"** → on-demand, per-row, at claim time
  — a click-to-act affordance (the download icon), not always-on visual chrome.
- **Poor-extraction rows are info-poor** → the one case where glance-value would
  be highest is also the case a tiny thumbnail serves worst; a
  view/download of the full original serves it better.

## Cross-System Convention

Every candidate form (thumbnail, in-app preview, download) needs the *same*
missing backend piece: an owner-scoped read path to the original blob (a
short-lived read SAS or a streaming `GET /receipts/{id}/image`). The existing
staging-slot pattern (`ReceiptBlobService.CreateStagingSlotAsync`) is the
natural template — mirror it with `BlobSasPermissions.Read` against the
receipts container. So the reframe does not increase backend cost versus the
thumbnail; both are gated on the identical prerequisite.

## Reframed (or Confirmed) Problem Statement

> **The actual problem to plan around is**: the owner has no way to get the
> original receipt image out of the list — the one moment it matters most (a
> return/warranty claim, the PRD's core problem), the proof of purchase is
> unreachable.

FR-006's thumbnail addresses a *different, secondary* need (glance-level visual
identification) that the user rates as non-primary and that the row's existing
metadata already largely covers. Substituting a per-row action that surfaces the
**full original** — download, as the user proposed, and/or open-in-tab preview —
serves the primary retrieval need and aligns with the PRD's north-star problem.
The initial framing (thumbnail is low-value) held up; the reframe extends it by
naming what the real need is.

## Confidence

**HIGH** — the narrowing answers were decisive and point the same way as the
PRD's stated core problem and the code reality (row already info-rich; no
thumbnail-serving backend either way). The one product cost to acknowledge:
this **drops FR-006** (a real nice-to-have PRD requirement) rather than
implementing it. That is a deliberate, recorded product decision, defensible
because the need FR-006 targets was rated non-primary — not an oversight.

## What Changes for /10x-plan

Plan a per-row **"download / view original"** action (icon beside rename/delete),
backed by a new owner-scoped read path to the receipt blob — **not** an inline
thumbnail. Decide download-vs-open-in-tab as a solution detail during planning;
both ride the same new backend read-SAS/stream endpoint. Note in the plan that
this supersedes roadmap S-05 and leaves FR-006 (visual identification)
intentionally unaddressed.

## References

- Source files: `list.component.html:65-146`, `receipt.service.ts:6-15`,
  `ReceiptBlobService.cs:16-48`, `ReceiptDocument.cs:14-15`,
  `prd.md:20`, `prd.md:34-36`, `prd.md:82-83`, `roadmap.md:127-137`
- Related research: none (no prior research doc for this slice)
- Investigation tasks: none dispatched — surface was small and fully read
  directly (skill guardrail #6/#7: no agent padding, time-boxed)
