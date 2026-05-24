# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ReceiptWell — receipt upload, AI extraction, and search. ASP.NET Core 9.0 minimal API, C#, deployed to Azure App Service.

## Commands

```bash
dotnet build
dotnet run                                              # http://localhost:5191 | https://localhost:7028
dotnet test --filter "FullyQualifiedName~<TestName>"   # xUnit single test
dotnet restore                                          # required after any .csproj package change
```

## Conventions

- **Namespace root:** `ReceiptWell` (not `receipt_well` — override the scaffolded default)
- **Nullable reference types are ON** — annotate every nullable reference with `?`; non-annotated types are assumed non-null
- **Implicit usings are ON** — do not add `using System;`, `using System.Collections.Generic;`, etc.

## Stack (planned, not yet wired)

| Concern | Technology |
|---|---|
| AI / LLM orchestration | Semantic Kernel |
| File storage | Azure Blob Storage, SAS token access |
| Search | Azure Cognitive Search |
| Auth | JWT bearer (ASP.NET Core middleware) |

No SQL database, no EF Core.

## Package locks

This project uses NuGet lock files with content hashes. After adding or updating any `<PackageReference>` in `.csproj`, run `dotnet restore` to rebuild the lock file before committing.

## Workflow

Solo dev during MVP — commit directly to `develop`, no PR required.
