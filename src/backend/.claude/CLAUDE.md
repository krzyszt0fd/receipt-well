# CLAUDE.md — Backend

ASP.NET Core 9.0 minimal API, C#.

## Security guardrails

Never write hosting infrastructure details, secrets, keys, tokens, or PII into any git-tracked file.

**`appsettings.json` rules:**
- Contains only structural keys with empty or localhost defaults — no resource names, no account names, no hostnames, no tenant/subscription IDs.
- Production values are injected by Azure App Service app settings (managed by Terraform) and override `appsettings.json` at runtime.
- Local values that must not be committed go in `dotnet user-secrets` (`UserSecretsId` is set in `ReceiptWell.csproj`).
- **Exception — Azurite:** the Azurite emulator's fixed well-known account name (`devstoreaccount1`), its default localhost endpoints (`http://127.0.0.1:10000/10001/10002`), and this project's own blob/queue container names (e.g. `receipts`, `receipt-staging`, `receipt-extraction`, `data-protection`) are identical on every developer machine and carry no production-specific information — these may be committed as defaults. The Azurite account *key* (also a fixed, publicly documented Microsoft constant) follows the same exception. This does not extend to any real Azure resource's account name, hostname, or container name.

**`appsettings.Development.json` rules:**
- Same restrictions as `appsettings.json`. Not gitignored — treat it as public.

If a value belongs in config, read it from `IConfiguration`. Never hardcode resource names, URLs, or account identifiers in C# source files.

## Conventions

- **Namespace root:** `ReceiptWell` (not `receipt_well` — override the scaffolded default)
- **Nullable reference types are ON** — annotate every nullable reference with `?`; non-annotated types are assumed non-null
- **Implicit usings are ON** — do not add `using System;`, `using System.Collections.Generic;`, etc.
- **Zero-warning policy** — a build must not raise warnings
- **Use global using declarations in test projects** — global usings file should include testing framework namespace, assertion library namespaces, and any project-wide test helper namespaces. Nothing else.

## Package management

**Central package management is ON** — package versions are declared in `Directory.Packages.props`, not in individual `.csproj` files. When adding a `<PackageReference>`, omit the `Version` attribute; set the version only in `Directory.Packages.props`.

This project uses NuGet lock files with content hashes. After adding or updating any package version, run `dotnet restore` to rebuild the lock file before committing.

## Logging

### Log structure
- Use contextual logging: always log current identity ID and the entity ID being changed or accessed
- Always use source-generated logging

### Log levels
- Input validation failures → Info (not Warning).
- Caught exceptions with a working fallback path → Warning.
- Exceptions returned back to API client or broken background job → Error.
- App startup failure → Fatal/Critical.
- You must not log web requests or responses as this will be covered in a dedicated middleware.

## Testing

Run: `dotnet test src/backend/ReceiptWell.sln`.

**Boot requirement.** `ReceiptWellWebFactory` (`ReceiptWell.Tests/Infrastructure/`) boots the real app offline. Three config keys are read eagerly at startup *outside* any DI lambda — the factory supplies in-memory dummies for them; if you add another eager read, update the factory or the host won't start under test. The `SearchIndexInitializer` hosted service is removed for the same reason.

**Identity injection.** `TestAuthHandler` is the default auth scheme. It reads request headers: `X-Test-Oid: <oid>` → authenticated with that `oid`; `X-Test-No-Oid` → authenticated without `oid`; no header → 401 challenge. Only authentication is faked — the real policy decides 401 vs 403.

**Azure client substitutes** (`BlobServiceClient`, `SearchClient`, `SearchIndexClient`, `QueueClient`) are NSubstitute instances exposed on the factory. Use `DidNotReceiveWithAnyArgs()` to assert a rejected path performed no side effects.

**Filter assertions must be requirement-derived**, not an exact copy of the implementation string. Assert the filter contains the scoping field name and the caller's id — never `Assert.Equal($"UserId eq '{userId}'", filter)`.

## Maintain example endpoints collection

After modification to an existing endpoint contract or new one added or deleted, update example http request collection in `@../../receipt-well.http`.
