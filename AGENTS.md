# Agent Notes

## Project Context

OrionERP and the SQL Server databases are part of the same project surface. Codex should treat the Blazor/.NET repo and the SQL Server data model as integrated parts of OrionERP, not as separate systems.

The SQL Server databases that go side by side with this repo are:

- `grupocarpio`: production database.
- `Orion_Sandbox`: development and validation database.

## Public Platform Boundaries

This repository contains two different web platforms that are publicly exposed through Cloudflare tunnels:

- OrionERP management console: `src/OrionERP.Web`, published by `Publish-prod.ps1` as the `OrionERP` service, publicly exposed at `https://orionerp.orion.land`. When the user says `OrionERP`, assume they mean this management console unless they explicitly mention another project.
- Bonhomia public website: `src/OrionERP.Bonhomia.Web`, published by `Publish-Bonhomia-prod.ps1` as the `OrionERP.Bonhomia` service, publicly exposed at `https://bonhomiasuites.com`. When the user says `public website`, assume they mean this Bonhomia-facing website unless they explicitly say otherwise.

The two platforms share application, infrastructure, and SQL Server data model code, so route UI and configuration changes to the correct web project while keeping shared data/workflow changes consistent across both surfaces when needed.

Public base URLs are operational configuration and are not secrets. Keep public origins in appsettings when useful for local defaults or generated links, but keep passwords, API keys, PayPal secrets, Graph secrets, and SQL Server credentials in user secrets, environment variables, deployment configuration, or another approved private secret store.

## Bruno's Menu Images

The canonical Bruno's menu images live outside this repository under
`C:\Users\Orion\Grupo Carpio Dropbox\Grupo Orion\Bruno's\assets\menus`, but the
OrionERP `/menus` page serves its runtime copies from
`src/OrionERP.Web/wwwroot/Images/Brunos/Menus`.

Whenever the user asks to update a current Bruno's menu image, update both the
canonical image and its matching repository copy in `wwwroot`. Preserve the
stable filenames `menu-principal.png` and `menu-bebidas.png`, follow the
canonical folder's archive procedure, and verify that the canonical and
repository copies have matching hashes after the change. Do not rely solely on
the build-time copy target to leave the working tree ready.

## UI Control Design Defaults

When building or changing UI in either web platform, treat common controls as intentional product surfaces. Text fields, selectors, dropdowns, date/month inputs, toggles, checkboxes, scrollbars, tabs, buttons, and table controls should look modern, polished, and consistent with the surrounding app instead of falling back to dated raw browser defaults.

Prefer accessible native controls and semantic HTML first, then style their visible states thoughtfully: default, hover, focus, active, disabled, loading, validation, and overflow/scroll states. Keep controls compact and operational for OrionERP workflows, but make sure they feel current and easy to use on both desktop and mobile.

## SQL Server Connection

The application uses `ConnectionStrings:OrionDb` as the canonical database connection setting. For environment-based configuration, use `ASPNETCORE_ConnectionStrings__OrionDb`.

Production and development use the same SQL Server endpoint and login pattern; only the database name should change:

- Production example: `Server=bonhomia.ddns.net,1433;Database=grupocarpio;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;`
- Development example: `Server=bonhomia.ddns.net,1433;Database=Orion_Sandbox;User Id=orion;Password=<redacted>;TrustServerCertificate=True;Encrypt=True;`

Do not store real SQL Server passwords or secrets in this file. Keep secrets in user secrets, environment variables, deployment configuration, or another approved private secret store.

## CRUD Guidance

When work touches database tables or data workflows, Codex should evaluate SQL/table CRUD and Blazor-side CRUD together.

Default policy:

- Let SQL Server handle raw data work, filtering, joins, relationships, aggregation, and data-heavy operations whenever that is the better fit.
- Let Blazor own the user experience, workflow orchestration, validation surfaces, and interaction design.
- Choose direct SQL/table CRUD or Blazor-side CRUD case by case based on data shape, user workflow, safety, maintainability, and production risk.

## Development Testing

When testing OrionERP in the development environment, Codex should sign in with the dedicated testing user:

- Email: `admin@orionerp.local`
- Password: `Orion2021`

This account is intended for browser smoke tests and development validation of implemented features.
