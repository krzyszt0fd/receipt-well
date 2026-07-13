---
date: 2026-07-13T20:21:31+02:00
researcher: Krzysztof Dudzik
git_commit: 70ca6ebbc3601db2f1390b7a5851ad3363c395a4
branch: develop
repository: receipt-well
topic: "How can we combine linguistic (lemmatized) and prefix (as-you-type) tag search for best usability?"
tags: [research, codebase, tag-search, azure-ai-search, analyzers, combined-search]
status: complete
last_updated: 2026-07-13
last_updated_by: Krzysztof Dudzik
---

# Research: Combining linguistic and prefix tag search

**Date**: 2026-07-13T20:21:31+02:00
**Researcher**: Krzysztof Dudzik
**Git Commit**: 70ca6ebbc3601db2f1390b7a5851ad3363c395a4
**Branch**: develop
**Repository**: receipt-well

## Research Question

The linguistic match introduced in `tag-search` overrode the prefix search. We want both:
linguistic match so "rower" still matches "rowery", **and** prefix search so results appear
as the user types. How can we combine both methods for best usability?

## Summary

The two behaviors were never combinable on a *single* Azure AI Search query against a *single*
field, and that is exactly why one replaced the other. The constraint (recorded in
`lessons.md`):

- **Linguistic match** needs a plain, analyzed query (`QueryType.Simple`, `searchText = term`)
  so the `pl.microsoft` analyzer lemmatizes both index and query tokens ("rowery" → "rower").
- **Prefix match** needs a Lucene wildcard (`QueryType.Full`, `term + "*"`), which **bypasses
  the analyzer** — the token is matched literally, so it is prefix-only with no lemmatization.

They are mutually exclusive *per clause*, not per query. The clean way to have both is to run
**two clauses against two different fields and union the hits**:

| Behavior | Field to query | Why that field |
|---|---|---|
| Prefix / as-you-type | `Tags` (standard analyzer, non-lemmatized) | Indexed tokens are the raw surface forms, so `rowe*` prefixes match `rower` **and** `rowery`. |
| Whole-word inflection | `TagsPl` (`pl.microsoft`, lemmatized) | Analyzed query lemmatizes `rowery` → `rower` and hits the stored lemma. |

Both fields **already exist on the index** — `Tags` is the original display field, `TagsPl` was
added by `tag-search`. No schema change, no index rebuild, no backfill is required beyond what
already ran. The change is essentially **one method** — `ReceiptQueryService.GetReceiptsAsync` —
plus its tests. The frontend already debounces and re-queries and needs no mechanism change.

**Recommended approach:** a single `QueryType.Full` query combining a prefix clause on `Tags`
with an analyzed clause on `TagsPl` (one round trip, one relevance-ranked result set, term
boosting available). A two-call "query each, merge in the service" variant is the simpler,
escaping-free fallback. Both are detailed below.

## Detailed Findings

### The current (post-`tag-search`) behavior — lemma only, no prefix

`ReceiptQueryService.GetReceiptsAsync` is the whole search surface
(`src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:9-50`):

- Browse (blank `q`): `searchText = "*"`, `OrderBy UploadedAt desc`, no `SearchFields`.
- Search (non-blank `q`): `searchText = term`, `SearchFields = { "TagsPl" }`, ordering dropped so
  relevance ranks (`ReceiptQueryService.cs:21-31`).

It queries **only `TagsPl`** with a plain analyzed term → lemmatization works, prefix does not.
That is the regression the change note describes: the intermediate `tag-search` build used a
`term + "*"` wildcard (prefix, no lemma); the impl-review swapped it for the plain analyzed term
(lemma, no prefix). The user experienced this as "prefix search was overridden."

### Why the two can't share one clause (the core constraint)

Documented twice, from the same incident:

- `lessons.md:54-59` — "Azure AI Search: wildcard queries bypass the field's language analyzer."
  `QueryType.Full` + `*` matches the query token literally as a prefix against pre-analyzed index
  tokens; `pl.microsoft` never runs, so `rowery*` never reaches the indexed lemma `rower`.
- `context/archive/2026-06-24-tag-search/reviews/impl-review.md:23-30` (finding **F1, CRITICAL**)
  — the original bug report ("only exact match") traced to exactly this; the fix removed the
  wildcard and `QueryType.Full`.

Key nuance that makes combining possible: in Lucene **full** syntax, only wildcard/fuzzy/regex/
prefix terms skip analysis. A **plain** term in the same `QueryType.Full` query **is** analyzed.
So one Full query can carry an analyzed clause *and* a wildcard clause side by side.

### Why prefix belongs on `Tags`, not `TagsPl`

`TagsPl` is lemmatized at index time — its stored token for a "rowery" tag is `rower`. A prefix
`rowery*` would therefore **miss** it (the stored token is shorter than the typed prefix).
`Tags` uses the default/standard analyzer (`ReceiptDocument.cs:35`, `[SearchableField(IsFilterable = true)]`)
— it lowercases and tokenizes but does **not** stem, so the stored token is the raw surface form.
Prefixing against `Tags` gives true as-you-type:

- Type `rowe` → `Tags:rowe*` matches both `rower` and `rowery`.
- Type `rowery` (a full inflected word) → `TagsPl:rowery` lemmatizes to `rower` and matches a
  `rower`-tagged receipt that prefix alone would miss (e.g. `myszy` → lemma `mysz`, where
  `myszy*` would not match the stored `mysz`).

The two fields are complementary; neither alone covers both cases.

### Both fields are already populated — no migration

- `Tags`: written at confirm and enrichment time; the display source read into `ReceiptSummary`
  (`ReceiptQueryService.cs:37-45`).
- `TagsPl`: added additively by `tag-search`, populated on enrichment via
  `ReceiptStore.SetReadyAsync`, and backfilled once (`ReceiptWell.Tests/BackfillTagsPl.cs` — a
  skipped throwaway runner). Both fields stay in sync from the same normalized tag list.

So a combined query reads two fields that are already there. `ReceiptDocument.cs:38-44` documents
the design intent explicitly ("Polish-stemmed mirror of `Tags` … Added as a NEW field so the
existing `Tags` analyzer is never mutated").

### Frontend already supports the pattern — no mechanism change

`ReceiptListComponent` (`src/frontend/src/app/receipts/list/list.component.ts`) already:

- Debounces the search box 300ms and re-queries on change (`SEARCH_DEBOUNCE_MS = 300`,
  `list.component.ts:22`, `99-105`).
- Threads the active term through every fetch **including background polls**, so search survives
  the pending-poll loop (`runFetch` reads `this.query()`, `list.component.ts:170-175`).
- Renders a no-match state and a clear action (`noSearchResults`, `clearSearch`,
  `list.component.ts:89-91`, `134-151`).

The current UX is "filter the list as you type." A combined *query* upgrades that filtering to
also catch prefixes — **no template or component change is required** for the recommended
approach. (A distinct autocomplete-dropdown UX would be a larger, separate change — see Options.)

## Options for combining

### Option A — Single `QueryType.Full` query across both fields (recommended)

Build one search expression in `GetReceiptsAsync`, e.g. (conceptually):

```
searchText = "Tags:{escaped}* OR TagsPl:{escaped}"
options.QueryType = SearchQueryType.Full
// drop SearchFields = {TagsPl}; the field scoping now lives in the query string
```

- **Pros:** one round trip; one relevance-ranked result set; supports term **boosting** to rank
  exact/lemma hits above noisy prefixes (`TagsPl:{term}^3 OR Tags:{term}*`); minimal surface
  (one method + tests).
- **Cons / must-handle:** user input now flows into a Lucene query string → **escape** the Lucene
  special characters (`+ - && || ! ( ) { } [ ] ^ " ~ * ? : \ /`) before interpolation. Keep the
  `UserId` filter exactly as-is (term never touches `Filter`). **Verify** manually that the
  non-wildcard `TagsPl` clause is still analyzed under `QueryType.Full` (expected per Lucene, but
  the index is the sole data store — confirm with a live `?q=rowery` check, per the same manual
  step that caught the original regression).

### Option B — Two queries, merged in the service (simpler fallback)

Issue two `SearchAsync` calls — `TagsPl` analyzed (Simple) and `Tags` wildcard (Full) — then
union by `Id`, preferring the analyzed hit's rank.

- **Pros:** no Lucene-string escaping (each call uses the existing `SearchFields` mechanism);
  each behavior is independently testable; low QPS makes two calls a non-issue (PRD
  `target_scale.qps: low`, `tag-search` plan Performance section).
- **Cons:** merge/dedupe and a blended ordering must be written by hand; two round trips.

### Option C — Dedicated prefix field (edge n-gram) — not for MVP

A custom `edgeNGram` analyzer field gives true prefix-as-token matching, but requires a custom
analyzer in the index definition (heavier than `FieldBuilder` attributes) and more indexing cost.
Overkill given `Tags` already delivers usable prefixing.

### Option D — Suggester / Autocomplete API — different UX, larger change

`SuggestAsync` / `AutocompleteAsync` are Azure's purpose-built as-you-type features but require a
**suggester defined on the index** and change the UX to a completion dropdown rather than filtering
the list. A product decision, not a drop-in — out of scope for "combine the existing two methods."

## Code References

- `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs:21-31` — the single query branch to
  extend (this is where "combine" happens).
- `src/backend/ReceiptWell.Core/ReceiptDocument.cs:35` — `Tags`, standard analyzer → the prefix field.
- `src/backend/ReceiptWell.Core/ReceiptDocument.cs:43-44` — `TagsPl`, `pl.microsoft` → the lemma field.
- `src/backend/ReceiptWell.Tests/ReceiptSearchTests.cs:21-83` — unit tests that capture
  `searchText`/`SearchOptions`; these assert the current lemma-only contract and must be updated
  for whichever option is chosen (they are the guardrail against re-drifting).
- `src/frontend/src/app/receipts/list/list.component.ts:99-105,170-175` — debounce + term-through-poll
  wiring already in place; no change needed for Options A/B.

## Architecture Insights

- **One analyzer per field is the load-bearing constraint.** "Combine both" is fundamentally
  "query two fields," because a field can only be lemmatized *or* raw, and prefix needs raw while
  inflection needs lemmatized. The `tag-search` slice already set this up by adding `TagsPl`
  additively next to `Tags`.
- **The index is the sole data store.** Any approach that stays additive/query-only (A and B) is
  safe; anything requiring an analyzer change on an existing field would force an index rebuild and
  is off the table (`tag-search` plan "Critical Implementation Details").
- **Relevance/usability lever:** prefix matching is noisy mid-typing. Boosting the analyzed/lemma
  clause above the prefix clause (Option A) is the cleanest way to keep precise matches on top —
  a usability win the current single-clause query can't express.

## Historical Context (from prior changes)

- `context/archive/2026-06-24-tag-search/plan.md` — introduced `TagsPl`; **explicitly scoped out
  prefix/fuzzy matching** ("No prefix / fuzzy / partial matching", line 43). This change reopens
  that decision deliberately.
- `context/archive/2026-06-24-tag-search/reviews/impl-review.md:23-39` — F1/F2: the wildcard-vs-
  analyzer regression and the test corrections. Essential reading before touching the query.
- `context/foundation/lessons.md:54-59` — the generalized rule from that incident.

## Open Questions

1. **Query construction (A vs B):** single Full query with escaping + boosting, or two-call merge?
   (Recommendation: A, for one ranked result set and boosting; B if Lucene escaping feels risky.)
2. **Boosting/ordering:** should lemma/exact hits rank above prefix hits? If yes, Option A with a
   `^` boost; define the desired order before writing tests.
3. **Minimum prefix length:** apply the prefix clause on every keystroke, or only for inputs ≥ N
   chars, to reduce mid-typing noise? (Frontend debounce already caps request rate.)
4. **Verify the analyzer-under-Full assumption** on the live index (`?q=rowery` still lemmatizes)
   before closing the change — the sole-store index makes empirical confirmation worthwhile.

## Related Research

- None prior for this change. Upstream: `context/archive/2026-06-24-tag-search/` (plan + review).
