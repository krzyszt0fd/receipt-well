# Lessons Learned

## JwtBearer: set MapInboundClaims = false or JWT claim names won't resolve

- **Context**: `Program.cs` — `AddJwtBearer` configuration; applies to every endpoint that reads claims by name
- **Problem**: ASP.NET Core JwtBearer defaults to `MapInboundClaims = true`, which rewrites JWT claim names to legacy .NET URI types (e.g. `oid` → `http://schemas.microsoft.com/identity/claims/objectidentifier`). `FindFirstValue("oid")` returns null even when the claim is present in the token.
- **Rule**: Always set `options.MapInboundClaims = false` in `AddJwtBearer`. Use short JWT claim names (`oid`, `sub`, `name`, etc.) everywhere in endpoint code.
- **Applies to**: backend auth setup, any endpoint using `FindFirstValue` or `FindFirst`

## MSAL Angular v5: strict pathname matching requires `/*` wildcard in protectedResourceMap

- **Context**: `app.config.ts` — any phase that configures `MSAL_INTERCEPTOR_CONFIG.protectedResourceMap`
- **Problem**: MSAL Angular v5 changed URL matching to strict anchored regex (`^pattern$` per URL component). A key of `https://host:port` has `pathname = "/"` which produces regex `^/$` — only matching the root, not `/api/endpoint`. All API calls return 401 because the interceptor skips them.
- **Rule**: Always use a wildcard suffix in the map key: `${environment.apiUrl}/*` (not just `${environment.apiUrl}`). In strict mode, `/*` compiles to `^\/.*$`, matching all sub-paths.
- **Applies to**: app config, environment-specific protectedResourceMap setup

## MSAL interceptor requires active account to be set

- **Context**: `App.ngOnInit()` — must subscribe to `broadcastService.inProgress$` and call `instance.setActiveAccount(accounts[0])` on `InteractionStatus.None`
- **Problem**: `MsalInterceptor` calls `instance.getActiveAccount()` first; without it set, no silent token acquisition runs and Bearer header is omitted.
- **Rule**: In `App.ngOnInit()`, subscribe to `inProgress$` filtered to `None`, and if no active account is set but accounts exist in cache, call `setActiveAccount(accounts[0])`.
- **Applies to**: app bootstrap, auth configuration

> Append-only register of recurring rules and patterns. Re-read at start by /10x-frame, /10x-research, /10x-plan, /10x-plan-review, /10x-implement, /10x-impl-review.

## Use GUID-based ciamlogin.com authority for Entra External ID — both frontend and backend

- **Context**: Any config that sets an authority/issuer for Entra External ID (CIAM) — frontend `environment.*.ts`, backend `AzureExternalId:Authority` user secret, and any IaC that wires auth.
- **Problem**: CIAM tokens carry `iss = https://{tenant-id}.ciamlogin.com/{tenant-id}/v2.0`. If the backend `Authority` is set to a `login.microsoftonline.com` URL or a friendly-name CIAM URL, the JwtBearer middleware fetches the wrong discovery document, discovers a different issuer, and rejects every token with "issuer is invalid". Consumer users can't call the API; internal Entra accounts still work, hiding the misconfiguration.
- **Rule**: For both frontend MSAL and backend JwtBearer, always use `https://{tenant-id}.ciamlogin.com/{tenant-id}/v2.0` as the authority/issuer. Backend value goes in `dotnet user-secrets set "AzureExternalId:Authority" "https://{tenant-id}.ciamlogin.com/{tenant-id}/v2.0"`. Frontend value goes in `environment.local.ts` (already set). After changing user secrets, restart the backend — JwtBearer caches the discovery doc at startup.
- **Applies to**: environment setup, backend user secrets, provisioning

## Infrastructure provisioning: verify resources exist before calling them out-of-scope in a plan

- **Context**: `infra/*.tf` — any plan phase that depends on cloud infrastructure (Azure AI Search, Blob Storage, App Service, etc.)
- **Problem**: Plan stated "No Terraform changes — those resources must already exist." Resources didn't exist at implementation time; provisioning landed in an unplanned commit (8d81356) without documentation, causing a scope discipline violation in review.
- **Rule**: Before writing "What We're NOT Doing: no Terraform changes", verify the target resources actually exist. If they don't, scope the Terraform provisioning explicitly in the plan — include which resources to create, in which .tf file, and what App Service env wiring is needed.
- **Applies to**: /10x-plan (when authoring any plan that depends on cloud infrastructure); /10x-plan-review — flag if a plan says "resources must already exist" without evidence they do.

## 400 errors are not retriable — distinguish validation failures from transient errors in the UI

- **Context**: Any UI component that calls a backend API and shows an error state with a recovery action.
- **Problem**: A generic "Try again" button is correct for transient failures (network errors, 5xx) but wrong for 400s. A 400 means the request itself is invalid; retrying it without changing the input will always fail again. Showing "Try again" traps the user in an unrecoverable loop.
- **Rule**: On a 400 response, show an action that resets the form or starts over (e.g. "Try a different file", "Go back") rather than retrying the same request. Extract and display the error message from the RFC 7807 Problem Details body (https://datatracker.ietf.org/doc/html/rfc7807): prefer `detail`, then the first entry in `errors` (field-level messages, e.g. ASP.NET Core `ValidationProblemDetails`), then `title`. On 5xx or network failure, keep the retriable error path.
- **Applies to**: every UI component that handles API errors; plan the retriable vs. non-retriable error states and their recovery actions upfront in the component contract.

## Azure AI Search: MergeOrUpload with a full POCO clobbers defaulted fields — use a partial-merge DTO

- **Context**: Any write path that calls `SearchClient.MergeOrUploadDocumentsAsync` to update *some* fields of an existing document (e.g. rename `FileName`, set `Status`, write AI-extracted fields).
- **Problem**: Azure AI Search "merge" overwrites **every field present in the serialized payload**, not just the ones you meaningfully set. Passing a full `ReceiptDocument` with only `Id` + `FileName` assigned still serializes the other properties at their defaults (`UserId=""`, `Status=""`, `FileSize=0`, `Tags=[]`, …), so the merge silently clobbers them. Wiping `UserId` drops the document out of the owner's `UserId eq '{oid}'` query on the next read — the update appears "not to persist". Mock-based tests do **not** catch this: they capture the in-memory object (whose unset fields look empty) and never exercise real merge serialization.
- **Rule**: For a partial merge, send a dedicated DTO that has **only** the fields to update (mirror `ReceiptWell.Functions`' `ReceiptEnrichmentDocument`/`ReceiptStatusDocument`). Never round-trip or re-shape the full index model for a partial write. In tests, capture the merged argument as that DTO type so the type itself guarantees no extra field is sent.
- **Applies to**: any `/10x-plan` or implementation touching `MergeOrUploadDocumentsAsync`; `/10x-impl-review` — flag a partial merge that passes the full index model instead of a fields-only DTO.

## Azure AI Search: wildcard queries bypass the field's language analyzer

- **Context**: `ReceiptQueryService.cs` — any Azure AI Search query path that uses a language analyzer (e.g. `pl.microsoft`, `en.microsoft`) on a field for lemmatization/inflection matching.
- **Problem**: Setting `QueryType = SearchQueryType.Full` and appending `*` to the search term (Lucene wildcard syntax) disables the field's language analyzer at query time. The query token is matched literally as a prefix against pre-analyzed tokens in the index. So typing "rowery" sends "rowery*", which does not match the indexed lemma "rower" — only typing the exact base form works. The wildcard approach is not "prefix + stemming" — it is prefix only.
- **Rule**: For language-analyzer fields, use plain `searchText = term` with `QueryType.Simple` (the default). The service applies the field's registered analyzer to both the document at index time and the query term at search time, enabling true inflection matching (e.g. "rowery" → "rower" via `pl.microsoft`). Only use `QueryType.Full` and wildcards when you explicitly want prefix matching without analyzer involvement, and document that tradeoff.
- **Applies to**: any plan or implementation that uses Azure AI Search with a language analyzer field; any `/10x-plan` or `/10x-impl-review` touching `SearchQueryType`, `QueryType.Full`, or wildcard (`*`) query construction.
