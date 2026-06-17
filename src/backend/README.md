# ReceiptWell — Backend

ASP.NET Core 9.0 minimal API, C#.

## Projects

- `ReceiptWell.Web/` — ASP.NET Core 9.0 minimal API
- `ReceiptWell.Core/` — shared model library (`ReceiptDocument`, `ReceiptStatus`)
- `ReceiptWell.Functions/` — Azure Functions isolated-worker project; consumes the extraction queue and calls GPT-4o to enrich receipts

## Commands

```bash
dotnet build ReceiptWell.sln                                                        # build the whole solution
dotnet run --project ReceiptWell.Web/ReceiptWell.Web.csproj -lp "https"            # http://localhost:5191 | https://localhost:7028
dotnet run --project ReceiptWell.Functions/ReceiptWell.Functions.csproj            # starts the Functions host (requires Azurite, see below)
dotnet restore ReceiptWell.sln                                                      # restore packages and refresh lock files
```

## Running ReceiptWell.Functions locally

The Functions project requires:

- **[Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4** (`func` CLI) — needed for the local Functions tooling/extensions even when launching via `dotnet run`.
- **[Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)** running locally — `AzureWebJobsStorage` is set to `UseDevelopmentStorage=true` in `local.settings.json`, and the host's internal queue/blob bindings will fail to start without a running Azurite instance (default ports `10000`/`10001`/`10002`).
- A `ReceiptWell.Functions/local.settings.json` (gitignored, create it locally) populated with real `AzureSearch__*` and `AzureOpenAI__*` values — e.g. via `terraform output` from `infra/` — plus the Azurite connection string for `AzureStorage__ConnectionString`.

Start Azurite, then either `func start` or `dotnet run` in `ReceiptWell.Functions/` — both boot the same embedded host and pick up `local.settings.json`.
