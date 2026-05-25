# CLAUDE.md — Backend

ASP.NET Core 9.0 minimal API, C#.

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
