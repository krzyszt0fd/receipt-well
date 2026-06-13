<!-- IMPL-REVIEW-REPORT -->
# Implementation Review: Receipt Upload Confirmation Flow

- **Plan**: context/changes/receipt-upload-confirm/plan.md
- **Scope**: All Phases (1–3 of 3)
- **Date**: 2026-06-13
- **Verdict**: NEEDS ATTENTION
- **Findings**: 0 critical  2 warnings  3 observations

## Verdicts

| Dimension | Verdict |
|-----------|---------|
| Plan Adherence | PASS |
| Scope Discipline | WARNING |
| Safety & Quality | WARNING |
| Architecture | PASS |
| Pattern Consistency | WARNING |
| Success Criteria | PASS |

## Findings

### F1 — Terraform infrastructure created, contradicting explicit scope guardrail

- **Severity**: ⚠️ WARNING
- **Impact**: 🔬 HIGH — architectural stakes; think carefully before deciding
- **Dimension**: Scope Discipline
- **Location**: infra/search.tf, infra/storage.tf, infra/app_service.tf, infra/outputs.tf, infra/variables.tf
- **Detail**: Plan's "What We're NOT Doing" stated explicitly: "No Terraform changes to the Azure AI Search or Blob Storage resources themselves — those resources must already exist. This plan only wires the application layer." Five infra files were added (commit 8d81356), creating azurerm_search_service, azurerm_storage_account, three storage containers, a blob lifecycle management policy (1-day TTL on staging — which the plan DID reference as a Terraform side-effect), and App Service environment variable wiring for the new services. The plan's assumption that resources pre-existed was incorrect; provisioning them here was necessary.
- **Fix A ⭐ Recommended**: Document as an addendum to the plan — update "What We're NOT Doing" to note that resources were provisioned as part of this slice (commit 8d81356). Blind spot: verify infra files don't contain hosting secrets (resource names, subscription IDs) — project CLAUDE.md prohibits these in tracked files.
  - Strength: Keeps plan as source of truth; no rework; future reviewers understand the context.
  - Tradeoff: Plan's scope constraint was violated; addendum normalizes that.
  - Confidence: HIGH — infra is functionally correct and necessary.
- **Fix B**: Extract infra into a separate context/changes/infra-setup change — retroactive split with no safety benefit at MVP.
  - Confidence: LOW.
- **Decision**: FIXED via Fix A + ACCEPTED-AS-RULE: "Infrastructure provisioning: verify resources exist before calling them out-of-scope in a plan"

### F2 — Content-Type validation trusts client-declared MIME type (honor-system check)

- **Severity**: ⚠️ WARNING
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: src/backend/Services/ReceiptConfirmService.cs:55–65
- **Detail**: The ContentType checked in ConfirmUploadAsync is the value the client wrote during the SAS PUT — the client controls it entirely. A user can PUT an executable with Content-Type: image/jpeg and pass the MIME validation. The backend never inspects magic bytes. The plan accepted this implicitly (no magic-byte check was specified). S-03's vision model provides a downstream defense.
- **Fix A ⭐ Recommended**: Accept as MVP risk — add a comment in ConfirmUploadAsync noting the client-controlled Content-Type limitation. S-03 rejects non-image content anyway.
  - Strength: Zero implementation cost; S-03 provides a second gate.
  - Tradeoff: A malicious client can store arbitrary bytes in receipts container with a misleading MIME type.
  - Confidence: HIGH — MVP with authenticated users; risk surface is low.
  - Blind spot: If receipts container ever gets direct access beyond this API, stored-content assumptions break.
- **Fix B**: Add magic-byte check on first 16 bytes of staged blob (PNG: 0x89504E47, JPEG: 0xFFD8FF, WEBP: RIFF…WEBP, GIF: 47494638) via a range-header DownloadContentAsync call.
  - Strength: Closes MIME-spoofing gap completely.
  - Tradeoff: Extra blob read per confirm (~50 ms); more complex validation path.
  - Confidence: MED — overkill for MVP with authenticated consumer users.
- **Decision**: FIXED via Fix B (range-download magic-byte check; AllowedContentTypes made case-insensitive)

### F3 — In-memory blob copy: ~40 MB allocation per 20 MB upload (plan-intentional)

- **Severity**: 🔍 OBSERVATION
- **Impact**: 🔎 MEDIUM — real tradeoff; pause to reason through it
- **Dimension**: Safety & Quality
- **Location**: src/backend/Services/ReceiptConfirmService.cs:78–88
- **Detail**: DownloadContentAsync → UploadAsync copies full blob content through server heap (~40 MB per 20 MB file). The plan chose this over SyncCopyFromUriAsync with the documented rationale that Azure Storage server-side copy requires a public or SAS-signed source URI even within the same account. Implementation is correct per plan. At MVP scale (single user) acceptable; at multi-user scale concurrent uploads would pressure App Service memory limits.
- **Fix**: When App Service memory becomes a constraint, generate a short-lived read SAS on the staging blob and pass it to SyncCopyFromUriAsync — moves the copy into Azure Storage entirely, removing server-side allocation.
- **Decision**: FIXED (SyncCopyFromUriAsync with read SAS; eliminates in-memory download+reupload)

### F4 — User delegation key fetched per staging-slot request, no caching

- **Severity**: 🔍 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Safety & Quality
- **Location**: src/backend/Services/ReceiptBlobService.cs:40–46
- **Detail**: In production (CanGenerateSasUri = false / managed identity), each CreateStagingSlotAsync call triggers a GetUserDelegationKeyAsync round trip to the STS (~+200 ms per upload). Delegation keys are valid for up to 7 days; fetching one per request is wasteful.
- **Fix**: Cache the delegation key in IMemoryCache (singleton). Key on the next 60-minute expiry boundary; invalidate ~5 minutes before expiry. One IMemoryCache.GetOrCreate call in the managed-identity branch.
- **Decision**: FIXED (IMemoryCache delegation-key caching added to both ReceiptBlobService and ReceiptConfirmService; AddMemoryCache registered in Program.cs)

### F5 — LogBlobNotFound carries an Exception parameter at Information level

- **Severity**: 🔍 OBSERVATION
- **Impact**: 🏃 LOW — quick decision; fix is obvious and narrowly scoped
- **Dimension**: Pattern Consistency
- **Location**: src/backend/Services/ReceiptConfirmService.cs:~130
- **Detail**: Source-generated log method LogBlobNotFound is declared at LogLevel.Information but accepts an Exception parameter. By .NET logging convention, exception parameters attach full stack traces — misleading at Information level for a 404-equivalent client error. Other exception-bearing log calls in this service are at Warning or Error.
- **Fix**: Remove the Exception parameter from LogBlobNotFound. Log the blob name alone.
- **Decision**: SKIPPED

## Success Criteria Notes

- Automated builds recorded as PASS at commits e5d18ed (Phase 1), a3765e1 (Phase 2), 9712476 (Phase 3).
- dotnet build was attempted this session but blocked by a running process (file-in-use on ReceiptWell.exe). Compilation succeeded with 10 warnings — recommend a clean build to triage those warnings.
- ng build and ng test were not re-run this session; accepted per Progress section shas.
- All manual criteria (3.4–3.11) marked [x] at commit 9712476.
