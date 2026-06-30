# CLAUDE.md

## Project

ReceiptWell — receipt upload, AI extraction, and search. Deployed to Azure App Service.

- Backend: `src/backend/` — see `@src/backend/.claude/CLAUDE.md` for .NET-specific conventions
- Frontend: `src/frontend/` — see `@src/frontend/.claude/CLAUDE.md` for Angular-specific conventions
- E2E tests: `src/e2e-tests/` — see `@src/e2e-tests/CLAUDE.md` for Playwright conventions

## Security guardrails

Never write the following into any git-tracked file:
- Hosting infrastructure details (resource names, hostnames, account names, container names, subscription or tenant IDs)
- Secrets, keys, tokens, passwords, or connection strings
- PII of any kind

## Workflow

Solo dev during MVP — commit directly to `develop`, no PR required.

## E2E tests

Playwright lives in `src/e2e-tests/`. Two prerequisites before running:

1. **Frontend must use `start:local`** — `npm start` uses `environment.ts` which has empty MSAL config; E2E auth won't work. Use `npm run start:local` (or let `playwright.config.ts`'s `webServer` block start it automatically).
2. **Auth storageState** — run the setup project once to create `playwright/.auth/user.json`: `npx playwright test --project=setup`. Reuse with `--no-deps` on subsequent runs.

Run a single spec: `npx playwright test tests/<spec>.spec.ts --project=chrome --no-deps`

## Stack

`@context/foundation/tech-stack.md`

No SQL database, no EF Core.
