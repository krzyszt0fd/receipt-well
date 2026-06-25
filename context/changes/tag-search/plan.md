# Tag Search Implementation Plan

## Overview

Deliver the roadmap north star (S-04): a logged-in user types a tag like "rower" and finds their matching receipts — including inflected forms like "rowery". The search index, client wiring, and a list-returning query service already exist from earlier slices, so this plan is focused: add a Polish-stemmed `TagsPl` field (analyzer `pl.microsoft`) to the index, extend the existing `GET /receipts` endpoint with an optional `?q=` term that runs a full-text query over `TagsPl` (kept strictly `UserId`-scoped), and add a debounced search bar atop the existing receipts list with a no-result empty state.

## Current State Analysis

What exists today:

- **Azure AI Search index is live.** `ReceiptDocument` (`src/backend/ReceiptWell.Core/ReceiptDocument.cs`) defines `Tags` as `[SearchableField(IsFilterable = true)]`; `SearchIndexInitializer` provisions the index at startup; `SearchClient`/`SearchIndexClient` are registered in `Program.cs:89-96`.
- **A list query service to extend.** `ReceiptQueryService.GetReceiptsAsync` (`src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs`) runs `SearchAsync("*", options)` with `Filter = "UserId eq '{userId}'"`, `OrderBy UploadedAt desc`, `Size 1000`, mapping each hit to `ReceiptSummary`. Tag search is the same call with a real query term and `SearchFields = { "Tags" }`.
- **Endpoint pattern.** Minimal-API endpoints in `Program.cs` resolve identity via `httpContext.User.GetUserId()` and wrap work in try/catch → `Results.Problem(statusCode: 500)`. `GET /receipts` (`Program.cs:197-215`) is the one to extend.
- **Tags are lowercase, Polish-normalized.** `TagNormalizer` (`src/backend/ReceiptWell.Functions/TagNormalizer.cs`) trims, lowercases (`ToLowerInvariant`), and de-dupes tags before they are indexed. The existing `Tags` field uses the default standard Lucene analyzer — no Polish stemming, so "rowery" does not match "rower".
- **The Search index is the sole source of truth.** There is no SQL/Cosmos store. `ReceiptConfirmService` writes the initial `ReceiptDocument`, the extraction Function merges enrichment via `ReceiptStore.SetReadyAsync` (`src/backend/ReceiptWell.Functions/ReceiptStore.cs`), and reads go through `GetDocumentAsync`/`SearchAsync`. `ReceiptStore.cs:33` calls it "the frozen index schema."
- **Per Azure AI Search rules** (`learn.microsoft.com/azure/search/search-analyzers`, `index-add-language-analyzers`): a field has exactly one analyzer; a language analyzer (`pl.microsoft`) is used for *both* indexing and querying and cannot be split. You **cannot change an existing field's analyzer** without dropping & recreating the index — which, since the index is the only store, would destroy all receipt metadata. Adding a *new* field is allowed live via `CreateOrUpdateIndex`, but existing documents have it empty until re-indexed.
- **Frontend.** `ReceiptService` (`src/frontend/src/app/receipts/receipt.service.ts`) exposes `getReceipts()`. `ReceiptListComponent` (`.../receipts/list/list.component.ts`) is a signals-based, OnPush component with a pending-poll loop (`POLL_INTERVAL_MS = 5000`) that re-fetches while any receipt is `pending`. Routes live under `/home/{upload,receipts}`. UI uses Angular Material + the shared `.page`/`.page-card` SCSS convention.
- **Test harness.** `ReceiptWellWebFactory` (`src/backend/ReceiptWell.Tests/Infrastructure/`) boots the real app offline with NSubstitute Azure clients; `TestAuthHandler` injects identity via `X-Test-Oid`. `ReceiptQueryScopingTests.cs` already covers the list path. The backend CLAUDE.md requires filter assertions to be **requirement-derived** (assert the filter contains the scoping field and the caller's id — never an exact-string equality).

What's missing: any way to pass a search term end-to-end; a search input in the UI; a no-result state distinct from the "no receipts yet" empty state.

## Desired End State

A user on `/home/receipts` sees a search box above their receipt list. Typing "rower" (after a ~300ms debounce) replaces the list with only receipts carrying a matching `Tags` entry; clearing the box returns to the full list. A term with zero matches shows a "No receipts match …" panel with a clear-search action. The search request is the same `GET /receipts` endpoint with `?q=rower`, and it never returns another user's receipts.

Verify: with two seeded users, user A searching a tag that only user B's receipt carries returns empty; user A searching their own tag returns exactly their matching receipt.

### Key Discoveries:

- `ReceiptQueryService.GetReceiptsAsync` already builds `SearchOptions` with the `UserId` filter — adding a term means changing the `searchText` argument from `"*"` to the user's term and setting `SearchFields = { "TagsPl" }` (`ReceiptQueryService.cs:12-19`).
- `SearchIndexInitializer` builds the index schema from `ReceiptDocument` via `new FieldBuilder().Build(typeof(ReceiptDocument))`, so adding an attributed `TagsPl` property is enough — `CreateOrUpdateIndexAsync` adds the field live, no manual index definition (`SearchIndexInitializer.cs:17-19`).
- `TagsPl` is populated at the same point `Tags` is set: `ReceiptStore.SetReadyAsync` / `ReceiptEnrichmentDocument` after extraction (`ReceiptStore.cs:25-42`). The confirm-time doc has no tags yet, so no change there.
- Existing receipts need a **one-time backfill** copying `Tags` → `TagsPl`, since the new field is empty on documents indexed before this change.
- The endpoint's `OrderBy UploadedAt desc` should be **dropped when a term is present** so Search can rank by relevance; keep it for the browse (blank-`q`) path. (See Critical Implementation Details.)
- The frontend poll loop re-fetches via `getReceipts()` — it must carry the active search term, or polling would silently reset search results to the full list (`list.component.ts:69-92`).
- `400` is non-retriable per `lessons.md`; blank `q` must therefore mean "list all", not a validation error.

## What We're NOT Doing

- **No infrastructure changes.** The Search service, `SearchClient`, queue, and blob resources already exist and are provisioned by Terraform — verified present in `Program.cs:89-96`. No `.tf` edits. (The `TagsPl` field is added to the index schema in code via the existing `SearchIndexInitializer`, not Terraform.)
- **No index rebuild and no change to the existing `Tags` field.** `Tags` stays as-is for display/retrieval; stemming is added via a *new* additive `TagsPl` field. The index is never dropped (it is the sole data store).
- **No English stemming / bilingual search.** Decided Polish-only (`pl.microsoft`) — tags are PRD-normalized to Polish. No `TagsEn`, no `pl.lucene`, no suggester, no synonyms.
- **No prefix / fuzzy / partial matching.** Whole-word full-text (with Polish lemmatization) only.
- **No search over store name, file name, or date.** Tags-only, per FR-005.
- **No thumbnails in results** — that is S-05 (`receipt-thumbnail-in-search`), a separate slice.
- **No new route or nav entry** — search lives on the existing `/home/receipts` list.
- **No pagination of results** — the existing `Size 1000` cap is retained.

## Implementation Approach

Extend rather than add. The backend gains one optional query parameter threaded into the existing query service; the frontend gains a search-term signal wired into the existing fetch/poll machinery. Both modes (browse vs. search) flow through one endpoint and one component, so the pending-poll behavior and result rendering are reused unchanged.

## Critical Implementation Details

- **Additive field only — never mutate `Tags`.** Changing the analyzer on the existing `Tags` field requires dropping and recreating the index; since the index is the only store, that destroys all receipt metadata. Add a new `TagsPl` field instead. `CreateOrUpdateIndexAsync` accepts new fields on an existing index without downtime.
- **Backfill is one-time and idempotent.** Existing documents have `TagsPl` empty until re-indexed. A throwaway backfill pages all documents (search `*`) and merges `{ Id, TagsPl = <existing Tags> }` per doc via `MergeOrUploadDocumentsAsync`. It is safe to re-run (merge is idempotent) and can be deleted after running once against each environment. New uploads need no backfill — `SetReadyAsync` populates `TagsPl` going forward.
- **Relevance vs. recency ordering.** When `q` is blank (browse), keep `OrderBy UploadedAt desc` so the list is newest-first. When `q` is present (search), omit the explicit `OrderBy` so Azure AI Search returns hits by relevance score. Passing both a real `searchText` and an `OrderBy` would suppress relevance ranking. **Why relevance over recency on search:** a receipt can carry several tags, so TF/IDF surfaces the receipts where the matched tag is the more salient/dominant tag first (e.g. a receipt that is primarily a "rower" purchase ranks above one where "rower" is an incidental line-item tag), which is more useful for a tag lookup than strict upload order. (If field-uniform scores make ordering feel arbitrary in practice, revisit by re-adding `OrderBy UploadedAt desc` to the search branch — it is a one-line change with no schema impact.)
- **Search term must survive polling.** The poll loop in `ReceiptListComponent` re-invokes the fetch. The active search term must be read at fetch time (from a signal) so a background poll re-runs the *current* search, not a bare list — otherwise results flicker back to "all" every 5s while a receipt is pending.
- **User scoping is non-negotiable and term-independent.** The `UserId eq '{userId}'` filter must remain applied in the search path exactly as in the browse path. The term changes `searchText`/`SearchFields`, never the `Filter`.

## Phase 1: Backend search support (Polish-stemmed)

### Overview

Add a Polish-stemmed `TagsPl` field to the index schema, populate it on enrichment, and backfill existing documents. Thread an optional search term through `GET /receipts` and `ReceiptQueryService` (matching against `TagsPl`), preserving user scoping and switching ordering between recency (browse) and relevance (search). Cover the security-critical scoping and the blank/term branches with integration tests. Update the example request collection.

### Changes Required:

#### 1. Add the Polish-analyzed search field

**File**: `src/backend/ReceiptWell.Core/ReceiptDocument.cs`

**Intent**: Add a `TagsPl` string-collection field that mirrors `Tags` but is analyzed with the Polish Microsoft analyzer so inflected queries match (e.g. "rowery" → "rower"). `Tags` is unchanged and remains the display/retrieval field. The existing `SearchIndexInitializer` will add this field to the live index automatically via `FieldBuilder`.

**Contract**: `[SearchableField(AnalyzerName = LexicalAnalyzerName.Values.PlMicrosoft)] public IList<string> TagsPl { get; set; } = [];` (analyzer name `"pl.microsoft"`). Search-only intent — not filterable, not the display source. Requires `using Azure.Search.Documents.Indexes.Models;` for `LexicalAnalyzerName`.

#### 2. Populate `TagsPl` on enrichment

**File**: `src/backend/ReceiptWell.Functions/ReceiptStore.cs`

**Intent**: When the extraction Function writes the normalized tags via `SetReadyAsync`, also write the same list into `TagsPl` so the Polish-analyzed field stays in sync with `Tags`. Add the property to the partial-merge `ReceiptEnrichmentDocument`.

**Contract**: `ReceiptEnrichmentDocument` gains `public IList<string> TagsPl { get; set; } = [];`, set to the same `tags.ToList()` as `Tags` in `SetReadyAsync`. No other write path changes (confirm-time doc has no tags yet).

#### 3. One-time backfill of existing documents

**File**: new throwaway utility — e.g. `src/backend/ReceiptWell.Tools/BackfillTagsPl` (console) or a `[Fact(Skip=...)]`-style runner; implementer's choice of the lightest form.

**Intent**: Copy `Tags` → `TagsPl` for documents indexed before this change, so already-stored receipts become searchable with stemming. Pages all documents via `SearchAsync("*")` and merges `{ Id, TagsPl }` back. Idempotent; run once per environment, then discard.

**Contract**: Reads `Tags` + `Id` for every doc, calls `MergeOrUploadDocumentsAsync` with `{ Id, TagsPl = Tags }` in batches. No change to any other field. Not part of the app runtime — not wired into `Program.cs`.

#### 4. Query service accepts a search term

**File**: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs`

**Intent**: Let `GetReceiptsAsync` take an optional query term. When absent/blank, preserve today's behavior (search text `"*"`, order by `UploadedAt desc`). When present, use the term as `searchText`, restrict matching to the `TagsPl` field, and drop the explicit ordering so relevance ranking applies. The `UserId` filter is unchanged in both branches.

**Contract**: `GetReceiptsAsync(string userId, string? query, CancellationToken ct)`. Internally sets `SearchOptions.SearchFields = { "TagsPl" }` and omits `OrderBy` when `query` is non-blank; otherwise keeps `OrderBy UploadedAt desc`. `searchText` is the trimmed term or `"*"`. Mapping to `ReceiptSummary` is unchanged (still reads `Tags`).

#### 5. Endpoint exposes `?q=`

**File**: `src/backend/ReceiptWell.Web/Program.cs`

**Intent**: Add an optional `q` query-string parameter to the `GET /receipts` handler and forward it to the query service. Blank/whitespace/missing `q` behaves as browse-all (no 400). Identity resolution and the try/catch→`Results.Problem(500)` envelope are unchanged.

**Contract**: `app.MapGet("/receipts", async (HttpContext, ReceiptQueryService, ILoggerFactory, string? q, CancellationToken) => …)`. Passes `q` to `GetReceiptsAsync`. Response shape (array of `ReceiptSummary`) is unchanged.

#### 6. Example request collection

**File**: `receipt-well.http` (repo root)

**Intent**: Add an example `GET /receipts?q=rower` request alongside the existing list request, per the backend convention to keep the `.http` collection current after an endpoint contract change.

**Contract**: One new request entry exercising the `q` parameter with the standard auth header used by the other entries.

#### 7. Integration tests for the search path

**File**: `src/backend/ReceiptWell.Tests/ReceiptQueryScopingTests.cs` (extend) or a new `ReceiptSearchTests.cs` sibling

**Intent**: Prove the behaviors that matter: (a) a search request stays scoped to the caller's `UserId`; (b) a non-blank `q` is applied as the search text against the `TagsPl` field; (c) a blank/missing `q` lists all (browse path, no 400). Use the NSubstitute `SearchClient` from `ReceiptWellWebFactory` to capture the `SearchOptions`/`searchText` actually passed.

**Contract**: Assertions are **requirement-derived** — assert the captured `Filter` contains the scoping field name and the caller's oid (never exact-string equality), assert `SearchFields` contains `TagsPl` and `searchText` equals the term on the search path, and assert the browse path passes `"*"`. Identity is injected via the `X-Test-Oid` header per the harness. (Actual Polish lemmatization runs in the real Search service, not the substitute — its behavior is covered by manual verification below.)

### Success Criteria:

#### Automated Verification:

- Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- All backend tests pass: `dotnet test src/backend/ReceiptWell.sln`
- New search tests assert user-scoping holds on the search path and the term is applied to `TagsPl`

#### Manual Verification:

- The `TagsPl` field is present on the live index after startup (`SearchIndexInitializer` ran); the existing index was updated, not recreated.
- Backfill ran once: an existing pre-change receipt is findable by its tag after the backfill.
- Polish stemming works: a receipt tagged "rower" is returned by `?q=rowery` (inflected form), confirming `pl.microsoft` lemmatization.
- `GET /receipts?q=<tag>` returns only the caller's receipts whose tags match the term
- `GET /receipts` (no `q`) and `GET /receipts?q=` (blank) both return the full newest-first list
- A term with no matches returns an empty array (HTTP 200, not 400)

**Implementation Note**: After this phase passes automated verification, pause for manual confirmation that the endpoint behaves as expected (use `receipt-well.http`), including the stemming and backfill checks, before starting Phase 2.

---

## Phase 2: Frontend search bar on the receipts list

### Overview

Add a debounced search input above the existing receipt list, wire the term through `ReceiptService` and the fetch/poll loop, and add a no-result empty state distinct from the "no receipts yet" state. Cover the query→results, no-result, and clear flows with component specs.

### Changes Required:

#### 1. Service passes the search term

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Allow `getReceipts` to send an optional search term as the `q` query parameter; calling it with no term preserves today's full-list request.

**Contract**: `getReceipts(query?: string): Observable<ReceiptSummary[]>` — appends `q` via `HttpParams` only when a non-empty term is supplied. Return type unchanged.

#### 2. Search input + term state on the list component

**File**: `src/frontend/src/app/receipts/list/list.component.ts`

**Intent**: Hold the search term in a signal, debounce input changes (~300ms), and feed the current term into every fetch — including background polls — so search survives the pending-poll loop. Clearing the input returns to the full list. In-flight requests are superseded on a new term (reuse the existing `fetchSubscription` unsubscribe pattern). Reactive form control for the input per the Angular conventions.

**Contract**: A `query` signal (or `FormControl` + signal) drives a debounced fetch; `runFetch`/`pollReceipts` read the active term and call `receiptService.getReceipts(term)`. Add a `searching`/term-presence computed to drive template branching. No `mutate`; use `set`/`update`.

**Contract** (debounce wiring — non-obvious): debounce the input via the form control's `valueChanges` with `debounceTime(300)` + `distinctUntilChanged`, mapped into the fetch; ensure the subscription is torn down in `ngOnDestroy` alongside the existing poll/ fetch cleanup.

#### 3. Search UI + no-result state in the template

**File**: `src/frontend/src/app/receipts/list/list.component.html` (+ `.scss` as needed)

**Intent**: Render a labelled search field at the top of the list card (visible whenever the user has receipts). Add a new branch: when a term is active and results are empty, show a "No receipts match \"<term>\"" panel with a clear-search action — distinct from the existing "No receipts yet / Upload your first receipt" empty state, which stays for the no-term case. Must meet the project's AXE/WCAG-AA bar (label, focus, contrast).

**Contract**: Native control flow (`@if`/`@for`); a new no-match branch keyed off "term active && results empty"; a clear-search control that resets the term signal. Accessible label on the input; `class`/`style` bindings only (no `ngClass`/`ngStyle`).

#### 4. Component specs

**File**: `src/frontend/src/app/receipts/list/list.component.spec.ts` (extend)

**Intent**: Cover the new flows: typing a term triggers a (debounced) fetch with `q` and renders the returned subset; an empty result with an active term shows the no-match panel; clearing the term restores the full list. Mock `ReceiptService.getReceipts` to assert it receives the term.

**Contract**: Specs assert `getReceipts` is called with the typed term, that the no-match branch renders only when a term is active, and that clearing resets to the no-term fetch. Use fake async / tick for the debounce.

### Success Criteria:

#### Automated Verification:

- Frontend builds: `npm --prefix src/frontend run build`
- Lint passes: `npm --prefix src/frontend run lint`
- Component specs pass: `npm --prefix src/frontend test`

#### Manual Verification:

- Typing a tag filters the list after a brief pause; clearing the box restores the full list
- A non-matching term shows the no-match panel with a working clear action (distinct from "No receipts yet")
- While a receipt is `pending`, the 5s poll does not wipe out an active search
- Search field is keyboard-accessible and passes AXE (label, focus, contrast)

**Implementation Note**: After automated verification passes, pause for manual confirmation in the browser (filter, clear, no-match, and poll-during-search) before considering the slice done.

---

## Testing Strategy

### Unit / Component Tests:

- Backend: search path stays `UserId`-scoped (requirement-derived filter assertion); term applied to `Tags`; blank `q` → browse-all.
- Frontend: term → `getReceipts(q)`; no-match panel renders only with an active term; clear restores full list; debounce via fake async.

### Integration Tests:

- End-to-end via `ReceiptWellWebFactory`: authenticated `GET /receipts?q=` captures the `SearchOptions` passed to the substituted `SearchClient` and asserts scoping + search fields.

### Manual Testing Steps:

1. Seed two users with distinct tags; confirm a user only finds their own receipts by tag.
2. Search a tag that exists → matching receipts; search a nonsense term → no-match panel; clear → full list.
3. Upload a receipt (status `pending`) and search while polling is active → search results persist across polls.
4. Keyboard-only pass on the search field; run AXE.

## Performance Considerations

Query volume is low (PRD `target_scale.qps: low`). Debouncing input at ~300ms keeps keystroke-driven requests bounded. The `Size 1000` cap is unchanged and adequate for the small data volume. No new indexes or analyzers, so no indexing-cost change.

## Migration Notes

- **New index field, no rebuild.** `TagsPl` is added to the existing index live via `SearchIndexInitializer`/`CreateOrUpdateIndexAsync`. The index is never dropped, so no receipt metadata is lost.
- **One-time backfill per environment.** Run the backfill utility once against each environment (local Azurite/emulator if used, and the live service) after the field is added, to copy `Tags` → `TagsPl` on pre-existing documents. Idempotent; discard the utility after running.

## References

- Roadmap slice S-04: `context/foundation/roadmap.md` (north star)
- PRD: FR-005, US-01 — `context/foundation/prd.md`
- Query service to extend: `src/backend/ReceiptWell.Web/Services/ReceiptQueryService.cs`
- Endpoint to extend: `src/backend/ReceiptWell.Web/Program.cs:197-215`
- List component to extend: `src/frontend/src/app/receipts/list/list.component.ts`
- Example request collection: `receipt-well.http` (repo root; backend convention references it as `@../../receipt-well.http`)
- Lessons: 400-not-retriable; verify infra exists; requirement-derived filter assertions — `context/foundation/lessons.md`
- Azure AI Search language analyzers: `learn.microsoft.com/azure/search/index-add-language-analyzers`, `learn.microsoft.com/azure/search/search-analyzers` (one analyzer per field; language analyzer used for both index + query; `pl.microsoft` lemmatization)

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles.

### Phase 1: Backend search support (Polish-stemmed)

#### Automated

- [x] 1.1 Solution builds with zero warnings: `dotnet build src/backend/ReceiptWell.sln`
- [x] 1.2 All backend tests pass: `dotnet test src/backend/ReceiptWell.sln`
- [x] 1.3 New search tests assert user-scoping holds on the search path and the term is applied to `TagsPl`

#### Manual

- [ ] 1.4 `TagsPl` field present on the live index after startup; index updated, not recreated
- [ ] 1.5 Backfill ran once; a pre-change receipt is findable by its tag
- [ ] 1.6 Polish stemming works: a receipt tagged "rower" is returned by `?q=rowery`
- [ ] 1.7 `GET /receipts?q=<tag>` returns only the caller's receipts whose tags match the term
- [ ] 1.8 `GET /receipts` (no `q`) and `GET /receipts?q=` (blank) both return the full newest-first list
- [ ] 1.9 A term with no matches returns an empty array (HTTP 200, not 400)

### Phase 2: Frontend search bar on the receipts list

#### Automated

- [ ] 2.1 Frontend builds: `npm --prefix src/frontend run build`
- [ ] 2.2 Lint passes: `npm --prefix src/frontend run lint`
- [ ] 2.3 Component specs pass: `npm --prefix src/frontend test`

#### Manual

- [ ] 2.4 Typing a tag filters the list after a brief pause; clearing the box restores the full list
- [ ] 2.5 A non-matching term shows the no-match panel with a working clear action (distinct from "No receipts yet")
- [ ] 2.6 While a receipt is `pending`, the 5s poll does not wipe out an active search
- [ ] 2.7 Search field is keyboard-accessible and passes AXE (label, focus, contrast)
