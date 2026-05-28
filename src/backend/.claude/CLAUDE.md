# CLAUDE.md — Backend

ASP.NET Core 9.0 minimal API, C#.

## Security guardrails

Never write hosting infrastructure details, secrets, keys, tokens, or PII into any git-tracked file.

**`appsettings.json` rules:**
- Contains only structural keys with empty or localhost defaults — no resource names, no account names, no hostnames, no tenant/subscription IDs.
- Production values are injected by Azure App Service app settings (managed by Terraform) and override `appsettings.json` at runtime.
- Local values that must not be committed go in `dotnet user-secrets` (`UserSecretsId` is set in `ReceiptWell.csproj`).

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

## Maintain example endpoints collection

After modification to an existing endpoint contract or new one added or deleted, update example http request collection in `@../../receipt-well.http`.
