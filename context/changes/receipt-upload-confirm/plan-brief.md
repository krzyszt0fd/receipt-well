# Receipt Upload Confirmation — Plan Brief

> Full plan: `context/changes/receipt-upload-confirm/plan.md`

## What & Why

Implement S-01: the first user-facing feature — upload a receipt photo and receive
immediate confirmation (filename + size). This slice introduces the Receipt data
contract that S-02 (list), S-03 (AI extraction), and S-04 (tag search) all depend on.
Getting the schema right here avoids a double-cost migration later.

## Starting Point

F-01 (auth gate) is fully complete: all backend endpoints default-deny, Angular shell
at `/home` has `MsalGuard`, MSAL interceptor attaches Bearer tokens to API calls
automatically. The `children: []` array in the shell route is waiting for S-01's
first child. No blob storage, no Azure AI Search SDK, and no domain models exist yet.

## Desired End State

An authenticated user at `/home/upload` picks a PNG, JPEG, WEBP, or GIF photo (≤ 20 MB),
sees a spinner, then an inline confirmation panel with the filename and formatted file
size, plus an "Upload another" button. The backend has the photo stored as
`{userId}/{receiptId}.{ext}` in the receipts Blob container and a `ReceiptDocument`
(status: `pending`) indexed in Azure AI Search — the foundation S-02 and S-03 build on.

## Key Decisions Made

| Decision | Choice | Why | Source |
|---|---|---|---|
| Metadata store | Azure AI Search | Native tag search for S-04; no workaround needed at the storage layer | Plan |
| Upload flow | SAS staging pattern | Bytes bypass the API; staging quarantine + server re-validation; production-proven | Plan |
| Blob isolation | Single container, `{userId}/{receiptId}.{ext}` paths | Standard Azure pattern; no container-count limits; RBAC via managed identity | Plan |
| User identity | `oid` JWT claim | Stable Entra External ID identifier; doesn't change on email rename | Plan |
| Route | `/home/upload` child, shell redirects `/home` → `/home/upload` | Explicit URL; `/home` stays available for S-02's list view | Plan |
| Confirmation UX | Inline state transition | Signal-driven; no external UI library needed | Plan |
| SAS generation | Branch on `CanGenerateSasUri` (account key local / user delegation key prod) | The two code paths are completely different; branching prevents silent prod failure | Plan |
| Index schema | All S-01–S-04 fields declared now | Freezes the contract; S-03 fills nullable fields without a schema migration | Plan |
| Confirm flow | Copy to receipts → write search → delete staging | Staging blob remains as retry anchor; if search fails caller can retry with same `stagingBlobName` | Plan (revised) |
| receiptId | Derived from `stagingBlobName` last segment (the pre-created GUID) | Deterministic → retries are idempotent; no duplicate blobs in receipts container | Plan (revised) |
| Search write failure | SDK retry (MaxRetries=3) → log Error → staging blob preserved → return 500 | Caller can retry; orphaned receipts-container blob is accepted MVP risk if caller never retries | Plan (revised) |
| Staging delete failure | Log Warning, continue — non-fatal | Receipt is already confirmed; staging lifecycle rule (1 day) handles cleanup | Plan (revised) |
| Staging cleanup | Azure Blob lifecycle rule (1 day) via Terraform | Zero application code; handles abandoned uploads transparently | Plan |

## Scope

**In scope:**
- `POST /receipts/staging-slot` — write-only SAS URI for staging container
- `POST /receipts/confirm` — validate, move, index, return confirmation
- Angular `UploadComponent` at `/home/upload` (lazy-loaded)
- Azure AI Search index schema (all roadmap fields, frozen)
- Client-side + server-side file validation (20 MB, PNG/JPEG/WEBP/GIF — the formats the S-03 vision model accepts)

**Out of scope:**
- AI extraction / tag generation (S-03)
- Receipt list view (S-02)
- Logout, navigation bar, shell chrome (S-02)
- Terraform changes to storage/search resources (must already exist)
- Staging container lifecycle rule (one-time Terraform side-effect, done outside this plan)

## Architecture / Approach

```
Browser                   ASP.NET Core            Azure
──────                    ────────────            ─────
1. pick file (client validate)
2. POST /receipts/staging-slot ──────────────────► create empty blob in staging
                          ◄─────────────────────── { stagingUri, stagingBlobName }
3. PUT file ─────────────────────────────────────► staging/{userId}/{guid}  (SAS, bypasses MSAL interceptor)
4. POST /receipts/confirm ───────────────────────► validate blob props
                                                   copy to receipts/{userId}/{receiptId}  ← no extension; Content-Type + Content-Disposition set as blob headers
                                                   index ReceiptDocument (status: pending)
                                                   delete staging blob (non-fatal if fails)
                          ◄─────────────────────── { receiptId, fileName, fileSize }
5. show confirmation panel
```

MSAL interceptor handles Bearer token for steps 2 and 4 automatically. Step 3 uses
`fetch` directly to avoid sending a Bearer token to the Azure Storage SAS URI.

## Phases at a Glance

| Phase | What it delivers | Key risk |
|---|---|---|
| 1. Backend setup | Packages, DI, `ReceiptDocument` model, Azure AI Search index created at startup | Index initialization failure blocks startup — logged Critical |
| 2. Staging slot + blob upload | File picker, client validation, SAS fetch, direct blob PUT | SAS generation differs by credential type — `CanGenerateSasUri` branch is critical |
| 3. Confirm + index + UX | Confirm endpoint, AI Search write with retry, inline confirmation panel | Staging preserved on search failure → retry is possible; orphaned receipts blob if user never retries (accepted MVP risk) |

**Prerequisites:** F-01 complete (done); Azure Blob Storage account + staging/receipts containers exist; Azure AI Search instance exists; local developer has account key in user secrets and Search API key in user secrets.

**Estimated effort:** ~2-3 sessions across 3 phases.

## Open Risks & Assumptions

- Azure AI Search Free tier (1 index, 50 MB) is assumed sufficient for MVP. If the account is on Basic tier, ~$75/month applies.
- The server-side copy is download + re-upload (not a native Azure server-side copy). For ≤ 10 MB this is fine; revisit if file size limits increase.
- **Known limitation**: if the receipts-container copy succeeds but the search write fails persistently and the user never retries, an orphaned blob will remain in the receipts container indefinitely (no TTL). Accepted as an MVP edge case.
- The allowed file types (PNG, JPEG, WEBP, non-animated GIF) and the 20 MB cap are dictated by the S-03 vision model's image-input limits, not by storage. Accepting other types would store receipts that S-03 cannot extract. Animated-GIF rejection is deferred to S-03.

## Success Criteria (Summary)

- Authenticated user uploads a JPEG and sees filename + formatted size in a confirmation panel — no manual data entry, under 30 seconds.
- Backend has the receipt blob in the receipts container and a `ReceiptDocument` in Azure AI Search with `status: "pending"`.
- All error paths (invalid file, network failure, search failure) show a meaningful error in the UI and log at Error in the backend.
