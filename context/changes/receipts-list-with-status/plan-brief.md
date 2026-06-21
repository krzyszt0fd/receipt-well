# Receipts List with Status (S-02) — Plan Brief

> Full plan: `context/changes/receipts-list-with-status/plan.md`

## What & Why

Add a receipts list screen showing each receipt's processing status (`pending`/`ready`/`error`) plus the store, date, and tags S-03 already extracts. S-03 is implemented and deployed but nothing renders its output yet — this slice is the one S-03's own close-out notes deferred rendering to.

## Starting Point

Backend has only `POST /receipts/staging-slot` and `POST /receipts/confirm` — no read endpoint. The Azure AI Search index already has every field this needs (`Status`, `StoreName?`, `PurchaseDate?`, `Tags`), all filterable/sortable where required, so no schema change. Frontend has exactly one route (`/home/upload`); no list, no nav bar.

## Desired End State

A signed-in user opens the receipts list and sees every receipt newest-first, with a color-coded status chip and — once processed — store name, purchase date, and up to 5 tags ("+K more" beyond that). A new user sees a friendly empty state. A manual refresh always works; once Phase 3 lands, the list also quietly keeps itself fresh while anything is still processing.

## Key Decisions Made

| Decision | Choice | Why (1 sentence) |
| --- | --- | --- |
| List content | Status + full metadata (store/date/tags) | The data already exists in the index; rendering only status would waste it and require touching this component again later |
| Live updates | Auto-poll only while something is pending, plus manual refresh | Fresh exactly when needed, zero idle cost once everything settles |
| Navigation | Upload stays the `/home` default; two contextual links bridge it to the list | No persistent nav bar needed for a two-screen MVP |
| Status visual | Colored `mat-chip` | Matches the existing Material/indigo-pink language; accessible (color + text) |
| Scale | Single fetch, no pagination (capped at 1000) | Matches PRD's small-data-volume MVP target; "load more" pagination is an explicit post-MVP follow-up |
| Tag overflow | Cap at 5 + "+K more", no expand | Keeps row height predictable; tags are for S-04 search, not browsing |
| Empty state | Icon + message + "Upload your first receipt" button | Matches the card-based visual language already shipped |
| Fallback ladder | Status + manual refresh = must-have; auto-poll = cuttable | The literal roadmap outcome survives any time crunch |

## Scope

**In scope:** `GET /receipts` endpoint; list page with loading/error/empty/populated states; status chips; store/date/tag display with tag cap; manual refresh; auto-poll while pending; two nav links (upload ↔ list).

**Out of scope:** real pagination beyond a single 1000-row fetch, manual retry of `error` receipts, a persistent nav bar or logout, thumbnails (S-05), tag-based search (S-04), expand/collapse for overflow tags, WebSocket/push updates.

## Architecture / Approach

Backend: one new scoped service (`ReceiptQueryService`) queries the existing `SearchClient` with `Filter = "UserId eq '{oid}'"`, `OrderBy = "UploadedAt desc"`, returning a new `ReceiptSummary` DTO — no index or infra change. Frontend: one new lazy-loaded route/component (`ReceiptListComponent`) under `/home/receipts`, following the existing signals + `OnPush` + Material patterns from the upload flow; a new `$pending` color var supports the third chip state.

## Phases at a Glance

| Phase | What it delivers | Key risk |
| --- | --- | --- |
| 1. Backend endpoint | `GET /receipts` returning status + metadata, filtered to the caller | Low — pure read over an already-correct schema |
| 2. Frontend list page (must-have) | Full list UI: chips, metadata, tag cap, empty/error states, manual refresh, nav links | Medium — most of the UI surface; satisfies the roadmap outcome on its own |
| 3. Auto-poll (nice-to-have) | Self-stopping background refresh while anything is pending | Low-medium — timer/state bookkeeping (avoiding double fetches, spinner flicker) |

**Prerequisites:** F-01 (auth gate) and S-01 (upload/confirm) — both already implemented. S-03 (already implemented) is what makes the metadata fields meaningful.
**Estimated effort:** ~2-3 sessions across 3 phases.

## Open Risks & Assumptions

- Assumes the live index already has at least one `ready`/`error` receipt to verify rendering against (from S-03's earlier validation runs) — if not, Phase 1 manual verification needs a fresh upload run through the full pipeline first.
- Assumes Azure AI Search's default Search API key auth (already in place) is sufficient for a read-only query; no new role/auth wiring anticipated.

## Success Criteria (Summary)

- A user can see every uploaded receipt's current processing status without guessing.
- A user can see the store, date, and tags S-03 extracted, the moment they're available — no separate screen or wait beyond what's already happening server-side.
- A user with zero receipts is guided back to upload, not left looking at a blank page.
