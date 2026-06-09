# Receipt Upload Confirmation — Implementation Plan

## Overview

Implement the S-01 receipt upload flow using a SAS-token staging pattern: backend
issues a write-only SAS for a quarantine blob → frontend uploads directly to Azure
Blob Storage → backend confirms (validates, moves to permanent container, indexes in
Azure AI Search). This slice establishes the Receipt data contract (all S-01 through
S-04 index fields frozen here) — downstream slices consume this schema without
migration.

## Current State Analysis

- **Backend**: `Program.cs` only — no domain models, no storage endpoints. `Azure.Identity` and the data-protection Blob extension are already installed, giving `DefaultAzureCredential` as the production auth baseline.
- **Frontend**: `LandingComponent` + `ShellComponent` with `children: []` waiting for the first feature route. `MsalInterceptor` is provided but **not yet registered under `HTTP_INTERCEPTORS`** — this slice makes the first authenticated API call, so Phase 2 must add that registration before tokens attach (see Phase 2 #0).
- **Data layer**: entirely absent — no `Azure.Storage.Blobs` for receipts, no `Azure.Search.Documents`, no receipt model.
- **Config**: `AzureStorage:AccountName` and `AzureStorage:KeyRingContainerName` exist for data protection. No receipt-specific keys yet.

## Desired End State

Authenticated user navigates to `/home` → redirected to `/home/upload` → selects a PNG, JPEG, WEBP, or (non-animated) GIF photo (≤ 20 MB) → upload form transitions to a confirmation panel showing filename and formatted file size. The backend has stored the photo in the receipts container under `{userId}/{receiptId}.{ext}` and indexed a `ReceiptDocument` (status: `pending`) in Azure AI Search. No AI extraction runs in this slice.

### Key Discoveries

- `provideHttpClient(withInterceptorsFromDi())` is set up (`app.config.ts:24`) and `MsalInterceptor` is listed as a provider (`app.config.ts:57`), BUT it is **not** registered under the `HTTP_INTERCEPTORS` multi-token — so it is injectable yet not in the request chain. Until that registration is added (Phase 2 #0), HTTP calls to `environment.apiUrl` do **not** carry Bearer tokens and the backend FallbackPolicy returns 401. The `protectedResourceMap` (`app.config.ts:51`) is already correct; only the `HTTP_INTERCEPTORS` registration is missing.
- Central package management is ON (`src/backend/Directory.Packages.props`) — version goes only in `Directory.Packages.props`, not in `.csproj`.
- `UserSecretsId: receipt-well-api` is set in `ReceiptWell.csproj` — local dev credentials (connection string, Search API key) go in user secrets, not in any tracked file.
- `BlobServiceClient.CanGenerateSasUri` is `true` when constructed from a connection string (SharedKeyCredential, local dev) and `false` with `DefaultAzureCredential` (managed identity, production). SAS generation takes entirely different code paths for each — see Critical Implementation Details.
- Shell route at `app.routes.ts` currently has `children: []` — this plan adds the first two child entries.

## What We're NOT Doing

- No AI extraction or tag generation — S-03.
- No receipt list view — S-02.
- No logout or navigation bar — deferred to S-02.
- No upload progress percentage — the MSAL interceptor does not expose upload progress; bypassing it for streaming adds complexity out of scope for S-01.
- No Terraform changes to the Azure AI Search or Blob Storage resources themselves — those resources must already exist. This plan only wires the application layer.
- No staging container lifecycle rule in application code — that is a one-time Terraform side-effect (delete blobs older than 1 day on the staging container) done outside this plan.

## Implementation Approach

Three sequentially verifiable phases. Each ends with a hard manual gate before the next begins:

1. **Backend setup** — packages, DI, config keys, `ReceiptDocument` model, Azure AI Search index schema + startup initializer. No user-facing behavior; verified by build + index visible in Azure portal.
2. **Staging slot + blob upload** — `POST /receipts/staging-slot` backend endpoint + Angular `UploadComponent` that validates a file client-side and PUTs it directly to the SAS URI. Verified by finding the staging blob in Azure Storage.
3. **Confirm + index + confirmation UX** — `POST /receipts/confirm` backend endpoint (validates staging blob, moves it to the receipts container, writes to Azure AI Search with SDK retry) + Angular completes the state machine and shows the inline confirmation panel.

## Critical Implementation Details

**SAS token generation branches on credential type.** `BlobServiceClient.CanGenerateSasUri` is `true` when constructed with a connection string (local dev) and `false` with `DefaultAzureCredential` (production). The `ReceiptBlobService.CreateStagingSlotAsync` must branch on this flag — using the account-key path in production silently fails, and using the user-delegation path locally throws:

```csharp
if (_blobServiceClient.CanGenerateSasUri)
{
    // Account key (local dev) — generate SAS directly
    return stagingBlobClient.GenerateSasUri(sasBuilder);
}
else
{
    // Managed identity (production) — requires user delegation key
    var key = await _blobServiceClient.GetUserDelegationKeyAsync(
        DateTimeOffset.UtcNow.AddMinutes(-5), sasExpiry);
    var queryParams = sasBuilder.ToSasQueryParameters(
        key, _blobServiceClient.AccountName);
    return new BlobUriBuilder(stagingBlobClient.Uri) { Sas = queryParams }.ToUri();
}
```

**Server-side blob move = download + re-upload.** `SyncCopyFromUriAsync` requires a public or SAS-signed source URI even within the same storage account. For ≤ 10 MB receipts, downloading the staging blob content and re-uploading to the receipts container is simpler and sufficient. No need to generate an internal read SAS.

**Every 500 response must log at Error.** Any `catch` block that results in a 500 being returned to the client must call the source-generated logger at `Error` level before returning — not just the orphaned-blob case. This applies to both endpoints.

**Angular upload must bypass the MSAL interceptor.** The SAS URI is a direct Azure Storage URL, not `environment.apiUrl`. Sending a Bearer token to it causes a 400 from Azure Storage. Use `fetch` (not `HttpClient`) for the PUT to the SAS URI.

---

## Phase 1: Backend Setup — Packages, DI, Model, Index Schema

### Overview

Add `Azure.Storage.Blobs` and `Azure.Search.Documents`, register both clients in DI (with the connection-string vs managed-identity branch for Blob), define `ReceiptDocument` with all fields the full roadmap needs (frozen contract for S-01 through S-04), and ensure the Azure AI Search index exists at startup via a hosted initializer.

### Changes Required

#### 1. NuGet package versions

**File**: `src/backend/Directory.Packages.props`

**Intent**: Declare versions for the two new packages so downstream `.csproj` references compile.

**Contract**: Add `<PackageVersion>` entries for `Azure.Storage.Blobs` and `Azure.Search.Documents` at their latest stable versions inside the existing `<ItemGroup>`.

#### 2. Project package references

**File**: `src/backend/ReceiptWell.csproj`

**Intent**: Reference both new packages (no `Version` attribute — managed centrally).

**Contract**: Add `<PackageReference Include="Azure.Storage.Blobs" />` and `<PackageReference Include="Azure.Search.Documents" />`.

#### 3. Config keys

**File**: `src/backend/appsettings.json`

**Intent**: Declare structural keys with empty defaults so the schema is visible in version control without embedding resource names or secrets.

**Contract**: Add the following keys (all empty strings):
- `AzureStorage:BlobServiceUri`
- `AzureStorage:ConnectionString` — overridden via user secrets for local dev; empty in all tracked files
- `AzureStorage:StagingContainerName`
- `AzureStorage:ReceiptsContainerName`
- `AzureSearch:ServiceUri`
- `AzureSearch:IndexName`
- `AzureSearch:ApiKey` — overridden via user secrets / App Service env; empty in all tracked files

#### 4. Receipt document model

**File**: `src/backend/Models/ReceiptDocument.cs` (new)

**Intent**: Define the C# class that maps to the Azure AI Search index document. All fields for S-01 through S-04 are declared now to freeze the index schema — S-03 fills the nullable fields without a schema migration.

**Contract**: Class `ReceiptDocument` in namespace `ReceiptWell.Models`, with Azure Search field attributes:

| Property | Type | Attributes | Notes |
|---|---|---|---|
| `Id` | `string` | Key | receiptId as GUID string |
| `UserId` | `string` | Filterable | `oid` claim value |
| `BlobUrl` | `string` | — | URL in receipts container |
| `FileName` | `string` | — | Original filename |
| `FileSize` | `long` | Filterable | Bytes |
| `Status` | `string` | Filterable | `"pending"` / `"ready"` |
| `UploadedAt` | `DateTimeOffset` | Filterable, Sortable | Upload timestamp |
| `StoreName` | `string?` | Searchable | Set by S-03 |
| `PurchaseDate` | `DateTimeOffset?` | Filterable, Sortable | Set by S-03 |
| `Tags` | `IList<string>` | Searchable, Filterable | Set by S-03; searched by S-04 |

Use `[SimpleField]` and `[SearchableField]` attributes from `Azure.Search.Documents.Indexes.Attributes`.

#### 5. Azure AI Search index initializer

**File**: `src/backend/Services/SearchIndexInitializer.cs` (new)

**Intent**: `IHostedService` that ensures the index exists at startup. Idempotent — `CreateOrUpdateIndexAsync` is a no-op when the schema is unchanged; adding nullable fields is a non-breaking update.

**Contract**: `SearchIndexInitializer(SearchIndexClient, IConfiguration, ILogger<SearchIndexInitializer>)`. In `StartAsync`: build a `SearchIndex` using the configured index name and `FieldBuilder.Build<ReceiptDocument>()`. Call `CreateOrUpdateIndexAsync`. On success: log Information. On failure: log Critical and rethrow (startup fails loudly rather than silently serving a broken app).

#### 6. DI registration

**File**: `src/backend/Program.cs`

**Intent**: Register `BlobServiceClient` with the credential branch, `SearchIndexClient`, `SearchClient` (with SDK retry), and `SearchIndexInitializer`.

**Contract**:
- `BlobServiceClient` (singleton): if `AzureStorage:ConnectionString` is non-empty, construct with the connection string; otherwise construct with `new Uri(config["AzureStorage:BlobServiceUri"]!)` and `new DefaultAzureCredential()`.
- `SearchIndexClient` (singleton): `new SearchIndexClient(new Uri(config["AzureSearch:ServiceUri"]!), new AzureKeyCredential(config["AzureSearch:ApiKey"]!))`.
- `SearchClient` (singleton): same URI + credential + index name, with `new SearchClientOptions { Retry = { MaxRetries = 3 } }` to enable SDK-level retry.
- `SearchIndexInitializer`: `AddHostedService<SearchIndexInitializer>()`.

### Success Criteria

#### Automated Verification

- Backend builds without warnings: `dotnet build src/backend/ReceiptWell.csproj`
- `dotnet restore` updates `packages.lock.json` without errors

#### Manual Verification

- Application starts with valid local user secrets and logs Information for index initialization
- Azure AI Search index visible in Azure portal with all expected fields
- `GET /health` still returns 200

**Pause here for manual confirmation before proceeding to Phase 2.**

---

## Phase 2: Staging Slot + Blob Upload

### Overview

Implement `POST /receipts/staging-slot` (creates an empty named blob in the staging container, returns a **write-only** SAS URI valid for 10 minutes) and the Angular `UploadComponent` that validates a file client-side and PUTs it directly to the SAS URI. Azure AI Search is not touched in this phase — verified by confirming the blob appears in the staging container.

### Changes Required

#### 0. Register MsalInterceptor in the HTTP pipeline

**File**: `src/frontend/src/app/app.config.ts`

**Intent**: `MsalInterceptor` is currently provided but never added to the `HTTP_INTERCEPTORS` chain, so `withInterceptorsFromDi()` does not run it and outgoing requests carry no Bearer token. Without this, every call from the new `ReceiptService` to `environment.apiUrl` returns 401. This is a prerequisite for everything else in Phase 2.

**Contract**: Add to the `providers` array:

```typescript
{ provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true }
```

Import `HTTP_INTERCEPTORS` from `@angular/common/http`. Keep the existing bare `MsalInterceptor` provider (it remains the implementation registered above). Verify by signing in and confirming a request to `environment.apiUrl` carries an `Authorization: Bearer` header in DevTools → Network.

#### 1. Receipt blob service

**File**: `src/backend/Services/ReceiptBlobService.cs` (new)

**Intent**: Encapsulate staging slot creation. `CreateStagingSlotAsync(string userId)` creates a randomly-named blob under `{userId}/{Guid.NewGuid()}` in the staging container, creates it as empty, generates a **write-only** SAS URI valid for 10 minutes, and returns `(Uri SasUri, string StagingBlobName)`.

**Contract**:
- Staging blob name: `$"{userId}/{Guid.NewGuid()}"` (no extension — content type is set by the frontend PUT).
- Create the blob as empty before returning the SAS: `await stagingBlobClient.UploadAsync(BinaryData.Empty, overwrite: false)`. This pre-create is load-bearing — the SAS grants `Write` only (not `Create`), so the client can write into this server-created blob but cannot create arbitrary blobs in the container.
- SAS builder: `Resource = "b"`, `ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(10)`, permissions `Write` only.
- Branch on `_blobServiceClient.CanGenerateSasUri` — see Critical Implementation Details.
- Source-generated log at Information: `"Staging slot created: user {UserId} blob {StagingBlobName}"`.

#### 2. Staging-slot endpoint

**File**: `src/backend/Program.cs`

**Intent**: `POST /receipts/staging-slot` — extracts `oid` from the JWT, calls `ReceiptBlobService.CreateStagingSlotAsync`, returns the SAS URI and staging blob name.

**Contract**:
- Extract userId: `httpContext.User.FindFirstValue("oid") ?? throw new InvalidOperationException("oid claim missing")`.
- Response (200): `{ stagingUri: string, stagingBlobName: string }` (camelCase JSON).
- Catch block: log Error + return `Results.Problem(statusCode: 500)`.
- Register `ReceiptBlobService` in DI as scoped before this endpoint.

#### 3. HTTP examples

**File**: `receipt-well.http`

**Intent**: Add an exercisable example for the new endpoint.

**Contract**: Add `POST {{receipt_well_HostAddress}}/receipts/staging-slot` with `Authorization: Bearer {{token}}` and `Content-Type: application/json`.

#### 4. Angular receipt service — staging + upload

**File**: `src/frontend/src/app/receipts/receipt.service.ts` (new)

**Intent**: `ReceiptService` (`providedIn: 'root'`) with `getStagingSlot()` and `uploadToBlob(sasUri, file)`. The blob PUT bypasses `HttpClient` to avoid the MSAL interceptor adding a Bearer token to the external SAS URI.

**Contract**:
- `getStagingSlot()`: `HttpClient.post<{ stagingUri: string; stagingBlobName: string }>(environment.apiUrl + '/receipts/staging-slot', {})` — returns `Observable`.
- `uploadToBlob(sasUri: string, file: File): Promise<void>`: `fetch(sasUri, { method: 'PUT', headers: { 'Content-Type': file.type, 'x-ms-blob-type': 'BlockBlob' }, body: file })`. Throw on non-2xx.

#### 5. Upload component

**File**: `src/frontend/src/app/receipts/upload/upload.component.ts` (new)

**Intent**: Standalone lazy-loaded component. Reactive form with file input — validates size (≤ 20,000,000 bytes) and MIME type on file selection. On submit: calls `getStagingSlot()` → `uploadToBlob()`. Drives a signal-based state machine; Phase 3 completes it.

**Allowed file types are dictated by the S-03 vision model** (GPT image input accepts only PNG, JPEG, WEBP, and non-animated GIF, ≤ 20 MB). Accepting anything else here would store a receipt that S-03 cannot extract. This is the binding allowlist for the whole upload pipeline — client validation, server validation, and extension mapping must all use it.

**Contract**:
- `ChangeDetectionStrategy.OnPush`, no `standalone: true` (default in Angular v20+).
- File input: `accept=".png,.jpg,.jpeg,.webp,.gif"`, `capture="environment"`.
- State signal: `type UploadState = 'idle' | 'uploading' | 'confirmed' | 'error'` — Phase 2 only reaches `uploading`.
- Client-side validation on file selection: allowed MIME types `image/png`, `image/jpeg`, `image/webp`, `image/gif`; size ≤ 20,000,000 bytes. Show inline error via `role="alert"` span.
- Animated-GIF rejection is **not** enforced in S-01 (frame inspection is out of scope); it is the S-03 extraction step's concern. Document this as a known limitation.
- During `uploading`: disable submit button, show spinner.
- AXE-compliant: file input has an associated `<label>`.

#### 6. Route wiring

**File**: `src/frontend/src/app/app.routes.ts`

**Intent**: Add two child routes to the `/home` shell: an empty-path redirect to `upload` and a lazy-loaded `upload` child.

**Contract**: Replace `children: []` on the `home` route with:

```typescript
children: [
  { path: '', redirectTo: 'upload', pathMatch: 'full' },
  {
    path: 'upload',
    loadComponent: () =>
      import('./receipts/upload/upload.component').then(m => m.UploadComponent)
  }
]
```

### Success Criteria

#### Automated Verification

- Backend builds: `dotnet build src/backend/ReceiptWell.csproj`
- Frontend builds: `ng build` (from `src/frontend/`)
- Unit tests pass: `ng test`

#### Manual Verification

- `/home` redirects to `/home/upload` and shows the upload form
- Selecting a file > 10 MB shows an inline validation error; no network call is made
- Selecting an invalid type (e.g., `.pdf`) shows an inline validation error; no network call
- Authenticated request to `environment.apiUrl` carries an `Authorization: Bearer` header (DevTools → Network) — confirms the interceptor is in the chain
- Selecting a valid JPEG and submitting → spinner appears; blob visible in the staging container in Azure Storage Explorer or portal
- `POST /receipts/staging-slot` without a token returns 401

**Pause here for manual confirmation before proceeding to Phase 3.**

---

## Phase 3: Confirm + Azure AI Search + Confirmation UX

### Overview

Implement `POST /receipts/confirm` (validates ownership + blob properties in staging, moves the file to the receipts container via download + re-upload, writes the `ReceiptDocument` to Azure AI Search with SDK retry, logs Error on every 500 path) and complete the Angular state machine (confirm call after blob upload, inline confirmation panel on success, error state on failure).

### Changes Required

#### 1. Receipt confirm service

**File**: `src/backend/Services/ReceiptConfirmService.cs` (new)

**Intent**: `ConfirmUploadAsync(string stagingBlobName, string userId, string originalFileName)` — ownership check, blob property validation, move to receipts container, Azure AI Search index write, return confirmation data.

**Contract**:
- **Ownership check**: `stagingBlobName.StartsWith($"{userId}/", StringComparison.Ordinal)`. If false: log Warning + return a result indicating 403 (prevents a user confirming another user's staging blob).
- **Blob properties**: `GetPropertiesAsync()` on the staging blob. Validate `ContentType` ∈ `{ "image/png", "image/jpeg", "image/webp", "image/gif" }` and `ContentLength ≤ 20_000_000`. On invalid: log Information (validation failure) + return 400 result.
- **Extension**: map ContentType → `.png` / `.jpg` / `.webp` / `.gif`. Fallback: `Path.GetExtension(originalFileName)`.
- **Move**: `DownloadContentAsync()` on the staging blob; `UploadAsync()` to `{userId}/{receiptId}{ext}` in the receipts container with `BlobHttpHeaders { ContentType = contentType }`. Delete staging blob after successful upload.
- **Index write**: `SearchClient.MergeOrUploadDocumentsAsync(new[] { receiptDocument })`. SDK retries up to 3 times (configured at DI registration in Phase 1). On `RequestFailedException` after retries: log Error (including `receiptId` + target blob URL) + rethrow.
- **Return**: `record ReceiptConfirmResult(string ReceiptId, string FileName, long FileSize)` on success.
- Source-generated log at Information on success: `"Receipt confirmed: user {UserId} receiptId {ReceiptId}"`.

#### 2. Confirm endpoint

**File**: `src/backend/Program.cs`

**Intent**: `POST /receipts/confirm` — extracts `oid`, deserializes request body, calls `ReceiptConfirmService`, maps service results to HTTP responses.

**Contract**:
- Request body: `{ stagingBlobName: string, originalFileName: string }`.
- On ownership violation: `Results.Forbid()`.
- On validation failure: `Results.ValidationProblem(...)`.
- On success: `Results.Ok(new { receiptId, fileName, fileSize })`.
- Catch block (unhandled exception): log Error + `Results.Problem(statusCode: 500)`.
- Register `ReceiptConfirmService` in DI as scoped.

#### 3. HTTP examples

**File**: `receipt-well.http`

**Intent**: Add an exercisable example for the confirm endpoint.

**Contract**: Add `POST {{receipt_well_HostAddress}}/receipts/confirm` with `Authorization: Bearer {{token}}`, `Content-Type: application/json`, and body `{ "stagingBlobName": "", "originalFileName": "" }`.

#### 4. Angular receipt service — confirm

**File**: `src/frontend/src/app/receipts/receipt.service.ts`

**Intent**: Add `confirmUpload(stagingBlobName: string, originalFileName: string)` to `ReceiptService`.

**Contract**: `HttpClient.post<{ receiptId: string; fileName: string; fileSize: number }>(environment.apiUrl + '/receipts/confirm', { stagingBlobName, originalFileName })` — returns `Observable`. Errors propagate to the component.

#### 5. Upload component — complete state machine

**File**: `src/frontend/src/app/receipts/upload/upload.component.ts`

**Intent**: Wire the confirm call after blob upload and complete the `confirmed` / `error` state rendering.

**Contract**:
- After `uploadToBlob` resolves: call `confirmUpload(stagingBlobName, file.name)`.
- `confirmedReceipt` signal: `{ fileName: string; fileSize: number } | null` — set from the confirm response.
- `errorMessage` signal: `string | null` — set on any error (getStagingSlot, uploadToBlob, or confirmUpload failure).
- Confirmed state: show `fileName` and formatted `fileSize` (`computed()` — bytes → `"X.X MB"` / `"X KB"`), plus an "Upload another" button that calls `reset()` (clears form + signals, transitions back to `idle`).
- Error state: show `errorMessage` + a "Try again" button that calls `reset()`.
- All error paths (any of the three async calls) transition to `error`.

### Success Criteria

#### Automated Verification

- Backend builds: `dotnet build src/backend/ReceiptWell.csproj`
- Frontend builds: `ng build` (from `src/frontend/`)
- Unit tests pass: `ng test`

#### Manual Verification

- Full flow: select a valid JPEG → spinner → confirmation panel shows filename and formatted file size
- Azure Storage: blob at `{userId}/{receiptId}.jpg` in receipts container; no blob remains in staging container
- Azure AI Search portal → index → Documents: receipt document present with `status: "pending"`, correct `userId`, `fileName`, `fileSize`, `uploadedAt`
- Simulated failure (temporarily invalid Search API key): error state shown in Angular UI; Error log appears in backend console
- "Upload another" resets form to idle
- Mobile (or DevTools device emulation): file input shows camera/gallery picker

---

## Testing Strategy

### Unit Tests

- `upload.component.spec.ts`: client-side validation — rejects file > 10 MB, rejects disallowed MIME type, accepts valid JPEG/PNG/HEIC. Mock `ReceiptService`; no HTTP in unit tests.

### Manual Testing Steps

1. `dotnet run --project src/backend` + `ng serve` (from `src/frontend/`)
2. Sign in → lands on `/home/upload`
3. Select invalid type → inline error, no request
4. Select file > 10 MB → inline error, no request
5. Select valid JPEG → confirm panel with filename and size
6. Azure Storage Explorer → blob in receipts container, staging empty
7. Azure portal → AI Search index → verify receipt document fields
8. Temporarily break Search API key → upload → error state in UI + Error in backend log
9. "Upload another" → form resets

## Performance Considerations

File bytes travel client → Azure Blob (SAS PUT) → backend download → backend re-upload to receipts. The double-touch adds latency proportional to file size. For ≤ 10 MB with a single MVP user this is acceptable. A future optimization is a server-side copy using a short-lived internal read SAS on the staging blob.

## Migration Notes

The Azure AI Search index schema (all fields) is created idempotently at Phase 1 startup. S-03 merges `storeName`, `purchaseDate`, and `tags` into existing documents via `MergeOrUploadDocuments` — no index schema migration needed, as those fields are already declared nullable.

## References

- Roadmap: `context/foundation/roadmap.md` §S-01
- PRD: `context/foundation/prd.md` §FR-002, §NFR, §Guardrails
- F-01 plan (completed): `context/changes/complete-auth-gate/plan.md`
- Backend: `src/backend/Program.cs`
- Frontend routes: `src/frontend/src/app/app.routes.ts`
- NuGet central versioning: `src/backend/Directory.Packages.props`

---

## Progress

> Convention: `- [ ]` pending, `- [x]` done. Append ` — <commit sha>` when a step lands. Do not rename step titles. See `references/progress-format.md`.

### Phase 1: Backend Setup — Packages, DI, Model, Index Schema

#### Automated

- [ ] 1.1 Backend builds without warnings: `dotnet build src/backend/ReceiptWell.csproj`
- [ ] 1.2 `dotnet restore` updates `packages.lock.json` without errors

#### Manual

- [ ] 1.3 Application starts and logs Information for index initialization
- [ ] 1.4 Azure AI Search index visible in portal with all expected fields
- [ ] 1.5 `GET /health` returns 200

### Phase 2: Staging Slot + Blob Upload

#### Automated

- [ ] 2.1 Backend builds: `dotnet build src/backend/ReceiptWell.csproj`
- [ ] 2.2 Frontend builds: `ng build` (from `src/frontend/`)
- [ ] 2.3 Unit tests pass: `ng test`

#### Manual

- [ ] 2.4 `/home` redirects to `/home/upload` and shows upload form
- [ ] 2.5 File > 10 MB shows inline validation error without a network call
- [ ] 2.6 Invalid file type shows inline validation error without a network call
- [ ] 2.7 Valid JPEG submit → staging blob appears in Azure Storage
- [ ] 2.8 Authenticated request to `environment.apiUrl` carries an `Authorization: Bearer` header

### Phase 3: Confirm + Azure AI Search + Confirmation UX

#### Automated

- [ ] 3.1 Backend builds: `dotnet build src/backend/ReceiptWell.csproj`
- [ ] 3.2 Frontend builds: `ng build` (from `src/frontend/`)
- [ ] 3.3 Unit tests pass: `ng test`

#### Manual

- [ ] 3.4 Full flow: valid JPEG → spinner → confirmation panel with filename and size
- [ ] 3.5 Receipt blob at `{userId}/{receiptId}.jpg` in receipts container
- [ ] 3.6 No blob remains in staging container after confirm
- [ ] 3.7 Receipt document in Azure AI Search with `status: "pending"` and correct fields
- [ ] 3.8 Simulated failure → error state in Angular UI + Error in backend console
- [ ] 3.9 "Upload another" resets form to idle
- [ ] 3.10 Mobile emulation: file input shows camera/gallery picker
