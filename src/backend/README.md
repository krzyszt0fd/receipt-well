# ReceiptWell — Backend

ASP.NET Core 9.0 minimal API, C#.

## Projects

- `ReceiptWell.Web/` — ASP.NET Core 9.0 minimal API
- `ReceiptWell.Core/` — shared model library (`ReceiptDocument`, `ReceiptStatus`)

## Commands

```bash
dotnet build ReceiptWell.sln                                                        # build the whole solution
dotnet run --project ReceiptWell.Web/ReceiptWell.Web.csproj -lp "https"            # http://localhost:5191 | https://localhost:7028
dotnet restore ReceiptWell.sln                                                      # restore packages and refresh lock files
```
