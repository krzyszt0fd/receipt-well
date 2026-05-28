# CLAUDE.md

## Project

ReceiptWell — receipt upload, AI extraction, and search. Deployed to Azure App Service.

- Backend: `src/backend/` — see `@src/backend/.claude/CLAUDE.md` for .NET-specific conventions
- Frontend: `src/frontend/` — see `@src/fronted/.claude/CLAUDE.md` for Angular-specific conventions

## Security guardrails

Never write the following into any git-tracked file:
- Hosting infrastructure details (resource names, hostnames, account names, container names, subscription or tenant IDs)
- Secrets, keys, tokens, passwords, or connection strings
- PII of any kind

## Workflow

Solo dev during MVP — commit directly to `develop`, no PR required.

## Stack

`@context/foundation/tech-stack.md`

No SQL database, no EF Core.
