# ReceiptWell

Receipt upload, AI-powered data extraction, and search — a solo MVP deployed to Azure.

Upload a photo of a receipt, get structured data (merchant, date, amount, line items) extracted automatically via an LLM, then search and filter your receipt history.

## Stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core 9 minimal API (C#) |
| Frontend | Angular SPA (TypeScript) |
| Auth | Microsoft Entra External ID (JWT bearer) |
| AI extraction | Anthropic / OpenAI .NET SDK |
| Hosting | Azure App Service (backend) + Azure Static Web Apps (frontend) |
| IaC | Terraform |
| CI/CD | GitHub Actions — auto-deploy on merge to `develop` |

## Folder structure

```
receipt-well/
├── src/
│   ├── backend/          # ASP.NET Core API — see src/backend/README.md
│   ├── frontend/         # Angular SPA     — see src/frontend/README.md
│   └── e2e-tests/        # Playwright E2E tests (Risk #5 async flow, Risk #6 tag-search UI)
├── infra/                # Terraform — Azure resources (App Service, Storage, Key Vault, SWA)
├── context/
│   ├── foundation/       # PRD, tech-stack, roadmap, lessons, infrastructure decisions
│   └── changes/          # Per-change planning and progress tracking
└── .github/              # CI/CD workflows
```

## Getting started

See the dedicated READMEs for local setup:

- **Backend** — [`src/backend/README.md`](src/backend/README.md): build, run, test commands
- **Frontend** — [`src/frontend/README.md`](src/frontend/README.md): local dev server, Entra auth config
- **E2E tests** — [`src/e2e-tests/`](src/e2e-tests/): Playwright; requires `npm run start:local` for the frontend and a one-time auth setup (`npx playwright test --project=setup` from `src/e2e-tests/`)

## Project context

Planning documents live under `context/foundation/`:

- [`prd.md`](context/foundation/prd.md) — product requirements
- [`roadmap.md`](context/foundation/roadmap.md) — ordered vertical slices
- [`tech-stack.md`](context/foundation/tech-stack.md) — stack rationale
- [`infrastructure.md`](context/foundation/infrastructure.md) — Azure deployment topology
