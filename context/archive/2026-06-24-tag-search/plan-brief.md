# Tag Search — Plan Brief

> Full plan: `context/changes/tag-search/plan.md`

## What & Why

Deliver the roadmap north star (S-04, US-01): a logged-in user types a tag like "rower" and finds their matching receipts. This is the slice that proves the product hypothesis — that automatic AI tagging is good enough to find a receipt by keyword weeks after purchase, with no manual labelling.

## Starting Point

The hard infrastructure is already in place from earlier slices: the Azure AI Search index exists with `Tags` as a searchable, filterable field; `SearchClient` is wired; and `ReceiptQueryService.GetReceiptsAsync` already lists a user's receipts via a `UserId`-scoped search. There is no way to pass a search term yet, and no search input in the UI.

## Desired End State

On `/home/receipts`, a search box sits above the list. Typing a tag (after a ~300ms debounce) filters the list to matching receipts — including Polish inflected forms, so "rowery" finds a receipt tagged "rower". Clearing it restores the full list; a term with no matches shows a distinct "No receipts match …" panel with a clear action. Requests go through the same `GET /receipts` endpoint with `?q=`, always scoped to the caller.

## Key Decisions Made

| Decision            | Choice                                  | Why (1 sentence)                                                       | Source |
| ------------------- | --------------------------------------- | --------------------------------------------------------------------- | ------ |
| Match semantics     | Full-text on a Polish-stemmed field     | Whole-word match fits FR-005; `pl.microsoft` lemmatizes so inflected forms match. | Plan |
| Stemming setup      | New `TagsPl` field (`pl.microsoft`), Polish-only | One analyzer per field + index is sole store → add a field (no rebuild); tags are PRD-normalized to Polish, so English adds marginal value. | Plan |
| Search scope        | Tags only                               | Matches the PRD "search by tag" precisely; smallest, predictable surface. | Plan |
| UI placement        | Search bar on the existing receipts list | One screen for browse + search; reuses list rendering and poll loop.   | Plan   |
| Endpoint shape      | Extend `GET /receipts` with `?q=`       | One endpoint serves browse + filter, fitting the single-bar UI.        | Plan   |
| Empty / no-result   | Blank `q` → all; no match → empty panel | Intuitive, never traps the user; 400 would be non-retriable.           | Plan   |
| Input UX            | Debounced live search (~300ms)          | Instant feel, no submit button, bounded request volume.                | Plan   |
| Testing             | Backend integration + frontend specs    | Locks the privacy-critical user scoping and the UX branches.           | Plan   |

## Scope

**In scope:** new Polish-analyzed `TagsPl` index field + one-time backfill; `?q=` parameter on `GET /receipts`; full-text search over `TagsPl` kept `UserId`-scoped; relevance ordering on search / recency on browse; debounced search bar; no-match empty state; backend integration + frontend component tests; `.http` update.

**Out of scope:** Terraform/infra changes; index rebuild or changes to the existing `Tags` field; English/bilingual stemming, `pl.lucene`, prefix or fuzzy matching; searching store/file/date; thumbnails in results (S-05); new route or nav entry; pagination.

## Architecture / Approach

Extend, don't add. Backend: a new additive `TagsPl` field (analyzer `pl.microsoft`) mirrors `Tags` and is populated on enrichment; existing docs are backfilled once. One optional `q` parameter is threaded from the endpoint into the existing query service — a present term switches `searchText` + `SearchFields=TagsPl` and drops the recency `OrderBy` for relevance; the `UserId` filter is untouched in both modes. Frontend: a debounced term signal feeds every fetch (including background polls) through the existing `ReceiptService`/poll machinery, with a new no-match template branch.

## Phases at a Glance

| Phase                       | What it delivers                                                       | Key risk                                                        |
| --------------------------- | --------------------------------------------------------------------- | --------------------------------------------------------------- |
| 1. Backend search (stemmed) | `TagsPl` field + backfill + `GET /receipts?q=` search, scoped + tested | Backfill must run per environment; user-scoping must hold on the search path (privacy guardrail). |
| 2. Frontend search bar      | Debounced search input + no-match state on the list, tested           | The 5s pending-poll must not wipe out an active search.         |

**Prerequisites:** F-01 (auth gate) and S-03 (AI extraction producing tags) complete — both present in the codebase.
**Estimated effort:** ~2 sessions across 2 phases; Phase 1 adds an index field + one-time backfill on top of the search endpoint.

## Open Risks & Assumptions

- Result quality depends entirely on S-03 tag quality; weak extraction surfaces here first (roadmap-noted product risk).
- The one-time `Tags`→`TagsPl` backfill must be run against each environment after deploy; until it runs, pre-existing receipts are not searchable (new uploads are unaffected).
- `pl.microsoft` handles Polish inflection but not cross-language: an English query won't match a Polish tag (and vice versa). Accepted — tags are normalized to Polish.

## Success Criteria (Summary)

- A user finds their own receipt by typing its tag, and never sees another user's receipts.
- Clearing the search returns to the full newest-first list; a non-matching term shows a clear-able no-match panel.
- Searching keeps working while a freshly uploaded receipt is still processing.
