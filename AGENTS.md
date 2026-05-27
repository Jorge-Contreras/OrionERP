# Agent Notes

## Project Context

OrionERP and the SQL Server databases are part of the same project surface. Codex should treat the Blazor/.NET repo and the SQL Server data model as integrated parts of OrionERP, not as separate systems.

The SQL Server databases that go side by side with this repo are:

- `grupocarpio`: production database.
- `Orion_Sandbox`: development and validation database.

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
