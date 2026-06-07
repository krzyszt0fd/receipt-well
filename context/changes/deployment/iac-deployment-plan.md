# Azure IaC Deployment Plan — ReceiptWell

## Context

Both apps are scaffold-level (WeatherForecast endpoint + empty Angular shell). No Azure resources exist yet. The goal is to get both apps live on Azure, wire up CI/CD so every push to `develop` auto-deploys, and configure all infrastructure plumbing (Data Protection, Managed Identity, Key Vault, CORS) correctly from day one — before any business logic is added. Auth (Entra External ID + MSAL) is wired in a dedicated final phase so it can be tested independently.

**All Azure infrastructure is provisioned via Terraform** (`infra/` at repo root). No manual `az` commands are used for Phases 1 or 4.1. Phases 2, 3, 4.3–4.4, and 6.5 are code changes the agent performs. Phases 5.3–5.4 and all of Phase 6.1–6.3 require the browser or Azure portal.

Sources of truth: `context/foundation/infrastructure.md`, `infra/`

---

## Phase Legend

> **AGENT** — The agent can execute this step without human intervention (file edits, CLI commands).
>
> **MANUAL** — Requires the human: browser login, Azure portal clicks, or human judgement on browser output.
>
> **MANUAL → AGENT** — Human provides one value; the agent does the rest.

---

## Phase 0 — Prerequisites

> **MANUAL** — All checks in this phase are run by the human to confirm the local environment is ready.

Verify before running any Terraform or `az` commands. All steps below assume these pass.

- [ ] Azure CLI authenticated: `az login` → confirm correct subscription shown
- [ ] Correct subscription active: `az account show --query name` → matches your commercial subscription
- [ ] Terraform CLI installed: `terraform version` → `>= 1.9`
- [ ] GitHub CLI authenticated: `gh auth status` → confirms access to the repo (required for Phase 4.2 automation)
- [ ] GitHub remote configured: `git remote get-url origin` → returns the repo URL
- [ ] Note your GitHub **org/username** and **repo name** — required for `terraform.tfvars`

---

### Entra External ID Tenant — Plan ~30 minutes before starting Phase 6

> **MANUAL (portal)** — Microsoft Entra External ID has no CLI or Terraform provider for tenant creation. These steps require the Azure portal or Entra admin center. No automation path exists. Complete them before Phase 6.

- [ ] You will create an External tenant at [entra.microsoft.com](https://entra.microsoft.com) → External Identities → External tenants → Create a new external tenant
- [ ] Tenant subdomain: `receiptwellext` → domain: `receiptwellext.onmicrosoft.com`
- [ ] Region: **Europe** (permanent — cannot be changed after creation)
- [ ] You will register two apps: backend API (`ReceiptWell API`) and frontend SPA (`ReceiptWell Web`)
- [ ] You will create a sign-up/sign-in user flow
- [ ] Full step-by-step instructions are in Phase 6.1–6.3

Phase 6 cannot proceed until these are done. See § [Phase 6](#phase-6--entra-external-id-tenant--auth-wiring) for details.

---

## Phase 1 — Infrastructure Provisioning via Terraform

> **AGENT** (except step 1.1 where you set `github_org` — see below)

All resources in this phase are declared in `infra/`. A single `terraform apply` provisions:

- Resource group, Key Vault, Storage account, `data-protection` container
- App Service Plan (D1 Windows) + Web App (.NET 9) with system-assigned Managed Identity
- Role assignments: `Key Vault Secrets User` and `Storage Blob Data Contributor` on the Managed Identity
- `Key Vault Administrator` on the deploying identity (so you can write secrets in Phase 6)
- Azure Static Web App (Free tier)
- Azure AD App Registration + Service Principal + OIDC federated credential for GitHub Actions
- `Contributor` role on the resource group for the GitHub Actions service principal
- App Service app settings: `AllowedOrigins__0`, `AzureStorage__AccountName`, `AzureStorage__KeyRingContainerName`, `WEBSITE_RUN_FROM_PACKAGE`

### 1.1 Configure Variables

> **MANUAL → AGENT** — The agent creates the file; you set `github_org`.

```bash
cd infra
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars` and set:

```hcl
github_org = "your-github-username-or-org"
```

All other variables have defaults matching the resource names used throughout this plan. Override only if you need non-default names.

- [ ] `terraform.tfvars` created (not committed — it is in `.gitignore`)
- [ ] `github_org` set to your GitHub username or org

---

### 1.2 Initialise and Apply

> **AGENT**

```bash
terraform init
terraform validate
terraform plan    # review: expect ~12 resources to create
terraform apply
```

> **Role assignment propagation**: Azure RBAC changes take 1–5 minutes to propagate after `apply` completes. Do not trigger the first GitHub Actions deploy immediately — wait at least 3 minutes before pushing to `develop`, otherwise the App Service may get a 403 from Blob Storage when writing the Data Protection key ring.

- [ ] `terraform init` succeeds, providers downloaded
- [ ] `terraform validate` reports no errors
- [ ] `terraform plan` shows ~12 resources to create, no unexpected diffs
- [ ] `terraform apply` completes with no errors
- [ ] Resource group `receipt-well-rg` visible in Azure portal

---

### 1.3 Capture Outputs

> **AGENT**

```bash
terraform output
terraform output -raw static_web_app_api_key
```

Note the following — they are needed in Phase 4:

| Output | Used for |
|---|---|
| `azure_client_id` | GitHub secret `AZURE_CLIENT_ID` |
| `azure_tenant_id` | GitHub secret `AZURE_TENANT_ID` |
| `azure_subscription_id` | GitHub secret `AZURE_SUBSCRIPTION_ID` |
| `static_web_app_api_key` | GitHub secret `AZURE_STATIC_WEB_APPS_API_TOKEN` |
| `web_app_hostname` | Smoke test URL; `environment.prod.ts` `apiUrl` |
| `static_web_app_hostname` | Smoke test URL; verify CORS allowed origin matches |

> The `static_web_app_api_key` is marked sensitive and is not shown in plain `terraform output`. Use `terraform output -raw static_web_app_api_key` to retrieve it.

- [ ] All outputs recorded
- [ ] `static_web_app_api_key` retrieved and stored temporarily in a password manager

---

## Phase 2 — Backend Code Preparation

> **AGENT**

Files to modify: `src/backend/Directory.Packages.props`, `src/backend/ReceiptWell.csproj`, `src/backend/Program.cs`, `src/backend/appsettings.json`

### 2.1 Add NuGet Packages

**`Directory.Packages.props`** — add to the `<ItemGroup>`:

```xml
<PackageVersion Include="Azure.Extensions.AspNetCore.DataProtection.Blobs" Version="1.3.4" />
<PackageVersion Include="Azure.Identity" Version="1.13.2" />
<PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="9.0.4" />
```

**`ReceiptWell.csproj`** — add `<PackageReference>` entries (no version, handled by central management):

```xml
<PackageReference Include="Azure.Extensions.AspNetCore.DataProtection.Blobs" />
<PackageReference Include="Azure.Identity" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
```

- [ ] Packages added to central management
- [ ] `dotnet build` passes with zero warnings

---

### 2.2 Configure Data Protection Key Ring

**`Program.cs`** — add before `builder.Build()`:

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToAzureBlobStorage(
        new Uri("https://receiptwellstorage.blob.core.windows.net/data-protection/keys.xml"),
        new DefaultAzureCredential());
```

> **Why this matters**: Without this, App Service restarts (which happen on every deploy to D1) invalidate all active sessions and anti-forgery tokens. Must be configured before any real user logs in.

- [ ] Data Protection configured to persist to Blob Storage

---

### 2.3 Configure CORS

**`Program.cs`** — add after `AddDataProtection`:

```csharp
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [])
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});
```

Add `app.UseCors()` before `app.UseAuthentication()` in the middleware pipeline.

> The `AllowedOrigins__0` app setting is already wired to the SWA hostname by Terraform — no manual `az webapp config appsettings set` needed.

- [ ] CORS configured via `AllowedOrigins` config key

---

### 2.4 Configure JWT Bearer Auth (skeleton — full wiring in Phase 6)

**`Program.cs`** — add after CORS:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["AzureExternalId:Authority"];
        options.Audience = builder.Configuration["AzureExternalId:ClientId"];
    });
builder.Services.AddAuthorization();
```

Add `app.UseAuthentication(); app.UseAuthorization();` in the middleware pipeline (after `UseCors`).

> Auth is configured but no endpoint requires `[Authorize]` yet — the WeatherForecast endpoint remains public for smoke-test purposes.

- [ ] JWT Bearer auth registered (values come from config, not yet set)

---

### 2.5 Update `appsettings.json` (non-secret config only)

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "AllowedOrigins": [ "https://receipt-well-web.azurestaticapps.net" ],
  "AzureStorage": {
    "AccountName": "receiptwellstorage",
    "KeyRingContainerName": "data-protection"
  },
  "AzureExternalId": {
    "Authority": "",
    "ClientId": ""
  }
}
```

> Secret values (client secrets, API keys) stay out of `appsettings.json` — they go into Key Vault and are referenced via App Service configuration Key Vault references. `AzureExternalId` values are filled in Phase 6 via Terraform. App Service app settings (which override `appsettings.json` in production) are managed by Terraform and do not need to be re-applied manually.

- [ ] `appsettings.json` updated
- [ ] No secrets committed to git

---

## Phase 3 — Frontend Code Preparation

> **AGENT**

Files to create/modify: `src/frontend/src/environments/`, `src/frontend/staticwebapp.config.json`, `src/frontend/angular.json`

### 3.1 Create Environment Files

**`src/frontend/src/environments/environment.ts`**:

```typescript
export const environment = {
  production: false,
  apiUrl: 'https://localhost:7000',
  externalId: {
    authority: '',        // https://login.microsoftonline.com/<tenant-id>/v2.0
    knownAuthority: '',   // login.microsoftonline.com
    clientId: '',
    apiScope: ''
  }
};
```

**`src/frontend/src/environments/environment.prod.ts`**:

```typescript
export const environment = {
  production: true,
  apiUrl: 'https://receipt-well-api.azurewebsites.net',
  externalId: {
    authority: '',        // https://login.microsoftonline.com/<tenant-id>/v2.0
    knownAuthority: '',   // login.microsoftonline.com
    clientId: '',
    apiScope: ''
  }
};
```

Wire `fileReplacements` in `angular.json` under `architect.build.configurations.production`:

```json
"fileReplacements": [
  {
    "replace": "src/environments/environment.ts",
    "with": "src/environments/environment.prod.ts"
  }
]
```

> Entra External ID values (`authority`, `knownAuthority`, `clientId`, `apiScope`) are filled in Phase 6 after the External ID tenant is created. Files are committed with empty strings.

- [ ] Environment files created
- [ ] `fileReplacements` added to `angular.json`

---

### 3.2 Create `staticwebapp.config.json`

**`src/frontend/staticwebapp.config.json`** (Angular SPA routing fallback):

```json
{
  "navigationFallback": {
    "rewrite": "/index.html",
    "exclude": ["/api/*", "/_framework/*", "/*.{ico,png,jpg,js,css,woff,woff2}"]
  },
  "globalHeaders": {
    "Cache-Control": "no-store, no-cache"
  }
}
```

> Required for Angular client-side routing — without it, any direct URL other than `/` returns 404.

- [ ] `staticwebapp.config.json` created

---

## Phase 4 — CI/CD Setup (GitHub Actions)

### 4.1 OIDC App Registration

Already provisioned by Terraform in Phase 1. The App Registration (`receipt-well-github-deploy`), Service Principal, and federated credential for the `develop` branch are all created. Skip this step.

---

### 4.2 Store Values in GitHub Secrets

> **AGENT** — Uses `gh secret set` to push Terraform outputs directly into GitHub Actions secrets. No portal access required.

Run from the repo root after `terraform apply` has completed:

```bash
gh secret set AZURE_CLIENT_ID              --body "$(terraform -chdir=infra output -raw azure_client_id)"
gh secret set AZURE_TENANT_ID              --body "$(terraform -chdir=infra output -raw azure_tenant_id)"
gh secret set AZURE_SUBSCRIPTION_ID        --body "$(terraform -chdir=infra output -raw azure_subscription_id)"
gh secret set AZURE_STATIC_WEB_APPS_API_TOKEN --body "$(terraform -chdir=infra output -raw static_web_app_api_key)"
```

> **Prerequisite**: `gh auth status` must pass (checked in Phase 0). If the CLI is not authenticated, fall back to the portal: GitHub → repo → Settings → Secrets and variables → Actions, and add the four secrets using values from `terraform output`.
>
> `AZURE_CLIENT_ID` is the App Registration's Application (client) ID — **not** the service principal object ID. `terraform output azure_client_id` returns the correct value.

- [ ] Four GitHub secrets added (via CLI or portal fallback)

---

### 4.3 Create Backend Deploy Workflow

> **AGENT**

**`.github/workflows/backend-deploy.yml`**:

```yaml
name: Deploy Backend

on:
  push:
    branches: [develop]
    paths:
      - 'src/backend/**'
      - '.github/workflows/backend-deploy.yml'

permissions:
  id-token: write
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.x'

      - name: Restore
        run: dotnet restore src/backend/ReceiptWell.csproj

      - name: Build & Publish
        run: dotnet publish src/backend/ReceiptWell.csproj -c Release -o ./publish

      - name: Azure Login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Deploy to App Service
        uses: azure/webapps-deploy@v3
        with:
          app-name: receipt-well-api
          resource-group-name: receipt-well-rg
          package: ./publish

      - name: Smoke test
        run: |
          sleep 30
          curl --fail https://receipt-well-api.azurewebsites.net/weatherforecast
```

> **Edge case — D1 Shared cold start during deploy**: The D1 tier has no deployment slot. Expect a 5–30 s gap where the app is unreachable while the new package is being swapped. This is acceptable at MVP.
>
> The `Smoke test` step runs after every deploy to `develop`, catching regressions automatically. The 30 s sleep covers the cold start on D1.

- [ ] Workflow file created at `.github/workflows/backend-deploy.yml`

---

### 4.4 Create SWA Deploy Workflow

> **AGENT**

Because the SWA was provisioned by Terraform without GitHub repo linking (no PAT required), no workflow is auto-generated. Create it manually:

**`.github/workflows/frontend-deploy.yml`**:

```yaml
name: Deploy Frontend

on:
  push:
    branches: [develop]
    paths:
      - 'src/frontend/**'
      - '.github/workflows/frontend-deploy.yml'

permissions:
  id-token: write
  contents: read

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: true

      - name: Deploy to Static Web Apps
        uses: Azure/static-web-apps-deploy@v1
        with:
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
          repo_token: ${{ secrets.GITHUB_TOKEN }}
          action: upload
          app_location: /src/frontend
          output_location: dist/frontend/browser
          api_location: ""
```

> **Output path**: The Angular project is named `frontend` in `angular.json`. With the `@angular/build:application` builder and no explicit `outputPath` set, the build output is `dist/frontend/browser` — not `dist/receipt-well/browser`. Using the wrong path causes the workflow to fail with `index.html not found`.

- [ ] Workflow file created at `.github/workflows/frontend-deploy.yml`
- [ ] `output_location` is `dist/frontend/browser`
- [ ] `api_location` is `""` (empty string)

---

## Phase 5 — First Deploy + Smoke Test

### 5.1 Trigger Deploy

> **AGENT**

> Wait at least 3 minutes after `terraform apply` before pushing — RBAC role assignments take 1–5 minutes to propagate. A deploy that arrives too soon may fail with a 403 on Blob Storage when Data Protection tries to write `keys.xml`.

```bash
git add .
git commit -m "chore: wire Azure deployment infrastructure"
git push origin develop
```

- [ ] Push triggers both GitHub Actions workflows
- [ ] Backend workflow completes green
- [ ] Frontend workflow completes green

---

### 5.2 Backend Smoke Tests

> **AGENT** — The CI workflow (4.3) runs the `/weatherforecast` check automatically. Run the blob check locally to verify Data Protection wired up correctly.

```bash
# Verify Data Protection key ring was created in blob storage
az storage blob list \
  --account-name receiptwellstorage \
  --container-name data-protection \
  --auth-mode login \
  --query "[].name" -o tsv
# Expected: "keys.xml"
```

> **Edge case — cold start on first hit**: D1 Shared idles after inactivity. The first request after a cold start can take 5–15 s. If it times out, retry once. If it 503s repeatedly, check App Service logs with `az webapp log tail --resource-group receipt-well-rg --name receipt-well-api`.

- [ ] CI smoke test step (`/weatherforecast`) passes green in GitHub Actions
- [ ] `keys.xml` blob exists in `data-protection` container
- [ ] No 500 errors in App Service log tail

---

### 5.3 Frontend Smoke Tests

> **MANUAL** — Browser required.

- [ ] SWA URL (from `terraform output static_web_app_hostname`) loads the Angular shell
- [ ] Navigating to a non-root path (e.g. `/dashboard`) returns the Angular shell (not 404) — proves `navigationFallback` works
- [ ] Browser DevTools → Network: no failed requests to the backend yet (expected — no API calls wired)

---

### 5.4 CORS Smoke Test

> **MANUAL** — Browser DevTools required.

Open browser DevTools Console on the SWA URL and run:

```javascript
fetch('https://receipt-well-api.azurewebsites.net/weatherforecast')
  .then(r => r.json()).then(console.log)
```

- [ ] Response returns JSON without CORS error
- [ ] No `Access-Control-Allow-Origin` error in the console

---

## Phase 6 — Entra External ID Tenant + Auth Wiring

> **SUPERSEDED**: This phase assumed a dedicated CIAM (External ID for Customers) tenant using `ciamlogin.com`. The project instead uses a standard Entra ID tenant with `login.microsoftonline.com`. Steps 6.1–6.3 (CIAM tenant creation and user flows) were not executed. App registrations (6.2) were created directly in the standard tenant. Authority values use `login.microsoftonline.com/{tenant-id}/v2.0` throughout.

> Complete this phase only after Phase 5 smoke tests pass.

### 6.1 Create Entra External ID Tenant (Portal — manual)

> **MANUAL (portal)** — No CLI or Terraform path exists. See Phase 0 prerequisites for time estimate.

1. Go to [entra.microsoft.com](https://entra.microsoft.com) → External Identities → External tenants → **Create a new external tenant**
2. Tenant type: **External** (customer-facing CIAM) — **NOT USED: project uses standard Entra ID tenant instead, see Phase 6 note**
3. **Region: Europe** (cannot be changed after creation)
4. Tenant subdomain: `receiptwellext` → domain: `receiptwellext.onmicrosoft.com`
5. Link the tenant to your commercial subscription

> **Hard constraint**: Entra External ID tenant region is permanent. A tenant created outside Europe cannot be moved — it must be deleted and recreated.

- [ ] External tenant created in Europe region
- [ ] Tenant linked to commercial subscription

---

### 6.2 Register Applications in Entra External ID

> **MANUAL (portal)**

Switch to the external tenant directory: Azure portal → switch directory → `receiptwellext.onmicrosoft.com` (or open the tenant-specific admin URL from the Entra portal).

**Backend API registration:**
- App registrations → New registration
- Name: `ReceiptWell API`
- Supported account types: Accounts in this organizational directory only
- Expose an API → add scope: `access_as_user`
- Note the **Application (client) ID** → this is `AzureExternalId__ClientId` in App Service config

**Frontend SPA registration:**
- App registrations → New registration
- Name: `ReceiptWell Web`
- Platform: Single-page application
- Redirect URI: `https://receipt-well-web.azurestaticapps.net`
- Also add `http://localhost:4200` for local development
- API permissions → Add a permission → My APIs → grant `access_as_user` scope from the backend registration
- Note the **Application (client) ID**

- [ ] Backend API registered
- [ ] Frontend SPA registered
- [ ] API scope granted

---

### 6.3 Create Sign-Up/Sign-In User Flow

> **MANUAL (portal)**

In the Entra External ID admin center (still inside the external tenant):
- User flows → New user flow → **Sign up and sign in**
- Identity providers: **Email with password** (or Email one-time passcode)
- User attributes to collect: Email Address, Display Name

> The user flow name does **not** appear in the authority URL — there is no policy string to embed. The authority is simply the tenant endpoint (unlike the legacy B2C model).

- [ ] User flow created
- [ ] Tested via the "Run user flow" panel — login completes successfully

---

### 6.4 Update App Service Config with Entra External ID Values via Terraform

> **AGENT** — Add the Entra External ID values as variables in `infra/variables.tf`, extend `app_settings` in `infra/app_service.tf`, then re-run `terraform apply`. This keeps all infra state in Terraform rather than split across `az` CLI calls.

**`infra/variables.tf`** — add:

```hcl
variable "external_id_authority" {
  description = "Entra ID authority URL (login.microsoftonline.com/{tenant-id}/v2.0)"
  default     = ""
}

variable "external_id_client_id" {
  description = "Backend API app registration client ID in Entra External ID"
  default     = ""
}
```

**`infra/app_service.tf`** — extend `app_settings` block:

```hcl
"AzureExternalId__Authority" = var.external_id_authority
"AzureExternalId__ClientId"  = var.external_id_client_id
```

**`infra/terraform.tfvars`** — add (after values are known from 6.2):

```hcl
external_id_authority = "https://login.microsoftonline.com/<tenant-id>/v2.0"
external_id_client_id = "<backend-app-registration-client-id>"
```

Then apply:

```bash
terraform plan   # should show 1 resource to update: the web app app_settings
terraform apply
```

- [ ] `variables.tf` extended with Entra External ID vars
- [ ] `app_service.tf` `app_settings` extended
- [ ] `terraform.tfvars` updated with real values (not committed — gitignored)
- [ ] `terraform apply` updates App Service settings

---

### 6.5 Add MSAL to Angular

> **Code changes — AGENT. Browser login test — MANUAL.**

Install packages:

```bash
cd src/frontend
npm install @azure/msal-angular @azure/msal-browser
```

Update `environment.prod.ts` with Entra External ID values:

```typescript
externalId: {
  authority: 'https://login.microsoftonline.com/<tenant-id>/v2.0',
  knownAuthority: 'login.microsoftonline.com',
  clientId: '<frontend-spa-client-id>',
  apiScope: 'api://<backend-client-id>/access_as_user'
}
```

Wire `MsalModule` in `src/frontend/src/app/app.config.ts`:

```typescript
const msalConfig: Configuration = {
  auth: {
    clientId: environment.externalId.clientId,
    authority: environment.externalId.authority,
    knownAuthorities: [environment.externalId.knownAuthority],
    redirectUri: '/'
  }
};
```

> `knownAuthorities` is not required for `login.microsoftonline.com` (it is in MSAL's default trusted host list). The field is kept for consistency but can be omitted.

- [ ] `@azure/msal-angular` and `@azure/msal-browser` installed
- [ ] Environment files populated with real Entra External ID values
- [ ] MSAL providers added to `app.config.ts` with `knownAuthorities`
- [ ] **MANUAL** — Login flow tested end-to-end in browser

---

## Edge Cases — Extra Support Steps

| Scenario | Diagnosis | Fix |
|---|---|---|
| `terraform apply` fails: Key Vault name already in use (soft-deleted) | A vault with this name was previously destroyed and is in 90-day soft-delete | The provider feature `recover_soft_deleted_key_vaults = true` will recover it automatically on the next `apply` |
| `terraform apply` fails: `storage_account_id` schema error | Using an old azurerm 3.x syntax | azurerm 4.x requires `storage_account_id` (full resource ID) on `azurerm_storage_container`, not `storage_account_name` — check provider version |
| `terraform plan` shows permadiff on `always_on` | azurerm 4.x drift detection on D1 | Ensure `always_on = false` is explicit in `site_config` — omitting it causes a null vs false diff every plan |
| First deploy 500: `CryptographicException` on Data Protection | RBAC not yet propagated — Managed Identity cannot write to Blob Storage | Wait 3–5 min after `terraform apply` and re-push; confirm with `az role assignment list --assignee <managed_identity_principal_id>` |
| GitHub Actions OIDC failure: `AADSTS70021` | Subject claim mismatch in the federated credential | Check `infra/oidc.tf` subject string: must be exactly `repo:<github_org>/<github_repo>:ref:refs/heads/<github_deploy_branch>` — re-apply Terraform if `github_org` was wrong |
| SWA deploy fails: `index.html not found in dist/frontend/browser` | Wrong `output_location` in the workflow | Angular project name in `angular.json` is `frontend`; output path is `dist/frontend/browser` — update the workflow |
| CORS error from browser | `AllowedOrigins` mismatch | The SWA hostname in `AllowedOrigins__0` is set by Terraform from `azurerm_static_web_app.web.default_host_name`. Verify it matches exactly (no trailing slash) with `terraform output static_web_app_hostname` |
| `az webapp log tail` drops connection | Known D1 Shared limitation noted in `infrastructure.md` | Use Azure portal → App Service → Log stream as fallback |
| External ID login loop / redirect_uri mismatch | Redirect URI not registered in SPA app registration | Add exact redirect URI in Entra External ID admin center → SPA registration → Authentication |
| `AADSTS500011` — resource principal not found | API scope URI mismatch between frontend and backend registrations | Verify `apiScope` in `environment.prod.ts` matches exactly the URI exposed in the backend app registration (Entra External ID admin center → ReceiptWell API → Expose an API) |
| `ClientConfigurationError: knownAuthority` at MSAL init | `knownAuthorities` omitted and a non-default authority is in use | Not applicable for `login.microsoftonline.com` (trusted by default); only relevant if switching to a custom domain |
| Need to import existing resources (Phase 1 CLI commands already ran) | Resources exist in Azure but not in Terraform state | Run `terraform import` commands listed in the infra README; then `terraform plan` to verify zero diff before applying |

---

## Terraform State

State is stored locally at `infra/terraform.tfstate`. This file is excluded from git by `.gitignore`. Keep it safe — losing the state file means Terraform cannot track existing resources and will try to recreate them.

To move to a shared backend (team or CI use), replace the `backend "local"` block in `infra/main.tf` with an `backend "azurerm"` block pointing to a dedicated `tfstate` container in the storage account, then run `terraform init -migrate-state`.

---

## Files Created / Modified

| File | Action | Who |
|---|---|---|
| `infra/main.tf` | Terraform block, providers, local backend, data sources | AGENT |
| `infra/variables.tf` | All input variable declarations + Entra External ID vars (Phase 6.4) | AGENT |
| `infra/outputs.tf` | All outputs; post-apply actions documented inline | AGENT |
| `infra/resource_group.tf` | Resource group | AGENT |
| `infra/key_vault.tf` | Key Vault (RBAC) + deployer Key Vault Administrator role | AGENT |
| `infra/storage.tf` | Storage account + data-protection container | AGENT |
| `infra/app_service.tf` | App Service Plan + Windows Web App + app_settings + Entra External ID vars | AGENT |
| `infra/static_web_app.tf` | Static Web App (Free tier, no GitHub linking) | AGENT |
| `infra/role_assignments.tf` | Managed Identity role assignments | AGENT |
| `infra/oidc.tf` | App Registration, Service Principal, federated credential, Contributor role | AGENT |
| `infra/terraform.tfvars.example` | Variable template (committed); `terraform.tfvars` is gitignored | AGENT |
| `.gitignore` | Terraform state and cache exclusions | AGENT |
| `src/backend/Directory.Packages.props` | Add 3 package versions | AGENT |
| `src/backend/ReceiptWell.csproj` | Add 3 PackageReference entries | AGENT |
| `src/backend/Program.cs` | Add Data Protection, CORS, JWT Bearer, middleware pipeline | AGENT |
| `src/backend/appsettings.json` | Add AllowedOrigins, AzureStorage, AzureExternalId config keys | AGENT |
| `src/frontend/src/environments/environment.ts` | Create with dev defaults | AGENT |
| `src/frontend/src/environments/environment.prod.ts` | Create with prod Azure URLs | AGENT |
| `src/frontend/angular.json` | Add fileReplacements for production build | AGENT |
| `src/frontend/staticwebapp.config.json` | Create SPA routing fallback | AGENT |
| `.github/workflows/backend-deploy.yml` | Create OIDC backend deploy workflow + smoke test step | AGENT |
| `.github/workflows/frontend-deploy.yml` | Create SWA frontend deploy workflow | AGENT |
| `src/frontend/package.json` | Add MSAL packages (Phase 6) | AGENT |
| `src/frontend/src/app/app.config.ts` | Wire Entra External ID MSAL providers with `knownAuthorities` (Phase 6) | AGENT |
| Entra External ID tenant + app registrations + user flow | Portal setup | MANUAL |

---

## Verification Checklist (End State)

- [ ] `terraform output` shows all 10 outputs with no errors
- [ ] `https://receipt-well-api.azurewebsites.net/weatherforecast` returns JSON (CI smoke test green)
- [ ] `https://<swa-hostname>.azurestaticapps.net` loads Angular shell
- [ ] SPA routing fallback works (direct nav to sub-path returns shell, not 404)
- [ ] CORS: fetch from SWA URL to API URL succeeds in browser console
- [ ] Data Protection: `keys.xml` blob exists in `data-protection` container
- [ ] Managed Identity: App Service has no connection strings — all Azure access via identity
- [ ] CI/CD: push to `develop` auto-deploys both apps within ~5 minutes
- [ ] `terraform plan` after a successful deploy shows zero changes (no drift)
- [ ] Entra External ID login flow completes and returns a JWT (Phase 6)
