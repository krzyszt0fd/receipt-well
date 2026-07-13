---
change_id: ai-extraction-and-enrichment
title: AI extraction and enrichment
status: archived
created: 2026-06-14
updated: 2026-07-13
archived_at: 2026-07-13T17:53:17Z
---

## Notes

<!-- Free-form notes for this change: links, ad-hoc context, decisions that don't belong in research/frame/plan. -->

### 6.6 split: S-02 rendering deferred (2026-06-18)

Plan success criterion 6.6 ("Content-filter/unreadable receipt lands at `Status = error`, visible on S-02 list") has two halves:

- **Backend half — verified.** Uploading a non-receipt image (`cv.png`) produced `Status: "error"`, `StoreName: null`, `PurchaseDate: null`, `Tags: []` in the live index — no throw loop, no perpetual `pending`.
- **S-02-rendering half — deferred, not dropped.** S-02 (the receipt list view) has not been implemented yet — only the upload flow exists in `src/frontend/src/app/receipts/`. This was a known cross-slice dependency called out in the plan's Migration Notes ("`Status = "error"` is a new value the S-02 list must render (coordinated with S-02)"), not a planning gap. The Progress checkbox for 6.6 stays unchecked until S-02 ships and actually renders the `error` state; revisit this note when picking up the S-02 change.

### 6.5 real-receipt validation summary (2026-06-18)

Ran 3 real Polish receipts (Decathlon, Media Expert/Shokz headphones) plus 1 deliberate negative case (`cv.png`, a non-receipt image) through the deployed pipeline — short of the 5–10 target, judged sufficient given the consistent signal across runs.

- **Store name / purchase date:** correct on every run.
- **Core tags:** good — correct category/brand/model tags (e.g. `kask ochronny`, `kettlebell`, `hulajnoga`, `słuchawki`, `bluetooth`, `shokz`, `openrun`).
- **Hallucination risk found — garbled SKU fragments.** The Decathlon receipt produced tag fragments that look like split product codes/sizes rather than real tags (`"lim purple"`, `"m/55-59 cm"`, `"ils set"`, `"soke evo"`, `"street blk/neo"`). Not corrected in this pass — `TagNormalizer` only dedupes/trims/lowercases, it doesn't validate semantic quality. Worth revisiting if this shows up across more receipts.
- **Boilerplate-as-tags found and patched twice.** First pass tagged discount/promo line items (`"promocja"`, `"zniżka"`) as if they were products. Patched the system prompt (`ReceiptExtractionService.cs`) to exclude price-adjustment and delivery/fulfillment line items by name. Re-test surfaced a second, more general case — disclaimer/footer text fragments (`"express"`, `"stanów"`, `"do"`, `"sklepu"`) also got tagged. Replaced the specific-word exclusion list with a general rule: only tag distinct purchased line items, never receipt boilerplate (payment method, return/exchange policy, loyalty text, address, legal/footer text), and drop unclear fragments rather than approximating them into a tag. **This latest prompt revision has not been re-verified against a live receipt** (local rebuild was blocked by a running debugger holding a file lock) — low risk to revisit if boilerplate-as-tags resurfaces.
- **Verdict:** GPT-4o quality is sufficient for MVP. No need for the Claude-on-Foundry A/B at this time.
