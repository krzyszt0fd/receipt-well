# CLAUDE.md

## Project

ReceiptWell — receipt upload, AI extraction, and search. ASP.NET Core 9.0 minimal API, C#, deployed to Azure App Service.

## Conventions

- **Namespace root:** `ReceiptWell` (not `receipt_well` — override the scaffolded default)
- **Nullable reference types are ON** — annotate every nullable reference with `?`; non-annotated types are assumed non-null
- **Implicit usings are ON** — do not add `using System;`, `using System.Collections.Generic;`, etc.
- **Zero-warning policy** - a build must not raise warnings
- **Use global using declarations in test projects** - global usings file should include testing framework namespace, assertion library namespaces, and any project-wide test helper namespaces. Nothing else.

## Package management

**Central package management is ON** — package versions are declared in `Directory.Packages.props`, not in individual `.csproj` files. When adding a `<PackageReference>`, omit the `Version` attribute; set the version only in `Directory.Packages.props`.

This project uses NuGet lock files with content hashes. After adding or updating any package version, run `dotnet restore` to rebuild the lock file before committing.

## Logging
Always log application flow:
- Use Information level for reporting normal application flow e.g. data successfully saved, image processed etc. Including validation failures.
- Use warning for exceptions that were caught and handled by alternative application path.
- Log error whenever the exceptions breaks a process (e.g. background processing) or results in 5xx error returned to the API client.
- Log fatal/critical if application can not start or crashes
Log structure:
- Use contextual logging: always log current identity ID and the entity ID being changed or accessed
- Always use source-generated logging

## Maintain example endpoints collection
After modification to an existing endpoint contract or new one added or deleted, update example http request collection in @receipt-well.http.

## Workflow

Solo dev during MVP — commit directly to `develop`, no PR required.

## Commands

```bash
dotnet run                                              # http://localhost:5191 | https://localhost:7028
dotnet test --filter "FullyQualifiedName~<TestName>"   # xUnit single test
```

## Stack

@context/foundation/tech-stack.md

| Concern | Technology |
|---|---|
| AI / LLM orchestration | Semantic Kernel |
| File storage | Azure Blob Storage, SAS token access |
| Search | Azure AI Search |
| Auth | JWT bearer (ASP.NET Core middleware) |
| Background processing | Azure functions |

No SQL database, no EF Core.
