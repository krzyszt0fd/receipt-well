# Initial Azure Deployment Plan — ReceiptWell

## Context

Both apps are scaffold-level (WeatherForecast endpoint + empty Angular shell). No Azure resources exist yet. The goal is to get both apps live on Azure, wire up CI/CD so every push to `develop` auto-deploys, and configure all infrastructure plumbing (Data Protection, Managed Identity, Key Vault, CORS) correctly from day one — before any business logic is added. Auth (B2C + MSAL) is wired in a dedicated final phase so it can be tested independently.

Source of truth: `context/foundation/infrastructure.md`

---

## Phase 0 — Prerequisites

> Verify before running any `az` commands. All steps below assume these pass.

- [ ] Azure CLI authenticated: `az login` → confirm correct subscription shown
- [ ] Correct subscription active: `az account show --query name` → matches your commercial subscription
- [ ] GitHub remote configured: `git remote get-url origin` → returns the repo URL
- [ ] Note the GitHub **org** and **repo name** (needed for SWA provisioning and OIDC federated credential)
- [ ] Confirm `az account list-runtimes` is available (CLI version ≥ 2.56)

---

## Phase 1 — Azure Infrastructure Provisioning

### 1.1 Resource Group

```bash
az group create --name receipt-well-rg --location westeurope
```

- [ ] Resource group created

---

### 1.2 Key Vault

```bash
az keyvault create \
  --name receipt-well-kv \
  --resource-group receipt-well-rg \
  --location westeurope \
  --enable-rbac-authorization true
```

> Using RBAC authorization (not vault access policies) — aligns with Managed Identity role assignments later.

- [ ] Key Vault created
- [ ] Note the vault URI: `https://receipt-well-kv.vault.azure.net/`

---

### 1.3 Storage Account

```bash
az storage account create \
  --name receiptwellstorage \
  --resource-group receipt-well-rg \
  --location westeurope \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false
```

Create the Data Protection container (must exist before app starts):

```bash
az storage container create \
  --name data-protection \
  --account-name receiptwellstorage \
  --auth-mode login
```

- [ ] Storage account created
- [ ] `data-protection` container created

---

### 1.4 App Service Plan + Web App

```bash
# D1 Shared — Windows only (omit --is-linux)
az appservice plan create \
  --name receipt-well-plan \
  --resource-group receipt-well-rg \
  --sku D1

# Verify the exact runtime string first:
az webapp list-runtimes --os windows | grep -i dotnet
# Expected: "dotnet:9"  (use this exact string in the next command)

az webapp create \
  --name receipt-well-api \
  --resource-group receipt-well-rg \
  --plan receipt-well-plan \
  --runtime "dotnet:9"
```

> **Edge case**: The runtime string may be `"dotnet:9"` or `"DOTNET|9.0"` depending on CLI version. Run the grep above and copy the exact value shown — do not guess.

- [ ] App Service Plan created (D1, Windows)
- [ ] Web App created with .NET 9 runtime
- [ ] Note the default hostname: `https://receipt-well-api.azurewebsites.net`

---

### 1.5 Enable Managed Identity

```bash
az webapp identity assign \
  --name receipt-well-api \
  --resource-group receipt-well-rg
```

- [ ] System-assigned Managed Identity enabled
- [ ] Note the `principalId` from the output (needed for role assignments)

---

### 1.6 Role Assignments for Managed Identity

```bash
# Get the subscription ID and managed identity principalId
SUB=$(az account show --query id -o tsv)
MI_ID=$(az webapp identity show --name receipt-well-api --resource-group receipt-well-rg --query principalId -o tsv)
KV_ID=$(az keyvault show --name receipt-well-kv --resource-group receipt-well-rg --query id -o tsv)
ST_ID=$(az storage account show --name receiptwellstorage --resource-group receipt-well-rg --query id -o tsv)

# Key Vault Secrets User (read secrets via Key Vault references)
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee-object-id $MI_ID \
  --assignee-principal-type ServicePrincipal \
  --scope $KV_ID

# Storage Blob Data Contributor (Data Protection key ring + future receipt blobs)
az role assignment create \
  --role "Storage Blob Data Contributor" \
  --assignee-object-id $MI_ID \
  --assignee-principal-type ServicePrincipal \
  --scope $ST_ID
```

- [ ] Key Vault Secrets User role assigned
- [ ] Storage Blob Data Contributor role assigned

> **Edge case**: Role assignments can take 1–5 minutes to propagate. If the first deploy fails with a 403 on Blob Storage, wait and redeploy rather than adding a connection string.

---

### 1.7 Static Web Apps

```bash
# Replace <org> and <repo> with your GitHub org and repo name
az staticwebapp create \
  --name receipt-well-web \
  --resource-group receipt-well-rg \
  --location westeurope \
  --source https://github.com/<org>/receipt-well \
  --branch develop \
  --app-location /src/frontend \
  --output-location dist/receipt-well/browser \
  --login-with-github
```

> **Critical edge case — Angular 21 output path**: Angular 17+ with the `application` builder outputs to `dist/<project>/browser`, not `dist/<project>`. The flag above uses `dist/receipt-well/browser`. Verify by checking `angular.json` → `architect.build.options.outputPath`. If it differs, update this flag before running.

> **Edge case — auto-generated workflow**: SWA creates `.github/workflows/azure-static-web-apps-*.yml` automatically. Open that file immediately after provisioning and set `api_location: ""` (empty string) — it defaults to `"api"` which will cause build failures since the API is a separate App Service.

- [ ] Static Web App created
- [ ] Note the SWA default hostname (e.g. `https://receipt-well-web.azurestaticapps.net`)
- [ ] Auto-generated GitHub Actions workflow committed to repo
- [ ] `api_location: ""` set in the auto-generated workflow

---

## Phase 2 — Backend Code Preparation

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
        new Uri($"https://receiptwellstorage.blob.core.windows.net/data-protection/keys.xml"),
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

- [ ] CORS configured via `AllowedOrigins` config key

---

### 2.4 Configure JWT Bearer Auth (skeleton — full wiring in Phase 6)

**`Program.cs`** — add after CORS:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["AzureB2C:Authority"];
        options.Audience = builder.Configuration["AzureB2C:ClientId"];
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
  "AzureB2C": {
    "Authority": "",
    "ClientId": ""
  }
}
```

> Secret values (client secrets, API keys) stay out of `appsettings.json` — they go into Key Vault and are referenced via App Service configuration Key Vault references. `AzureB2C` values are filled in Phase 6.

- [ ] `appsettings.json` updated
- [ ] No secrets committed to git

---

### 2.6 App Service Application Settings (CORS + non-secret config)

```bash
az webapp config appsettings set \
  --name receipt-well-api \
  --resource-group receipt-well-rg \
  --settings \
    AllowedOrigins__0="https://receipt-well-web.azurestaticapps.net" \
    AzureStorage__AccountName="receiptwellstorage" \
    AzureStorage__KeyRingContainerName="data-protection"
```

- [ ] App settings configured on App Service

---

## Phase 3 — Frontend Code Preparation

Files to create/modify: `src/frontend/src/environments/`, `src/frontend/staticwebapp.config.json`

### 3.1 Create Environment Files

**`src/frontend/src/environments/environment.ts`**:

```typescript
export const environment = {
  production: false,
  apiUrl: 'https://localhost:7000',
  b2c: {
    tenantName: '',
    clientId: '',
    userFlowSignUpSignIn: 'B2C_1_signupsignin',
    apiScope: ''
  }
};
```

**`src/frontend/src/environments/environment.prod.ts`**:

```typescript
export const environment = {
  production: true,
  apiUrl: 'https://receipt-well-api.azurewebsites.net',
  b2c: {
    tenantName: '',
    clientId: '',
    userFlowSignUpSignIn: 'B2C_1_signupsignin',
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

> B2C values (`tenantName`, `clientId`, `apiScope`) are filled in Phase 6 after the B2C tenant is created. Files are committed with empty strings.

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

### 4.1 Create OIDC App Registration for Backend Deploy

Done in Azure portal or CLI. This is the **main Azure AD tenant** (not B2C):

```bash
# Create the App Registration
APP_ID=$(az ad app create --display-name "receipt-well-github-deploy" --query appId -o tsv)
# Create the service principal
az ad sp create --id $APP_ID

# Add federated credential (replace <org> and <repo>)
az ad app federated-credential create \
  --id $APP_ID \
  --parameters '{
    "name": "github-develop",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<org>/receipt-well:ref:refs/heads/develop",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# Assign Contributor role on the resource group (simpler than per-resource for MVP)
SUB=$(az account show --query id -o tsv)
az role assignment create \
  --role "Contributor" \
  --assignee $APP_ID \
  --scope /subscriptions/$SUB/resourceGroups/receipt-well-rg
```

- [ ] App Registration created
- [ ] Federated credential configured for `develop` branch
- [ ] Contributor role assigned on resource group

---

### 4.2 Store OIDC Values in GitHub Secrets

In GitHub → repo → Settings → Secrets and variables → Actions, add:

| Secret name | Value |
|---|---|
| `AZURE_CLIENT_ID` | App Registration's Application (client) ID |
| `AZURE_TENANT_ID` | Main Azure AD tenant ID (from `az account show --query tenantId`) |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID (from `az account show --query id`) |

> These are identifiers, not passwords — but store them as secrets to avoid leaking subscription topology.

- [ ] Three GitHub secrets added

---

### 4.3 Create Backend Deploy Workflow

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
```

> **Edge case — D1 Shared cold start during deploy**: The D1 tier has no deployment slot. Expect a 5–30 s gap where the app is unreachable while the new package is being swapped. This is acceptable at MVP; document it in the team wiki.

- [ ] Workflow file created at `.github/workflows/backend-deploy.yml`

---

### 4.4 Fix Auto-Generated SWA Workflow

Open the auto-generated file (`.github/workflows/azure-static-web-apps-*.yml`) and update:

```yaml
# Find this:
api_location: "api"
# Change to:
api_location: ""
```

Also confirm:
```yaml
app_location: "/src/frontend"
output_location: "dist/receipt-well/browser"
```

- [ ] `api_location` set to empty string
- [ ] `output_location` matches Angular 21 build output path

---

## Phase 5 — First Deploy + Smoke Test

### 5.1 Trigger Deploy

```bash
git add .
git commit -m "chore: wire Azure deployment infrastructure"
git push origin develop
```

- [ ] Push triggers both GitHub Actions workflows
- [ ] Backend workflow completes green
- [ ] SWA workflow completes green

---

### 5.2 Backend Smoke Tests

```bash
# Health — default route
curl https://receipt-well-api.azurewebsites.net/weatherforecast

# Verify Data Protection key ring was created in blob storage
az storage blob list \
  --account-name receiptwellstorage \
  --container-name data-protection \
  --auth-mode login \
  --query "[].name" -o tsv
# Expected: "keys.xml"
```

> **Edge case — cold start on first hit**: D1 Shared idles after inactivity. The first request after a cold start can take 5–15 s. If it times out, retry once. If it 503s repeatedly, check App Service logs with `az webapp log tail --resource-group receipt-well-rg --name receipt-well-api`.

- [ ] `/weatherforecast` returns JSON from Azure URL
- [ ] `keys.xml` blob exists in `data-protection` container
- [ ] No 500 errors in App Service log tail

---

### 5.3 Frontend Smoke Tests

- [ ] SWA URL loads the Angular shell
- [ ] Navigating to a non-root path (e.g. `/dashboard`) returns the Angular shell (not 404) — proves `navigationFallback` works
- [ ] Browser DevTools → Network: no failed requests to the backend yet (expected — no API calls wired)

---

### 5.4 CORS Smoke Test

Open browser DevTools Console on the SWA URL and run:

```javascript
fetch('https://receipt-well-api.azurewebsites.net/weatherforecast')
  .then(r => r.json()).then(console.log)
```

- [ ] Response returns JSON without CORS error
- [ ] No `Access-Control-Allow-Origin` error in the console

---

## Phase 6 — Azure B2C Tenant + Auth Wiring

> Complete this phase only after Phase 5 smoke tests pass. B2C tenant creation requires Azure portal access.

### 6.1 Create B2C Tenant (Portal — manual)

1. Go to Azure Portal → Create a resource → Azure Active Directory B2C
2. **Region: Europe** (cannot be changed after creation)
3. Tenant name: `receiptwellb2c` → domain: `receiptwellb2c.onmicrosoft.com`
4. Link the tenant to your commercial subscription

> **Hard constraint**: B2C tenant region is permanent. A tenant created outside Europe cannot be moved — it must be deleted and recreated.

- [ ] B2C tenant created in Europe region
- [ ] Tenant linked to commercial subscription

---

### 6.2 Register Applications in B2C

**Backend API registration:**
- Name: `ReceiptWell API`
- Supported account types: Accounts in this organization directory only
- Expose an API → add scope: `access_as_user`
- Note the **Application (client) ID** → this is `AzureB2C__ClientId` in App Service config

**Frontend SPA registration:**
- Name: `ReceiptWell Web`
- Platform: Single-page application
- Redirect URI: `https://receipt-well-web.azurestaticapps.net`
- Also add `http://localhost:4200` for local development
- API permissions: grant `access_as_user` scope from the backend registration
- Note the **Application (client) ID**

- [ ] Backend API registered
- [ ] Frontend SPA registered
- [ ] API scope granted

---

### 6.3 Create Sign-Up/Sign-In User Flow

In B2C portal:
- User flows → New user flow → Sign up and sign in → Recommended
- Name: `signupsignin` → final policy ID: `B2C_1_signupsignin`
- Identity providers: Email signup
- User attributes to collect: Email Address, Display Name

- [ ] User flow created
- [ ] Tested via B2C "Run user flow" panel — login completes successfully

---

### 6.4 Update App Service Config with B2C Values

```bash
B2C_AUTHORITY="https://receiptwellb2c.b2clogin.com/receiptwellb2c.onmicrosoft.com/B2C_1_signupsignin/v2.0/"
B2C_CLIENT_ID="<backend-app-registration-client-id>"

az webapp config appsettings set \
  --name receipt-well-api \
  --resource-group receipt-well-rg \
  --settings \
    AzureB2C__Authority="$B2C_AUTHORITY" \
    AzureB2C__ClientId="$B2C_CLIENT_ID"
```

- [ ] App Service updated with B2C authority and client ID

---

### 6.5 Add MSAL to Angular

Install packages:

```bash
cd src/frontend
npm install @azure/msal-angular @azure/msal-browser
```

Update `environment.prod.ts` with B2C values:

```typescript
b2c: {
  tenantName: 'receiptwellb2c',
  clientId: '<frontend-spa-client-id>',
  userFlowSignUpSignIn: 'B2C_1_signupsignin',
  apiScope: 'https://receiptwellb2c.onmicrosoft.com/receiptwell-api/access_as_user'
}
```

Wire `MsalModule` in `src/frontend/src/app/app.config.ts` using the environment values.

- [ ] `@azure/msal-angular` and `@azure/msal-browser` installed
- [ ] Environment files populated with real B2C values
- [ ] MSAL providers added to `app.config.ts`
- [ ] Login flow tested end-to-end in browser

---

## Edge Cases — Extra Support Steps

| Scenario | Diagnosis | Fix |
|---|---|---|
| `az webapp create` rejects `"dotnet:9"` | CLI version difference | Run `az webapp list-runtimes --os windows \| grep -i dotnet` and use the exact string shown |
| First deploy 500: `CryptographicException` on Data Protection | keys.xml not yet created or RBAC not propagated | Wait 5 min after role assignment; check `data-protection` container exists |
| SWA build fails: `index.html not found in dist/receipt-well/browser` | Wrong `output_location` | Check `angular.json` → `architect.build.options.outputPath`; update SWA workflow to match |
| CORS error from browser | `AllowedOrigins` mismatch | Verify SWA hostname in App Service settings exactly matches — no trailing slash |
| `az storage container create` 403 | CLI user lacks Storage Blob Data Contributor | Add `--auth-mode key` flag, or assign role to your CLI user on the storage account |
| Role assignment 403 | Propagation delay (can take up to 5 min) | Wait and retry; confirm with `az role assignment list --assignee $MI_ID` |
| B2C login loop / redirect_uri mismatch | Redirect URI not registered in SPA app registration | Add exact redirect URI in B2C portal → SPA registration → Authentication |
| `az webapp log tail` drops connection | Known D1 Shared limitation (`az` CLI verbosity issue noted in infrastructure.md) | Use Azure portal → App Service → Log stream as fallback |
| GitHub Actions OIDC failure: `AADSTS70021` | Subject claim mismatch | Federated credential subject must exactly match: `repo:<org>/<repo>:ref:refs/heads/develop` |
| SWA auto-generated workflow builds API folder | `api_location` still set to `"api"` | Set `api_location: ""` in the auto-generated workflow file |

---

## Files Created / Modified

| File | Action |
|---|---|
| `src/backend/Directory.Packages.props` | Add 3 package versions |
| `src/backend/ReceiptWell.csproj` | Add 3 PackageReference entries |
| `src/backend/Program.cs` | Add Data Protection, CORS, JWT Bearer, middleware pipeline |
| `src/backend/appsettings.json` | Add AllowedOrigins, AzureStorage, AzureB2C config keys |
| `src/frontend/src/environments/environment.ts` | Create with dev defaults |
| `src/frontend/src/environments/environment.prod.ts` | Create with prod Azure URLs |
| `src/frontend/angular.json` | Add fileReplacements for production build |
| `src/frontend/staticwebapp.config.json` | Create SPA routing fallback |
| `.github/workflows/backend-deploy.yml` | Create OIDC deploy workflow |
| `.github/workflows/azure-static-web-apps-*.yml` | Fix `api_location: ""` and `output_location` |
| `src/frontend/package.json` | Add MSAL packages (Phase 6) |
| `src/frontend/src/app/app.config.ts` | Wire MSAL providers (Phase 6) |

---

## Verification Checklist (End State)

- [ ] `https://receipt-well-api.azurewebsites.net/weatherforecast` returns JSON
- [ ] `https://<swa-hostname>.azurestaticapps.net` loads Angular shell
- [ ] SPA routing fallback works (direct nav to sub-path returns shell, not 404)
- [ ] CORS: fetch from SWA URL to API URL succeeds in browser console
- [ ] Data Protection: `keys.xml` blob exists in `data-protection` container
- [ ] Managed Identity: App Service has no connection strings — all Azure access via identity
- [ ] CI/CD: push to `develop` auto-deploys both apps within ~5 minutes
- [ ] B2C login flow completes and returns a JWT (Phase 6)
