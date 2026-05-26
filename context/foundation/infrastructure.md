---
project: receipt-well
researched_at: 2026-05-26
recommended_platform: Azure App Service (Windows D1, West Europe)
runner_up: Render
context_type: mvp
tech_stack:
  language: C#
  framework: ASP.NET Core webapi
  runtime: .NET 9 STS (GA on Azure App Service; EOL ~Nov 2026)
  app_service_plan: D1 Shared (Windows) — no deployment slots, no always-on
  frontend: Angular — Azure Static Web Apps (global CDN, MSAL for auth)
  auth: Azure B2C + MSAL (handled in Angular via @azure/msal-angular, not SWA built-in auth)
  background_jobs: Azure Functions Consumption plan — Event Grid trigger on blob upload
  storage: Azure Blob Storage
  search: Azure AI Search
  ai: Azure OpenAI (West Europe)
---

## Recommendation

**Deploy on Azure App Service (Windows, D1 Shared tier, West Europe).**

The user already operates a commercial Azure subscription (100 €/month), has hands-on Azure expertise, and the PRD stack names five Azure-native services: Blob Storage for receipt images, Azure OpenAI for extraction, Azure AI Search for tag search, Azure Functions for event-driven background processing, and Azure B2C + MSAL for auth. Every alternative platform would require running all five of those services as external cross-cloud dependencies over the public internet. Azure App Service is the only platform where every component in the architecture is co-located, connected via Managed Identity (no secrets), and supported natively without a Dockerfile. The **D1 Shared tier** (~$9.49/month) replaces the S1 tier to reduce cost — this requires **Windows hosting** (D1 is not available on Linux App Service Plans). Deployment slots and preview environments are not used; rollback is a redeploy of the previous build artifact. The Angular frontend deploys to **Azure Static Web Apps** (free tier, no PR preview environments enabled), giving it a global CDN and auto-generated GitHub Actions workflow without adding compute cost; auth is handled by `@azure/msal-angular` talking directly to the B2C tenant, keeping the SWA built-in auth wrapper out of the picture. Receipt extraction is triggered by an **Event Grid subscription on the Blob Storage container** rather than a polling Blob trigger, reducing extraction latency from up to 10 minutes to seconds.

## Platform Comparison

**Hard-filtered out** (do not support the .NET runtime): Cloudflare Workers (JS/WASM only), Vercel (JS/Node.js native), Netlify (JS/Go functions only). These three platforms were dropped before scoring.

| Platform | CLI-first | Managed | Agent docs | Stable deploy API | MCP/Integration |
|---|---|---|---|---|---|
| **Azure App Service** | Pass | Pass | Pass | Pass | Pass |
| **Render** | Pass | Pass | Pass | Partial | Pass |
| **Fly.io** | Pass | Pass | Partial | Pass | Partial |
| **Railway** | Partial | Pass | Partial | Partial | Partial |

**Score notes:**
- Render: no CLI rollback command (dashboard or REST API only); otherwise strong — MCP server GA since August 2025, `llms.txt` published.
- Fly.io: no `llms.txt`; docs are HTML-only; `fly mcp server` is experimental; monorepo layout requires explicit `--dockerfile` path.
- Railway: rollback is UI-only (no `railway rollback` CLI command); MCP server has no GA declaration; no `llms.txt`.

**Interview soft-weight adjustments:**
- Cost: existing 100 €/month Azure subscription — cost delta between Azure and alternatives is near-zero.
- Familiarity: commercial Azure expertise — strong tie-breaker.
- Co-location: Blob Storage, Azure OpenAI, Azure AI Search, Azure Functions, Azure B2C all required → Azure wins decisively.
- Region: single-region Poland/Europe → West Europe (Netherlands) is the closest GA Azure region; Frankfurt-based alternatives (Render, Fly.io) are slightly farther.

### Shortlisted Platforms

#### 1. Azure App Service (Recommended)

Native .NET 9 STS support on Windows (no Dockerfile required; GA, EOL ~Nov 2026). All five co-located Azure services connect via Managed Identity — no secrets to rotate or store in config. D1 Shared tier at ~$9.49/month eliminates deployment slots; rollback is a redeploy from a prior SHA. Azure MCP Server (.mcpb, GA April 2026) covers App Service, Blob Storage, Key Vault, AI services, and Functions — the widest managed-service coverage of any platform evaluated. Azure CLI (`az webapp`) provides deploy and log tail from the terminal. GitHub Actions `azure/webapps-deploy@v3` is GA and OIDC-backed (no long-lived secrets). Microsoft Learn MCP Server (GA) makes Azure docs directly accessible to Claude Code.

#### 2. Render

Strongest MCP story among the alternatives — GA since August 2025, with over 20 tools including deploy, debug, and monitor. Publishes `llms.txt` and per-page Markdown docs. Frankfurt region covers the EU requirement. Gaps: Docker-only for .NET (multi-stage Dockerfile required), no CLI rollback command (dashboard or REST API), and ~15–21 €/month extra cost (web service + background worker at Starter tier). Azure Blob Storage stays external, adding cross-cloud latency for blob reads.

#### 3. Fly.io

Mature `flyctl` CLI; Frankfurt (`fra`) is the closest option to Warsaw (~600 km). Auto-stop/auto-start machines can reduce idle compute cost to near-zero. Gaps: no `llms.txt`, HTML-only docs, `fly mcp server` is experimental, and the monorepo layout (backend at `src/backend/`, Angular at `src/frontend/`) causes `fly launch` to misdetect the app as Node.js when run from the repo root. Data Protection key ring must be externalized to Azure Blob Storage for multi-machine deployments.

## Anti-Bias Cross-Check: Azure App Service

### Devil's Advocate — Weaknesses

1. **Total Azure lock-in.** The stack layers App Service + Functions + Blob Storage + AI Search + Azure OpenAI + B2C. Every service is Azure-specific. There is no incremental exit path — migrating requires replacing all services simultaneously.

2. **Azure Functions Consumption cold starts.** The receipt extraction flow runs on a Consumption plan. .NET cold starts on Consumption regularly reach 8–12 seconds. Users will see "pending" status longer than expected if the UX does not explicitly communicate async processing.

3. **D1 Shared has no SLA and shared compute.** The D1 tier runs on shared infrastructure — noisy-neighbour CPU contention is possible. There is no Azure SLA for the Shared tier. If a neighbour workload spikes, the API will be slow. Acceptable for an MVP with low traffic; revisit at first signs of p95 latency degradation.

4. **Azure CLI verbosity.** `az` is a Python CLI; `az webapp log tail` drops the connection under low activity and is less reliable than `fly logs` or `railway logs --tail`. Agents using `az` directly need more output parsing than with alternative CLIs.

5. **Azure OpenAI content filtering may silently block receipts.** Azure OpenAI enforces mandatory content filters. Receipt OCR text containing flagged product categories (certain medications, alcohol brands) can be rejected silently without a custom filter policy. This must be configured explicitly before launch.

### Pre-Mortem — How This Could Fail

Six months after launch the team is debugging in production. Azure Functions Consumption cold starts average 10 seconds for the .NET extraction flow — users leave the app before their receipt is processed. Moving to Flex Consumption to reduce cold starts adds ~30 €/month, pushing total Azure spend above the 100 €/month subscription budget. Separately, the Azure AI Search index schema was laid down hastily for MVP and lacks a Polish-language analyzer; tag recall is poor for Polish receipt content. Adding the analyzer requires dropping and recreating the index — a two-hour maintenance window that breaks the "always accessible" guardrail. The Data Protection key ring was never externalized; after the first App Service restart following a code push, all active user sessions are invalidated and the support inbox fills with "I got logged out" reports. The root cause takes two days to trace. Finally, 4% of receipts containing certain product categories are silently dropped by Azure OpenAI's content filter, with no error surfaced to the user. These operational gaps — all fixable, none obvious at MVP kick-off — accumulate into a perception that the infrastructure is unreliable.

### Unknown Unknowns

1. **Event Grid trigger chosen over Blob trigger.** Azure Functions Blob Storage triggers poll on a schedule and can delay by up to 10 minutes during inactivity — unacceptable for a receipt app. The architecture uses an **Event Grid subscription on the Blob Storage container** to fire the Function within seconds of upload. This requires creating a System Topic on the storage account and a `BlobCreated` event subscription pointing at the Function endpoint; the Function uses an `EventGridTrigger` binding instead of `BlobTrigger`.

2. **Data Protection key ring invalidates sessions on restart.** ASP.NET Core Data Protection stores keys in-memory by default. Any App Service restart, scale-out, or slot swap will invalidate all existing JWT validation keys and anti-forgery tokens. Configure **Azure Blob Storage as the key ring persister** (`PersistKeysToAzureBlobStorage(...)`) before deploying to real users.

3. **Azure AI Search enrichment billing.** If built-in AI enrichment skills (OCR, entity extraction) are enabled on the search indexer, Azure AI Search charges per 1,000 documents enriched. The better pattern for this stack: extract via Azure OpenAI externally → store normalized tags in the index → use Azure AI Search for tag-only queries with no built-in enrichment.

4. **B2C tenant in the right region.** Azure B2C tenants are created in a specific region and cannot be moved. Create the B2C tenant in **Europe** at setup time. A B2C tenant created in the wrong geography adds latency to every token validation and cannot be corrected later without recreating the tenant.

5. **West Europe single-region blast radius.** All five Azure services co-located in West Europe share the same failure domain. Azure's West Europe region has experienced partial outages multiple times in the past three years. Acceptable for MVP; worth documenting as a known risk.

## Operational Story

- **Preview deploys**: None — no deployment slots (D1 tier), no SWA PR preview environments. All merges to `develop` deploy directly to production. Test locally before merging.

- **Secrets**: Application settings stored in Azure App Service configuration (`az webapp config appsettings set`). Sensitive values (connection strings, API keys) stored in Azure Key Vault; App Service reads them via Key Vault references using Managed Identity — no secret leaves the vault. Azure B2C client IDs are not secrets; client secrets go in Key Vault. GitHub Actions uses OIDC federated credentials — no long-lived `AZURE_CLIENT_SECRET` stored in GitHub Secrets.

- **Rollback**: No slot swap available on D1. Rollback is a redeploy of the previous build artifact: re-run the GitHub Actions workflow on the prior commit (`git revert` or re-trigger the workflow at the last good SHA). Expect 2–5 minutes of downtime during redeploy. Design schema changes to be backward-compatible so a rollback doesn't require a matching data migration reversal.

- **Approval**: Human-on-irreversibles: creating or deleting resource groups, rotating the B2C client secret, dropping or recreating the Azure AI Search index, deleting a Blob Storage container. Agent-permitted: deploy to staging slot, swap slots, tail logs, update non-secret app settings, trigger a Function manually for testing.

- **Logs**: `az webapp log tail --resource-group receipt-well-rg --name receipt-well-api` streams App Service stdout/stderr. Azure Functions logs via `func azure functionapp logstream receipt-well-fn` or Azure Monitor / Log Analytics. Static Web Apps build and deploy logs are visible in the GitHub Actions run. The Azure MCP Server exposes log queries as structured tools for Claude Code.

## Risk Register

| Risk | Source | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| Azure Functions cold start degrades UX | Pre-mortem | H | M | Design UX to communicate async status explicitly; consider Flex Consumption if >3-second extraction p90 is unacceptable |
| ~~Blob trigger delay~~ | Unknown unknowns | — | — | **Mitigated**: Event Grid trigger adopted; extraction fires within seconds of blob upload |
| Event Grid subscription misconfiguration silently drops events | Research finding | M | H | Verify event subscription with `az eventgrid event-subscription show`; add dead-letter destination on the subscription |
| Data Protection key ring invalidates sessions | Unknown unknowns | H | H | Configure `PersistKeysToAzureBlobStorage(...)` on first deploy — not after |
| Azure OpenAI content filter silently drops receipts | Devil's advocate | M | M | Configure custom content filter policy at deployment time; add extraction failure event with user-visible error |
| B2C tenant created in wrong region | Unknown unknowns | M | H | Create B2C tenant in Europe during initial Azure setup; document in deploy plan |
| Azure AI Search enrichment billing accumulation | Unknown unknowns | M | M | Disable built-in AI enrichment; extract via Azure OpenAI externally and store pre-computed tags |
| West Europe regional outage takes down all services | Pre-mortem | L | H | Acceptable for MVP; document RTO expectation; revisit multi-region at post-MVP |
| Azure lock-in limits future platform flexibility | Devil's advocate | L | H | Accept as a deliberate trade-off given existing subscription and expertise |
| D1 Shared tier has no SLA and shared compute | Devil's advocate | M | M | Acceptable for MVP; upgrade to B1 (dedicated) if p95 API latency degrades or uptime SLA is needed |
| D1 has no always-on — app idles after inactivity | Research finding | H | M | First request after idle incurs a cold start (5–15s for .NET). Acceptable for MVP; add a lightweight health-check ping or upgrade to B1 if cold starts are unacceptable |
| .NET 9 STS reaches EOL ~Nov 2026 | Research finding | L | M | Plan migration to .NET 10 LTS (expected GA Nov 2025) before EOL; App Service runtime upgrades are in-place with no downtime |
| No deployment slots — rollback requires full redeploy | Research finding | M | M | Acceptable trade-off for cost; mitigate by keeping commits small and testing locally before merge |
| SWA free tier bandwidth cap (100 GB/month) | Research finding | L | L | Angular bundles are typically 1–5 MB; 100 GB covers ~20k–100k page loads/month — upgrade to Standard ($9/month) if exceeded |

## Getting Started

These steps assume an existing Azure subscription with Contributor access and the Azure CLI authenticated (`az login`).

1. **Create the resource group and App Service Plan (D1 Shared — Windows):**
   ```bash
   az group create --name receipt-well-rg --location westeurope
   # D1 is a Windows-only tier — omit --is-linux
   az appservice plan create --name receipt-well-plan --resource-group receipt-well-rg \
     --sku D1
   ```

2. **Create the Web App with .NET 9 runtime (GA):**
   ```bash
   az webapp create --name receipt-well-api --resource-group receipt-well-rg \
     --plan receipt-well-plan --runtime "dotnet:9.0"
   # Note: always-on is not available on D1 Shared tier — omit that config
   # If the command rejects "dotnet:9.0", verify the exact string:
   # az webapp list-runtimes --os windows | grep -i dotnet
   ```

3. **Enable Managed Identity and grant access to co-located services:**
   ```bash
   az webapp identity assign --name receipt-well-api --resource-group receipt-well-rg
   # Then assign Storage Blob Data Contributor, Cognitive Services OpenAI User,
   # and Search Index Data Contributor roles to the managed identity.
   ```

4. **Create the Azure Static Web Apps resource for the Angular frontend:**
   ```bash
   az staticwebapp create --name receipt-well-web --resource-group receipt-well-rg \
     --location westeurope --source https://github.com/<org>/receipt-well \
     --branch develop --app-location /src/frontend --output-location dist/receipt-well \
     --login-with-github
   ```
   This auto-generates a GitHub Actions workflow in the repo. Set `apiLocation` to empty string — the API is a separate App Service, not a SWA managed function. PR preview environments are disabled by default unless opted in via `staticwebapp.config.json`.

5. **Wire API GitHub Actions CI/CD** using OIDC (no long-lived secrets). Use the official `azure/webapps-deploy@v3` action deploying directly to production (no slot) on push to `develop`. No staging slot or swap step.

6. **Set up Event Grid trigger for receipt extraction:**
   ```bash
   # Create a System Topic on the Blob Storage account
   az eventgrid system-topic create --name receipt-uploads-topic \
     --resource-group receipt-well-rg \
     --source /subscriptions/<sub>/resourceGroups/receipt-well-rg/providers/Microsoft.Storage/storageAccounts/<storage> \
     --topic-type microsoft.storage.storageaccounts \
     --location westeurope
   # Create event subscription pointing at the Azure Function
   az eventgrid system-topic event-subscription create \
     --name receipt-extraction-trigger \
     --resource-group receipt-well-rg \
     --system-topic-name receipt-uploads-topic \
     --endpoint <function-endpoint-url> \
     --included-event-types Microsoft.Storage.BlobCreated \
     --subject-begins-with /blobServices/default/containers/receipts/
   ```

7. **Install the Azure MCP Server for Claude Code:**
   Download the `.mcpb` bundle from [github.com/microsoft/mcp](https://github.com/microsoft/mcp) and register it in Claude Code settings. This gives Claude Code structured access to App Service, Blob Storage, Key Vault, AI services, and Azure Functions without parsing `az` output.

## Out of Scope

The following were not evaluated in this research:
- Docker image configuration
- CI/CD pipeline setup (GitHub Actions workflow authoring)
- Production-scale architecture (multi-region, HA, DR)
- Azure B2C tenant detailed configuration (user flows, custom policies, MSAL integration code)
- Angular MSAL configuration (`@azure/msal-angular` setup, interceptors, guard configuration)
