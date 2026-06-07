# Frontend

Angular SPA for ReceiptWell. Requires the backend running locally for full functionality.

## Prerequisites

- Node.js (see `.nvmrc` or `package.json` for version)
- Angular CLI: `npm install -g @angular/cli`
- Backend running on `https://localhost:7028` (see `src/backend/`)

## Local environment setup (first time)

Authentication credentials are never committed. Create a local environment file before running the app:

```
src/frontend/src/environments/environment.local.ts
```

Copy this template and fill in the values from your Entra app registration:

```typescript
export const environment = {
  production: false,
  apiUrl: 'https://localhost:7028',
  externalId: {
    authority: 'https://login.microsoftonline.com/<tenant-id>/v2.0',
    knownAuthority: 'login.microsoftonline.com',
    clientId: '<frontend-spa-client-id>',
    apiScope: 'api://<backend-client-id>/access_as_user'
  }
};
```

This file is gitignored and must be created manually on each machine.

## Running locally

```bash
ng serve --configuration=local
```

Open `http://localhost:4200/`. The app reloads on file changes.

> `ng serve` (without `--configuration=local`) starts without Entra credentials — the landing page renders but Sign In will fail.

## Running unit tests

```bash
ng test
```

## Building

Production build (used by CI):

```bash
ng build
```

Artifacts are written to `dist/`. The production configuration substitutes `__PLACEHOLDER__` values in `environment.prod.ts` via GitHub Actions variables before the build runs.
