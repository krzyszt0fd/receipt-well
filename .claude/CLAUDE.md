# CLAUDE.md

## Project

ReceiptWell — receipt upload, AI extraction, and search. Deployed to Azure App Service.

- Backend: `src/backend/` — see `@src/backend/CLAUDE.md` for .NET-specific conventions
- Frontend: `src/frontend/`

## Workflow

Solo dev during MVP — commit directly to `develop`, no PR required.

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
