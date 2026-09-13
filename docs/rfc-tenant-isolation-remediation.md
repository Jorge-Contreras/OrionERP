# RFC tenant-isolation remediation

## Enforced rule

Mutable business masters, operational rows, and configurable catalogs belong to one Orion company. Authentication identities and platform-orchestration metadata may remain global; a shared login receives separate company memberships and resolves to a company-specific employee.

## Managed migrations

- `20260912_rfc_tenant_isolation_expand` owns the data repair, composite tenant keys, per-RFC catalog copies, RLS expansion, and durable audit trail.
- `20260912_rfc_tenant_isolation_company_controls` adds fail-closed `CompanyId` RLS to the three accounting control tables discovered in the post-migration audit and documents the platform tables that are deliberately global.
- `20260912_rfc_tenant_isolation_fail_closed_principals` removes the legacy `dbo` shortcut from operational RLS predicates. Even the management-console SQL login must now present a coherent company/RFC (and hospitality site when applicable) session.

All three migrations support `ApplyChanges=0` preview and `ApplyChanges=1` apply through `OrionERP.DatabaseMigrator`. The immutable remapping evidence is stored in `orion.TenantIsolationMigrationAudit`; table classification is stored in `orion.TenantTableClassification`.

## Orion_Sandbox result (2026-09-12)

- Business partners: 117 OHM, 34 Bruno's, 151 total.
- Vendor remaps: `8 -> 112`, `49 -> 1190`, `109 -> 1191`, `110 -> 1192`, `111 -> 1193` for Bruno's rows only.
- Bodega Aurrera material links: 56 Bruno's on partner 112; 60 OHM on partner 8. OHM retains its 14 purchase orders on partner 8.
- Material 6933 was consolidated into Bruno's existing partner-112 link.
- Purchase orders 18 and 26 have no Bruno-to-LONDON room scope.
- Work order 431 now owns employee 96 (Bruno's MIGUEL).
- Cross-RFC legacy `Roles_Usuario` rows: 0.
- Migration audit rows for the primary repair: 316.
- Classified tables: 368. Enabled RLS filters: 205.
- Final validation state: `RFC_TENANT_ISOLATION_OK`.

The audit also found and repaired 21 Bruno logistics locations linked to OHM rooms and cross-company work-order participants. Those locations retained their own identity and inventory history but were detached from the foreign room.

## Validation

Run `database/validation/validate-rfc-tenant-isolation.sql` after either deployment or any future schema migration. It fails when:

- a new table has no explicit classification;
- an RFC/CompanyId-owned mutable table lacks an enabled filter and both insert/update blocks;
- a required composite tenant foreign key is missing, disabled, or untrusted;
- an operational RLS predicate restores an unscoped `dbo` shortcut;
- a vendor, room, employee, or legacy role crosses company ownership;
- the obsolete `dbo.BusinessPartnerRfcScope` table returns.

`RfcTenantIsolationSqlTests` exercises the database with identical vendor tax RFCs/names, units, allergens, payment codes, and work-order categories in OHM and Bruno's, then confirms independent IDs/edits and a blocked cross-company insert. `RfcTenantIsolationRemediationTests` also rejects new global company-facing catalog descriptors and any new raw SQL connection that has not been explicitly reviewed and scoped.

## Production result (2026-09-12)

All three migrations were previewed and applied to `grupocarpio` after the application deployment. The verified copy-only backup is:

`C:\Program Files\Microsoft SQL Server\MSSQL16.SQLEXPRESS\MSSQL\Backup\grupocarpio_before_rfc_tenant_isolation_20260913_031820_332aa488.bak`

Production contains one additional legitimate Bruno's vendor that was not present in the Sandbox baseline: partner 120, `MIGUEL MEDALLO SERRANO`. It was already scoped to Bruno's, has a vendor role, and has no material or purchase-order references. The migration preserved it as Bruno-owned.

- Business partners: 117 OHM, 35 Bruno's, 152 total.
- Migration audit rows for the primary repair: 319.
- Bodega Aurrera material links: 56 Bruno's on partner 112; 60 OHM on partner 8. OHM retains its 14 purchase orders on partner 8.
- Classified tables: 368. Tenant-owned tables: 306. Enabled RLS filters: 205.
- An unscoped connection sees zero business partners and work orders.
- Direct tenant probes show partner 8 only to OHM and partner 112/work order 431 only to Bruno's.
- A Bruno-scoped attempt to insert an OHM-owned partner failed with SQL error 33504.
- Final validation state: `RFC_TENANT_ISOLATION_OK`.

## Future production rollout

Deploy the application and migrations together in a maintenance window because the primary migration removes the obsolete partner scope bridge and makes new ownership columns mandatory.

1. Back up `grupocarpio` and retain the backup reference.
2. Run all three migrations in manifest order with `--mode preview --database grupocarpio`.
3. Review the preview counts and remap audit output against the sandbox baseline. Stop if the migration reports a provider/customer shared by multiple companies or any unresolved cross-company relationship.
4. Apply using the reviewed preview receipts, backup reference, and explicit production approval required by the migrator.
5. Run `database/validation/validate-rfc-tenant-isolation.sql` and authenticated smoke tests for OHM and Bruno's before reopening writes.

Future tenant-owned tables must be added to `orion.TenantTableClassification`, receive an explicit owner or tenant-aware parent key, and be covered by enabled fail-closed RLS in the same migration that creates them.
