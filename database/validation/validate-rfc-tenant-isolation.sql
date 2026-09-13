SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'orion.TenantTableClassification',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_expand')
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_company_controls')
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260912_rfc_tenant_isolation_fail_closed_principals')
  THROW 53500,'La expansión de aislamiento RFC no está aplicada.',1;

IF EXISTS
(
  SELECT 1
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
  LEFT JOIN orion.TenantTableClassification classification
    ON classification.SchemaName=schemaInfo.name AND classification.TableName=tableInfo.name
  WHERE tableInfo.is_ms_shipped=0 AND classification.TableName IS NULL
)
  THROW 53501,'Hay tablas nuevas sin clasificación de aislamiento.',1;

IF EXISTS
(
  SELECT 1
  FROM orion.TenantTableClassification classification
  WHERE classification.Classification='TENANT_OWNED'
    AND classification.OwnerColumn IN(N'Rfc',N'RFC',N'OwnerRfc',N'CompanyId',N'OrionCompanyId')
    AND NOT EXISTS
    (
      SELECT 1
      FROM sys.security_predicates predicateInfo
      JOIN sys.security_policies policyInfo ON policyInfo.object_id=predicateInfo.object_id AND policyInfo.is_enabled=1
      WHERE predicateInfo.target_object_id=OBJECT_ID(QUOTENAME(classification.SchemaName)+N'.'+QUOTENAME(classification.TableName))
        AND predicateInfo.predicate_type=0
    )
    AND NOT(classification.SchemaName=N'cfdi' AND classification.TableName IN(N'Emisor',N'Receptor'))
    AND classification.TableName NOT LIKE N'%Audit%'
    AND classification.TableName NOT LIKE N'%Backfill%'
    AND classification.TableName NOT LIKE N'%Log%'
)
  THROW 53502,'Una tabla operativa con dueño RFC no tiene filtro RLS.',1;

IF EXISTS
(
  SELECT 1
  FROM orion.TenantTableClassification classification
  JOIN sys.security_predicates predicateInfo
    ON predicateInfo.target_object_id=OBJECT_ID(QUOTENAME(classification.SchemaName)+N'.'+QUOTENAME(classification.TableName))
  JOIN sys.security_policies policyInfo ON policyInfo.object_id=predicateInfo.object_id AND policyInfo.is_enabled=1
  WHERE classification.Classification='TENANT_OWNED'
    AND classification.OwnerColumn IN(N'Rfc',N'RFC',N'OwnerRfc',N'CompanyId',N'OrionCompanyId')
  GROUP BY classification.SchemaName,classification.TableName
  HAVING SUM(CASE WHEN predicateInfo.predicate_type=0 THEN 1 ELSE 0 END)<1
      OR SUM(CASE WHEN predicateInfo.predicate_type=1 THEN 1 ELSE 0 END)<2
)
  THROW 53503,'Una tabla RFC no tiene filtro y ambos bloqueos RLS.',1;

IF OBJECT_DEFINITION(OBJECT_ID(N'logistica.fn_RfcAccessPredicate')) LIKE N'%USER_NAME()=N''dbo''%'
   OR OBJECT_DEFINITION(OBJECT_ID(N'rh.fn_RfcAccessPredicate')) LIKE N'%USER_NAME()=N''dbo''%'
   OR OBJECT_DEFINITION(OBJECT_ID(N'orion.fn_HospitalityScopePredicate')) LIKE N'%USER_NAME()=N''dbo''%'
  THROW 53514,'Una política operativa conserva el bypass global de dbo.',1;

DECLARE @RequiredForeignKeys TABLE(Name sysname PRIMARY KEY);
INSERT @RequiredForeignKeys VALUES
  (N'FK_BusinessPartnerRole_OwnerPartner'),
  (N'FK_BusinessPartnerCfdiProfile_OwnerPartner'),
  (N'FK_MaterialVendor_OwnerPartner'),
  (N'FK_PurchaseOrder_OwnerPartner'),
  (N'FK_VendorProfile_OwnerPartner'),
  (N'FK_AP_RecurringPayable_OwnerPartner'),
  (N'FK_HospitalityFiscalCustomer_OwnerPartner'),
  (N'FK_Material_Rfc_BaseUnit'),
  (N'FK_Material_Rfc_PurchaseUnit'),
  (N'FK_MaterialAllergen_Rfc_Allergen'),
  (N'FK_Transacciones_Rfc_FormaPago'),
  (N'FK_OrdenTrabajo_Rfc_Categoria'),
  (N'FK_PurchaseOrderRoomScope_CompanyRoom'),
  (N'FK_Location_CompanyRoom'),
  (N'FK_OrdenTrabajo_Rfc_Owner'),
  (N'FK_OrdenTrabajoParticipante_Rfc_Empleado'),
  (N'FK_Roles_Usuario_Rfc_Employee'),
  (N'FK_ROOM_OwnerCompanyProvider'),
  (N'FK_HospitalitySiteCustomer_CompanyCustomer');

IF EXISTS
(
  SELECT 1 FROM @RequiredForeignKeys required
  LEFT JOIN sys.foreign_keys foreignKeyInfo ON foreignKeyInfo.name=required.Name AND foreignKeyInfo.is_disabled=0 AND foreignKeyInfo.is_not_trusted=0
  WHERE foreignKeyInfo.object_id IS NULL
)
  THROW 53504,'Falta una llave foránea tenant-aware o no está confiada.',1;

DECLARE @EnabledFilterCount int=
(
  SELECT COUNT(*)
  FROM sys.security_predicates predicateInfo
  JOIN sys.security_policies policyInfo ON policyInfo.object_id=predicateInfo.object_id
  WHERE predicateInfo.predicate_type=0 AND policyInfo.is_enabled=1
);

BEGIN TRANSACTION;
BEGIN TRY
  CREATE TABLE #EnabledSecurityPolicies
  (
    SchemaName sysname NOT NULL,
    PolicyName sysname NOT NULL,
    PRIMARY KEY(SchemaName,PolicyName)
  );
  INSERT #EnabledSecurityPolicies
  SELECT OBJECT_SCHEMA_NAME(object_id),name FROM sys.security_policies WHERE is_enabled=1;
  DECLARE @DisablePolicies nvarchar(max);
  SELECT @DisablePolicies=STRING_AGG(CONVERT(nvarchar(max),N'ALTER SECURITY POLICY '+QUOTENAME(SchemaName)+N'.'+QUOTENAME(PolicyName)+N' WITH (STATE=OFF);'),CHAR(10))
  FROM #EnabledSecurityPolicies;
  IF @DisablePolicies IS NOT NULL EXEC sys.sp_executesql @DisablePolicies;

IF OBJECT_ID(N'dbo.BusinessPartnerRfcScope',N'U') IS NOT NULL
  THROW 53505,'El bridge BusinessPartnerRfcScope todavía es una tabla.',1;
IF EXISTS(SELECT 1 FROM dbo.BusinessPartner WHERE OwnerRfc IS NULL)
  THROW 53506,'Hay socios sin propietario.',1;
IF EXISTS(SELECT 1 FROM logistica.MaterialVendor link JOIN dbo.BusinessPartner partner ON partner.Id=link.BusinessPartnerId WHERE link.Rfc<>partner.OwnerRfc)
  THROW 53507,'Hay materiales ligados a proveedores de otra empresa.',1;
IF EXISTS(SELECT 1 FROM logistica.PurchaseOrder purchaseOrder JOIN dbo.BusinessPartner partner ON partner.Id=purchaseOrder.BusinessPartnerId WHERE purchaseOrder.Rfc<>partner.OwnerRfc)
  THROW 53508,'Hay órdenes de compra ligadas a proveedores de otra empresa.',1;
IF EXISTS(SELECT 1 FROM dbo.OrdenTrabajo workOrder JOIN dbo.Capital_Humano employee ON employee.ID=workOrder.OwnerEmployeeId WHERE workOrder.Rfc<>employee.RFC)
  THROW 53509,'Hay órdenes de trabajo ligadas a empleados de otra empresa.',1;
IF EXISTS(SELECT 1 FROM dbo.OrdenTrabajoParticipante participant JOIN dbo.OrdenTrabajo workOrder ON workOrder.Id=participant.OrdenTrabajoId JOIN dbo.Capital_Humano employee ON employee.ID=participant.EmployeeId WHERE workOrder.Rfc<>employee.RFC)
  THROW 53510,'Hay participantes de órdenes ligados a otra empresa.',1;
IF EXISTS(SELECT 1 FROM dbo.Roles_Usuario legacyRole JOIN dbo.Capital_Humano employee ON employee.ID=legacyRole.Usuario_ID WHERE legacyRole.RFC<>employee.RFC)
  THROW 53511,'Hay Roles_Usuario cruzados.',1;
IF EXISTS(SELECT 1 FROM logistica.PurchaseOrderRoomScope roomScope JOIN orion.Company company ON company.Rfc=roomScope.Rfc JOIN dbo.ROOM room ON room.ID=roomScope.RoomId WHERE room.OrionCompanyId<>company.CompanyId)
  THROW 53512,'Hay órdenes de compra con habitaciones de otra empresa.',1;
IF EXISTS(SELECT 1 FROM logistica.Location locationInfo JOIN orion.Company company ON company.Rfc=locationInfo.Rfc JOIN dbo.ROOM room ON room.ID=locationInfo.RoomId WHERE room.OrionCompanyId<>company.CompanyId)
  THROW 53513,'Hay ubicaciones logísticas con habitaciones de otra empresa.',1;

SELECT
  (SELECT COUNT(*) FROM orion.TenantTableClassification) ClassifiedTables,
  (SELECT COUNT(*) FROM orion.TenantTableClassification WHERE Classification='TENANT_OWNED') TenantOwnedTables,
  @EnabledFilterCount EnabledFilters,
  (SELECT COUNT(*) FROM dbo.BusinessPartner) BusinessPartners,
  (SELECT COUNT(*) FROM orion.TenantIsolationMigrationAudit WHERE MigrationId=N'20260912_rfc_tenant_isolation_expand') MigrationAuditRows,
  N'RFC_TENANT_ISOLATION_OK' Estado;

  ROLLBACK TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
