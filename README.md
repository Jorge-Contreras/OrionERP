# OrionERP

Modular monolith for OrionERP, Bonhomía Suites, and Bruno's Garden. Tech: .NET 10 (ASP.NET Core + Blazor Server), SQL Server, Dapper/EF Core.

## Production publishing

`Publish-All-prod.ps1` is the canonical production entry point. By default it publishes the OrionERP management console, the Bonhomia public website, and the Bruno's public website with one elevation prompt. It requires a clean `main` branch synchronized with `origin/main`, preserves deployment configuration and application data, restarts each Windows service, and verifies the local application before continuing.

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\Publish-All-prod.ps1
```

Use `-Applications OrionERP`, `-Applications Bonhomia`, or `-Applications Bruno` for a targeted publish. Use `-ValidateOnly` to publish all selected projects to a temporary directory without touching services or production folders. The individual `Publish-*-prod.ps1` files remain component-level workers for troubleshooting and targeted maintenance.
