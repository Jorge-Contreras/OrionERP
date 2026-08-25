# Company-bound sessions

OrionERP binds every management-console login to exactly one active company. A
user who works for more than one company chooses the employment context after
their password and second factor have been validated. Changing company requires
signing out and signing in again.

## Source of truth

- `orion.Company` is the durable company directory. RFC values are immutable;
  companies are deactivated instead of deleted.
- `auth.AspNetUserCompanies` links one OrionERP account to each employer and,
  when available, to that RFC's Capital Humano row.
- `auth.AspNetUserCompanyRoles` grants operational roles within one membership.
- `Administrador` and `Arrendadores` are global roles. All other roles are
  company-scoped.
- `auth.CompanyAccessAudit` is append-only and records company, membership,
  employee-link, role, branding, and review actions.

`ApplicationUser.EmployeeId` and legacy RFC/account-wide role assignments remain
only for the rollback window. The application principal does not use them to
choose a company or employment.

## Authentication behavior

Password, lockout, and 2FA validation happen before an application cookie is
issued. A five-minute protected pending cookie carries only the company-selection
transaction. The selected membership and company are checked again before the
final cookie is created.

The final identity contains one `rfc`, the matching `employee_id` and
`employee_rfc` when linked, global roles, and roles for the selected membership.
The `company_session_version` claim rejects cookies from the earlier multi-RFC
model. Company, membership, employment-link, or role changes rotate the affected
security stamp.

All pages use the read-only current-company context. URL/query RFCs, downloads,
HTTP endpoints, and SignalR connections must match the login company; a mismatch
returns access denied and never changes the session.

## Administration

- `/admin/empresas` manages company names, active status, and logos. Logos may be
  PNG, JPEG, or WebP, at most 2 MB, and are resized to fit 1024×1024.
- `/admin/seguridad` manages global roles separately from company memberships,
  company roles, and employer-specific Capital Humano links.
- Missing, inactive, duplicate, and cross-RFC employee relationships are shown
  as access-review issues. A reviewer explicitly completes migrated multi-company
  records.
- Reserved runtime claims (`rfc`, `employee_id`, `employee_rfc`, roles, and the
  session-version claim) cannot be edited in the generic claims grid.

## Migration and rollback window

The idempotent migration is:

`src/OrionERP.Infrastructure/Features/Auth/Sql/20260824_company_bound_sessions.sql`

It defaults to preview mode and rolls its transaction back. The deployment
connection must set SQL session context key `ApplyChanges` to `1` before running
the file to commit it. Every environment must be previewed and reviewed before
application. Production must be backed up first.

The migration preserves effective operational access by copying each existing
company role to every current membership, flags multi-company access for review,
and performs cross-employer CURP linking only for a unique active match. Ambiguous
records stay unlinked. A later cleanup release may remove legacy RFC claims,
account-wide company roles, and the singular account employee column after the
new session model has remained stable.
