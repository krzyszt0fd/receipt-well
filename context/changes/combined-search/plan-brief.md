# Combined Linguistic + Prefix Tag Search — Plan Brief

> Full plan: `context/changes/combined-search/plan.md`
> Research: `context/changes/combined-search/research.md`

## What & Why

The linguistic (lemmatized) match added in `tag-search` overrode prefix search, so results
no longer appear as the user types. We want both: linguistic match so "rower" matches
"rowery", **and** prefix match so results show mid-typing. The two can't share one clause on
one field, so we query both fields in one request and union the hits.

## Starting Point

`ReceiptQueryService.GetReceiptsAsync` is the entire search surface. For a non-blank term it
queries only the Polish-stemmed `TagsPl` field with a plain analyzed term — lemma works,
prefix doesn't. Both index fields (`Tags` = raw, `TagsPl` = lemmatized) already exist and are
populated; no migration is needed. The frontend already debounces and threads the term through
polls — no frontend change.

## Desired End State

Typing `rowe` returns `rower` and `rowery` (prefix); typing `rowery` still returns a
`rower`-tagged receipt (lemma); when a doc matches both ways, the lemma/exact hit ranks above
the prefix hit. A search fires only from the 2nd character; a single character holds the
current view. Browse-all and caller scoping are unchanged.

## Key Decisions Made

| Decision              | Choice                                             | Why (1 sentence)                                                              | Source   |
| --------------------- | -------------------------------------------------- | ---------------------------------------------------------------------------- | -------- |
| Query construction    | Single `QueryType.Full` query                      | One round trip, one ranked result set, boosting available.                   | Plan     |
| Query string          | `TagsPl:{term}^3 OR Tags:{term}*`                  | Analyzed lemma clause + prefix clause coexist in one Full query.             | Research |
| Ranking               | Boost lemma/exact above prefix (`^3`)              | Keeps precise matches on top; mid-typing prefix noise sinks below.          | Plan     |
| Minimum search length | 2 characters (frontend gate); empty = browse-all  | Damps the whole-catalog result a 1-char prefix returns mid-typing.         | Plan     |
| Guard against re-drift | Unit-contract tests + mandatory live `?q=` check | Mocks can't run `pl.microsoft`; live check proves analyzer fires under Full. | Plan     |
| Input safety          | Escape Lucene special chars before interpolation  | User term now flows into a query string — prevents syntax injection.        | Research |

## Scope

**In scope:**
- Rewrite the search branch of `GetReceiptsAsync` (escape term, build boosted Full query, drop `SearchFields`).
- Update `ReceiptSearchTests` to lock the new query contract.
- Add a 2-character minimum gate to the frontend search subscription + its Vitest tests.
- Manual live-index verification of lemma, prefix, escaping, and boost.

**Out of scope:**
- Index schema / new field / custom analyzer / rebuild / backfill (fields already exist).
- Two-call merge (Option B), suggester/autocomplete (Option D), edge-ngram field (Option C).
- Any template/HTML/SCSS change, or changes to debounce/poll wiring.
- Server-side minimum-length gate; new integration/E2E harness.

## Architecture / Approach

One `QueryType.Full` search request. Field scoping moves out of `options.SearchFields` and
into the query string: an analyzed, boosted lemma clause on `TagsPl` OR a wildcard prefix
clause on the raw `Tags` field. The `UserId` filter stays on `options.Filter` and never
touches the search term. Escape the term against Lucene special characters *before* appending
the `*` and `^3` operators.

## Phases at a Glance

| Phase                                    | What it delivers                                    | Key risk                                                        |
| ---------------------------------------- | --------------------------------------------------- | -------------------------------------------------------------- |
| 1. Combined boosted query + contract tests | Prefix + lemma in one ranked query; updated tests | Analyzer-under-Full is mock-invisible — needs live `?q=` check |
| 2. Frontend 2-char minimum search gate    | Search fires from the 2nd char; Vitest coverage    | 1-char no-op leaves stale results if the box is edited down    |

**Prerequisites:** running backend against the real Search index for the manual step; frontend via `npm run start:local`.
**Estimated effort:** ~1 session — one backend method + one frontend gate, their tests, and a live-index smoke check.

## Open Risks & Assumptions

- **Analyzer-under-Full assumption** (`research.md:84-86`): a plain `TagsPl` term stays
  analyzed inside a `QueryType.Full` query. Expected per Lucene, but the index is the sole
  data store — the mandatory live `?q=rowery` check confirms it. This is the exact gap the
  prior regression slipped through.
- The 2-char minimum treats a 1-char term as a no-op, so editing a term down to one character
  leaves the previous results on screen (box and results briefly disagree). Accepted as a minor
  wrinkle; clearing fully reverts to browse-all.

## Success Criteria (Summary)

- `rowe` → both `rower` and `rowery`; `rowery` → still matches `rower` (both behaviors live).
- Lemma/exact hits rank at or above prefix-only hits.
- Special-character terms don't error; browse-all and caller scoping unregressed.
