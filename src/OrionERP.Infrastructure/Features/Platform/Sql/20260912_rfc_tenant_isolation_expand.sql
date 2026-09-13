SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit = 0;
DECLARE @MigrationId nvarchar(200) = N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128) = '$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64) = N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64) = NULL;
DECLARE @OhmRfc varchar(50) = 'OHM191112Q26';
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';

IF @ApplyChangesInput NOT LIKE N'$' + N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0', N'1')
    THROW 53400, 'ApplyChanges debe ser 0 o 1.', 1;
  SET @ApplyChanges = CONVERT(bit, @ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$' + N'(%'
   OR @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'grupocarpio', N'Orion_CutoverValidation_20260908')
  THROW 53401, 'La base esperada no está autorizada para esta migración.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 53402, 'La conexión no apunta a la base declarada.', 1;
IF @MigrationId <> N'20260912_rfc_tenant_isolation_expand'
  THROW 53403, 'MigrationId incorrecto.', 1;
IF @MigrationChecksum LIKE '$' + '(%' OR LEN(@MigrationChecksum) <> 64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53404, 'MigrationChecksum inválido.', 1;
IF @AppVersionInput NOT LIKE N'$' + N'(%'
  SET @AppVersion = NULLIF(LTRIM(RTRIM(@AppVersionInput)), N'');
IF OBJECT_ID(N'orion.SchemaMigration', N'U') IS NULL
   OR OBJECT_ID(N'logistica.RfcSecurityPolicy', N'SP') IS NULL
   OR OBJECT_ID(N'logistica.fn_RfcAccessPredicate', N'IF') IS NULL
   OR OBJECT_ID(N'dbo.BusinessPartnerRfcScope', N'U') IS NULL
  THROW 53405, 'Falta la plataforma RFC o el bridge de socios requerido por la expansión.', 1;
IF NOT EXISTS (SELECT 1 FROM orion.Company WHERE Rfc=@OhmRfc AND IsActive=1)
   OR NOT EXISTS (SELECT 1 FROM orion.Company WHERE Rfc=@BrunoRfc AND IsActive=1)
  THROW 53406, 'OHM y Bruno deben existir como empresas activas.', 1;

DECLARE @ExistingChecksum char(64) =
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53407, 'El mismo MigrationId ya existe con otro checksum.', 1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRANSACTION;
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:RfcTenantIsolation:Expand',@LockMode=N'Exclusive',
    @LockOwner=N'Transaction',@LockTimeout=15000;
IF @LockResult<0 THROW 53408,'No fue posible obtener el bloqueo de migración.',1;

/* RLS is disabled only inside this transaction so ownership can be reconciled
   across every company and site. XACT_ABORT guarantees that it is never left off. */
IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1)
  THROW 53421,'La política de Hospedaje debe estar activa antes de migrar.',1;
CREATE TABLE #EnabledSecurityPolicies(SchemaName sysname NOT NULL,PolicyName sysname NOT NULL,PRIMARY KEY(SchemaName,PolicyName));
INSERT #EnabledSecurityPolicies
SELECT OBJECT_SCHEMA_NAME(object_id),name FROM sys.security_policies WHERE is_enabled=1;
DECLARE @DisablePolicies nvarchar(max);
SELECT @DisablePolicies=STRING_AGG(CONVERT(nvarchar(max),N'ALTER SECURITY POLICY '+QUOTENAME(SchemaName)+N'.'+QUOTENAME(PolicyName)+N' WITH (STATE=OFF);'),CHAR(10))
FROM #EnabledSecurityPolicies;
EXEC sys.sp_executesql @DisablePolicies;

IF OBJECT_ID(N'orion.TenantIsolationMigrationAudit',N'U') IS NULL
BEGIN
  CREATE TABLE orion.TenantIsolationMigrationAudit
    (
      AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_orion_TenantIsolationMigrationAudit PRIMARY KEY,
      MigrationId nvarchar(200) NOT NULL,
      EntityType varchar(60) NOT NULL,
      ActionCode varchar(60) NOT NULL,
      SourceId nvarchar(100) NULL,
      TargetId nvarchar(100) NULL,
      SourceOwnerRfc varchar(50) NULL,
      TargetOwnerRfc varchar(50) NULL,
      Details nvarchar(2000) NULL,
      RecordedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_orion_TenantIsolationMigrationAudit_RecordedAtUtc DEFAULT SYSUTCDATETIME()
    );
  CREATE INDEX IX_orion_TenantIsolationMigrationAudit_Migration
    ON orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,AuditId);
END;

/* SQL Server compiles a batch before executing ALTER TABLE. Add every expansion
   column here, then cross a batch boundary while preserving this transaction. */
ALTER TABLE dbo.BusinessPartner ADD OwnerRfc varchar(50) NULL;
ALTER TABLE dbo.BusinessPartnerRole ADD Rfc varchar(50) NULL;
ALTER TABLE dbo.BusinessPartnerCfdiProfile ADD Rfc varchar(50) NULL;
ALTER TABLE orion.HospitalityFiscalCustomer ADD Rfc varchar(50) NULL;
ALTER TABLE logistica.UnitOfMeasure ADD Rfc varchar(50) NULL;
ALTER TABLE logistica.UnitConversion ADD Rfc varchar(50) NULL;
ALTER TABLE logistica.Allergen ADD Rfc varchar(50) NULL;
ALTER TABLE dbo.Formas_Pago ADD Rfc varchar(50) NULL;
ALTER TABLE dbo.OrdenTrabajoCategoria ADD Rfc varchar(50) NULL;
ALTER TABLE logistica.PurchaseOrderRoomScope ADD CompanyId bigint NULL,SiteId bigint NULL;
ALTER TABLE logistica.Location ADD CompanyId bigint NULL,RoomSiteId bigint NULL;
ALTER TABLE dbo.OrdenTrabajoParticipante ADD Rfc varchar(50) NULL;
ALTER TABLE dbo.Proveedores ADD OwnerCompanyId bigint NULL,OwnerRfc varchar(50) NULL;
ALTER TABLE dbo.Clientes ADD OwnerCompanyId bigint NULL,OwnerRfc varchar(50) NULL;
GO

DECLARE @ApplyChangesInput nvarchar(20) = N'$(ApplyChanges)';
DECLARE @ApplyChanges bit = CASE WHEN @ApplyChangesInput=N'1' THEN 1 ELSE 0 END;
DECLARE @MigrationId nvarchar(200) = N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128) = '$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64) = N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64) = CASE WHEN @AppVersionInput LIKE N'$'+N'(%' THEN NULL ELSE NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'') END;
DECLARE @OhmRfc varchar(50) = 'OHM191112Q26';
DECLARE @BrunoRfc varchar(50) = 'BRUNOS260707L26';

BEGIN TRY

  /* ---------- Business partners: one row, one owning RFC ---------- */
  ;WITH ScopeOwners AS
  (
    SELECT BusinessPartnerId,MIN(Rfc) OwnerRfc,COUNT(DISTINCT Rfc) OwnerCount
    FROM dbo.BusinessPartnerRfcScope
    WHERE IsActive=1
    GROUP BY BusinessPartnerId
  )
  UPDATE partner
  SET OwnerRfc=CASE
    WHEN partner.Id=112 THEN @BrunoRfc
    WHEN partner.Id IN(8,49,109,110,111) THEN @OhmRfc
    WHEN scopeInfo.OwnerCount=1 THEN scopeInfo.OwnerRfc
    ELSE @OhmRfc END
  FROM dbo.BusinessPartner partner
  LEFT JOIN ScopeOwners scopeInfo ON scopeInfo.BusinessPartnerId=partner.Id;

  INSERT orion.TenantIsolationMigrationAudit
    (MigrationId,EntityType,ActionCode,SourceId,TargetId,TargetOwnerRfc,Details)
  SELECT @MigrationId,'BusinessPartner','INFER_OWNER',CONVERT(nvarchar(100),partner.Id),CONVERT(nvarchar(100),partner.Id),partner.OwnerRfc,
    CASE WHEN scopeInfo.BusinessPartnerId IS NULL THEN N'Sin bridge previo; propietario acordado OHM.'
         WHEN scopeInfo.OwnerCount>1 THEN N'Bridge multiempresa corregido; el ID original permanece con OHM.'
         ELSE N'Propietario derivado del único bridge activo.' END
  FROM dbo.BusinessPartner partner
  LEFT JOIN
  (
    SELECT BusinessPartnerId,COUNT(DISTINCT Rfc) OwnerCount
    FROM dbo.BusinessPartnerRfcScope WHERE IsActive=1 GROUP BY BusinessPartnerId
  ) scopeInfo ON scopeInfo.BusinessPartnerId=partner.Id;

  IF EXISTS (SELECT 1 FROM dbo.BusinessPartner partner LEFT JOIN orion.Company company ON company.Rfc=partner.OwnerRfc WHERE company.Rfc IS NULL)
    THROW 53409,'Hay socios cuyo propietario no corresponde a orion.Company.',1;

  CREATE TABLE #VendorMap(SourceId int NOT NULL PRIMARY KEY,TargetId int NOT NULL UNIQUE);
  INSERT #VendorMap(SourceId,TargetId) VALUES(8,112);

  MERGE dbo.BusinessPartner AS target
  USING
  (
    SELECT source.Id SourceId,source.PartnerName,source.Rfc,source.Email,source.Phone,
      source.Street,source.Neighborhood,source.City,source.[State],source.PostalCode,
      source.BusinessLine,source.Notes,source.IsActive
    FROM dbo.BusinessPartner source
    WHERE source.Id IN(49,109,110,111)
  ) AS source
  ON 1=0
  WHEN NOT MATCHED THEN
    INSERT(LegacyProveedorId,PartnerName,Rfc,Email,Phone,Street,Neighborhood,City,[State],PostalCode,
      BusinessLine,Notes,IsActive,CreatedAt,UpdatedAt,OwnerRfc)
    VALUES(NULL,source.PartnerName,source.Rfc,source.Email,source.Phone,source.Street,source.Neighborhood,
      source.City,source.[State],source.PostalCode,source.BusinessLine,source.Notes,source.IsActive,
      SYSUTCDATETIME(),SYSUTCDATETIME(),@BrunoRfc)
  OUTPUT source.SourceId,inserted.Id INTO #VendorMap(SourceId,TargetId);

  INSERT orion.TenantIsolationMigrationAudit
    (MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  SELECT @MigrationId,'BusinessPartner','CLONE_VENDOR',CONVERT(nvarchar(100),map.SourceId),CONVERT(nvarchar(100),map.TargetId),
    @OhmRfc,@BrunoRfc,CASE WHEN map.SourceId=8 THEN N'Reutiliza el registro Bruno existente 112.' ELSE N'Clon Bruno separado del proveedor OHM.' END
  FROM #VendorMap map;

  INSERT dbo.BusinessPartnerRfcScope(Rfc,BusinessPartnerId,CreatedBy)
  SELECT @BrunoRfc,map.TargetId,'20260912_rfc_tenant_isolation_expand'
  FROM #VendorMap map
  WHERE NOT EXISTS(SELECT 1 FROM dbo.BusinessPartnerRfcScope scope WHERE scope.Rfc=@BrunoRfc AND scope.BusinessPartnerId=map.TargetId);

  INSERT dbo.BusinessPartnerRole(BusinessPartnerId,RoleCode)
  SELECT map.TargetId,roleMap.RoleCode
  FROM #VendorMap map
  JOIN dbo.BusinessPartnerRole roleMap ON roleMap.BusinessPartnerId=map.SourceId
  WHERE NOT EXISTS(SELECT 1 FROM dbo.BusinessPartnerRole existing WHERE existing.BusinessPartnerId=map.TargetId AND existing.RoleCode=roleMap.RoleCode);

  /* Preserve the newer Bruno history on material 6933 and fill only empty
     commercial fields from the source link before deleting the duplicate. */
  UPDATE logistica.MaterialVendor
  SET IsPrimary=0,UpdatedAt=SYSUTCDATETIME()
  WHERE Rfc=@BrunoRfc AND MaterialId=6933 AND BusinessPartnerId=8;
  UPDATE target
  SET IsPrimary=1,
      IsActive=CONVERT(bit,CASE WHEN target.IsActive=1 OR source.IsActive=1 THEN 1 ELSE 0 END),
      VendorCode=COALESCE(target.VendorCode,source.VendorCode),
      PurchaseQuantity=COALESCE(target.PurchaseQuantity,source.PurchaseQuantity),
      PurchaseIncrement=COALESCE(target.PurchaseIncrement,source.PurchaseIncrement),
      PurchaseUnitId=COALESCE(target.PurchaseUnitId,source.PurchaseUnitId),
      PurchaseLink=COALESCE(target.PurchaseLink,source.PurchaseLink),
      LastUnitPrice=COALESCE(target.LastUnitPrice,source.LastUnitPrice),
      LastPurchaseDate=COALESCE(target.LastPurchaseDate,source.LastPurchaseDate),
      Notes=COALESCE(target.Notes,source.Notes),
      UpdatedAt=SYSUTCDATETIME()
  FROM logistica.MaterialVendor target
  JOIN logistica.MaterialVendor source ON source.Rfc=target.Rfc AND source.MaterialId=target.MaterialId
  WHERE target.Rfc=@BrunoRfc AND target.MaterialId=6933
    AND target.BusinessPartnerId=112 AND source.BusinessPartnerId=8;

  DELETE source
  OUTPUT @MigrationId,'MaterialVendor','CONSOLIDATE_LINK',CONVERT(nvarchar(100),deleted.Id),N'112',@OhmRfc,@BrunoRfc,
    N'Material 6933 consolidado en el vínculo existente del proveedor 112.'
  INTO orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  FROM logistica.MaterialVendor source
  WHERE source.Rfc=@BrunoRfc AND source.MaterialId=6933 AND source.BusinessPartnerId=8;

  UPDATE materialVendor
  SET BusinessPartnerId=map.TargetId,UpdatedAt=SYSUTCDATETIME()
  OUTPUT @MigrationId,'MaterialVendor','REMAP_VENDOR',CONVERT(nvarchar(100),deleted.Id),CONVERT(nvarchar(100),inserted.BusinessPartnerId),
    @OhmRfc,@BrunoRfc,N'Vendor link remapeado al socio exclusivo de Bruno.'
  INTO orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  FROM logistica.MaterialVendor materialVendor
  JOIN #VendorMap map ON map.SourceId=materialVendor.BusinessPartnerId
  WHERE materialVendor.Rfc=@BrunoRfc;

  UPDATE purchaseOrder
  SET BusinessPartnerId=map.TargetId,UpdatedAt=SYSUTCDATETIME(),UpdatedBy='20260912 tenant isolation'
  OUTPUT @MigrationId,'PurchaseOrder','REMAP_VENDOR',CONVERT(nvarchar(100),deleted.Id),CONVERT(nvarchar(100),inserted.BusinessPartnerId),
    @OhmRfc,@BrunoRfc,N'Orden Bruno remapeada al socio exclusivo de Bruno.'
  INTO orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  FROM logistica.PurchaseOrder purchaseOrder
  JOIN #VendorMap map ON map.SourceId=purchaseOrder.BusinessPartnerId
  WHERE purchaseOrder.Rfc=@BrunoRfc;

  /* VendorProfile is already RFC-scoped. Merge Bodega into 112, then move the
     remaining Bruno profiles to their clones. */
  UPDATE target
  SET PaymentTerms=COALESCE(target.PaymentTerms,source.PaymentTerms),
      DefaultLeadTimeDays=COALESCE(target.DefaultLeadTimeDays,source.DefaultLeadTimeDays),
      Notes=COALESCE(target.Notes,source.Notes),
      IsApproved=CONVERT(bit,CASE WHEN target.IsApproved=1 OR source.IsApproved=1 THEN 1 ELSE 0 END),
      UpdatedAt=SYSUTCDATETIME()
  FROM logistica.VendorProfile target
  JOIN logistica.VendorProfile source ON source.Rfc=target.Rfc
  WHERE target.Rfc=@BrunoRfc AND target.BusinessPartnerId=112 AND source.BusinessPartnerId=8;
  DELETE FROM logistica.VendorProfile WHERE Rfc=@BrunoRfc AND BusinessPartnerId=8;
  UPDATE profile SET BusinessPartnerId=map.TargetId,UpdatedAt=SYSUTCDATETIME()
  FROM logistica.VendorProfile profile JOIN #VendorMap map ON map.SourceId=profile.BusinessPartnerId
  WHERE profile.Rfc=@BrunoRfc AND map.SourceId<>8;

  UPDATE roleMap SET Rfc=partner.OwnerRfc
  FROM dbo.BusinessPartnerRole roleMap JOIN dbo.BusinessPartner partner ON partner.Id=roleMap.BusinessPartnerId;
  ALTER TABLE dbo.BusinessPartnerRole ALTER COLUMN Rfc varchar(50) NOT NULL;

  UPDATE profile SET Rfc=partner.OwnerRfc
  FROM dbo.BusinessPartnerCfdiProfile profile JOIN dbo.BusinessPartner partner ON partner.Id=profile.BusinessPartnerId;
  ALTER TABLE dbo.BusinessPartnerCfdiProfile ALTER COLUMN Rfc varchar(50) NOT NULL;

  UPDATE fiscalCustomer SET Rfc=company.Rfc
  FROM orion.HospitalityFiscalCustomer fiscalCustomer JOIN orion.Company company ON company.CompanyId=fiscalCustomer.CompanyId;
  IF EXISTS(SELECT 1 FROM orion.HospitalityFiscalCustomer fiscalCustomer JOIN dbo.BusinessPartner partner ON partner.Id=fiscalCustomer.BusinessPartnerId WHERE fiscalCustomer.Rfc<>partner.OwnerRfc)
    THROW 53410,'Un cliente fiscal de Hospedaje apunta a un socio de otra empresa.',1;
  ALTER TABLE orion.HospitalityFiscalCustomer ALTER COLUMN Rfc varchar(50) NOT NULL;

  ALTER TABLE dbo.BusinessPartner ALTER COLUMN OwnerRfc varchar(50) NOT NULL;
  ALTER TABLE dbo.BusinessPartner ADD CONSTRAINT FK_BusinessPartner_OwnerCompany FOREIGN KEY(OwnerRfc) REFERENCES orion.Company(Rfc);
  CREATE UNIQUE INDEX UX_BusinessPartner_OwnerRfc_Id ON dbo.BusinessPartner(OwnerRfc,Id);
  DROP INDEX UX_BusinessPartner_LegacyProveedorId ON dbo.BusinessPartner;
  CREATE UNIQUE INDEX UX_BusinessPartner_OwnerRfc_LegacyProveedorId
    ON dbo.BusinessPartner(OwnerRfc,LegacyProveedorId) WHERE LegacyProveedorId IS NOT NULL;

  ALTER TABLE dbo.BusinessPartnerRole DROP CONSTRAINT FK_BusinessPartnerRole_BusinessPartner;
  ALTER TABLE dbo.BusinessPartnerRole ADD CONSTRAINT FK_BusinessPartnerRole_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);
  CREATE UNIQUE INDEX UX_BusinessPartnerRole_Rfc_Partner_Role ON dbo.BusinessPartnerRole(Rfc,BusinessPartnerId,RoleCode);

  ALTER TABLE dbo.BusinessPartnerCfdiProfile DROP CONSTRAINT FK_BusinessPartnerCfdiProfile_BusinessPartner;
  ALTER TABLE dbo.BusinessPartnerCfdiProfile ADD CONSTRAINT FK_BusinessPartnerCfdiProfile_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);
  CREATE UNIQUE INDEX UX_BusinessPartnerCfdiProfile_Rfc_Partner ON dbo.BusinessPartnerCfdiProfile(Rfc,BusinessPartnerId);

  ALTER TABLE logistica.MaterialVendor DROP CONSTRAINT FK_MaterialVendor_PartnerScope;
  ALTER TABLE logistica.MaterialVendor ADD CONSTRAINT FK_MaterialVendor_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);
  ALTER TABLE logistica.PurchaseOrder DROP CONSTRAINT FK_PurchaseOrder_BusinessPartner_Rfc;
  ALTER TABLE logistica.PurchaseOrder ADD CONSTRAINT FK_PurchaseOrder_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);
  ALTER TABLE logistica.VendorProfile DROP CONSTRAINT FK_VendorProfile_BusinessPartner_Rfc;
  ALTER TABLE logistica.VendorProfile ADD CONSTRAINT FK_VendorProfile_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);
  ALTER TABLE AP.RecurringPayable ADD CONSTRAINT FK_AP_RecurringPayable_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);

  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'orion.Company') AND name=N'UX_orion_Company_CompanyId_Rfc')
    CREATE UNIQUE INDEX UX_orion_Company_CompanyId_Rfc ON orion.Company(CompanyId,Rfc);
  ALTER TABLE orion.HospitalityFiscalCustomer DROP CONSTRAINT FK_HospitalityFiscalCustomer_BusinessPartner;
  ALTER TABLE orion.HospitalityFiscalCustomer ADD CONSTRAINT FK_HospitalityFiscalCustomer_CompanyRfc
    FOREIGN KEY(CompanyId,Rfc) REFERENCES orion.Company(CompanyId,Rfc);
  ALTER TABLE orion.HospitalityFiscalCustomer ADD CONSTRAINT FK_HospitalityFiscalCustomer_OwnerPartner
    FOREIGN KEY(Rfc,BusinessPartnerId) REFERENCES dbo.BusinessPartner(OwnerRfc,Id);

  DECLARE @DropScopeForeignKeys nvarchar(max);
  SELECT @DropScopeForeignKeys=STRING_AGG(CONVERT(nvarchar(max),N'ALTER TABLE '+QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id))+N' DROP CONSTRAINT '+QUOTENAME(name)+N';'),CHAR(10))
  FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID(N'dbo.BusinessPartnerRfcScope');
  IF @DropScopeForeignKeys IS NOT NULL EXEC sys.sp_executesql @DropScopeForeignKeys;
  ALTER SECURITY POLICY logistica.RfcSecurityPolicy
    DROP FILTER PREDICATE ON dbo.BusinessPartnerRfcScope,
    DROP BLOCK PREDICATE ON dbo.BusinessPartnerRfcScope AFTER INSERT,
    DROP BLOCK PREDICATE ON dbo.BusinessPartnerRfcScope AFTER UPDATE;
  DROP TABLE dbo.BusinessPartnerRfcScope;

  /* ---------- Per-RFC unit catalog ---------- */
  DECLARE @DropUnitForeignKeys nvarchar(max);
  SELECT @DropUnitForeignKeys=STRING_AGG(CONVERT(nvarchar(max),N'ALTER TABLE '+QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))+N'.'+QUOTENAME(OBJECT_NAME(parent_object_id))+N' DROP CONSTRAINT '+QUOTENAME(name)+N';'),CHAR(10))
  FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID(N'logistica.UnitOfMeasure');
  IF @DropUnitForeignKeys IS NOT NULL EXEC sys.sp_executesql @DropUnitForeignKeys;
  DROP INDEX UX_UnitOfMeasure_LegacyUnitId ON logistica.UnitOfMeasure;
  DROP INDEX UX_UnitOfMeasure_UnitName ON logistica.UnitOfMeasure;
  UPDATE logistica.UnitOfMeasure SET Rfc=@OhmRfc;
  CREATE TABLE #UnitMap(Rfc varchar(50) NOT NULL,SourceId int NOT NULL,TargetId int NOT NULL,PRIMARY KEY(Rfc,SourceId),UNIQUE(Rfc,TargetId));
  INSERT #UnitMap(Rfc,SourceId,TargetId) SELECT @OhmRfc,Id,Id FROM logistica.UnitOfMeasure;
  MERGE logistica.UnitOfMeasure AS target
  USING
  (
    SELECT company.Rfc,unitInfo.Id SourceId,unitInfo.LegacyUnitId,unitInfo.UnitName,unitInfo.Abbreviation,unitInfo.[Description],unitInfo.IsActive
    FROM orion.Company company CROSS JOIN logistica.UnitOfMeasure unitInfo
    WHERE company.IsActive=1 AND company.Rfc<>@OhmRfc AND unitInfo.Rfc=@OhmRfc
  ) source ON 1=0
  WHEN NOT MATCHED THEN INSERT(LegacyUnitId,UnitName,Abbreviation,[Description],IsActive,Rfc)
    VALUES(source.LegacyUnitId,source.UnitName,source.Abbreviation,source.[Description],source.IsActive,source.Rfc)
  OUTPUT source.Rfc,source.SourceId,inserted.Id INTO #UnitMap(Rfc,SourceId,TargetId);
  ALTER TABLE logistica.UnitOfMeasure ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE logistica.UnitOfMeasure ADD CONSTRAINT FK_UnitOfMeasure_Company FOREIGN KEY(Rfc) REFERENCES orion.Company(Rfc);
  CREATE UNIQUE INDEX UX_UnitOfMeasure_Rfc_Id ON logistica.UnitOfMeasure(Rfc,Id);
  CREATE UNIQUE INDEX UX_UnitOfMeasure_Rfc_UnitName ON logistica.UnitOfMeasure(Rfc,UnitName);
  CREATE UNIQUE INDEX UX_UnitOfMeasure_Rfc_LegacyUnitId ON logistica.UnitOfMeasure(Rfc,LegacyUnitId) WHERE LegacyUnitId IS NOT NULL;

  UPDATE material SET BaseUnitId=map.TargetId FROM logistica.Material material JOIN #UnitMap map ON map.Rfc=material.Rfc AND map.SourceId=material.BaseUnitId;
  UPDATE material SET PurchaseUnitId=map.TargetId FROM logistica.Material material JOIN #UnitMap map ON map.Rfc=material.Rfc AND map.SourceId=material.PurchaseUnitId;
  UPDATE vendorLink SET PurchaseUnitId=map.TargetId FROM logistica.MaterialVendor vendorLink JOIN #UnitMap map ON map.Rfc=vendorLink.Rfc AND map.SourceId=vendorLink.PurchaseUnitId;
  UPDATE component SET UnitId=map.TargetId FROM logistica.BomComponent component JOIN #UnitMap map ON map.Rfc=component.Rfc AND map.SourceId=component.UnitId;
  UPDATE versionInfo SET YieldUnitId=map.TargetId FROM logistica.BomVersion versionInfo JOIN #UnitMap map ON map.Rfc=versionInfo.Rfc AND map.SourceId=versionInfo.YieldUnitId;
  UPDATE conversionInfo SET FromUnitId=map.TargetId FROM logistica.MaterialUnitConversion conversionInfo JOIN #UnitMap map ON map.Rfc=conversionInfo.Rfc AND map.SourceId=conversionInfo.FromUnitId;
  UPDATE conversionInfo SET ToUnitId=map.TargetId FROM logistica.MaterialUnitConversion conversionInfo JOIN #UnitMap map ON map.Rfc=conversionInfo.Rfc AND map.SourceId=conversionInfo.ToUnitId;
  UPDATE production SET UnitId=map.TargetId FROM logistica.ProductionOrder production JOIN #UnitMap map ON map.Rfc=production.Rfc AND map.SourceId=production.UnitId;
  UPDATE delta SET UnitId=map.TargetId FROM restaurante.ModifierIngredientDelta delta JOIN #UnitMap map ON map.Rfc=delta.Rfc AND map.SourceId=delta.UnitId;
  UPDATE effect SET UnitId=map.TargetId FROM restaurante.OrderLineModifierIngredientEffect effect JOIN #UnitMap map ON map.Rfc=effect.Rfc AND map.SourceId=effect.UnitId;

  UPDATE logistica.UnitConversion SET Rfc=@OhmRfc;
  INSERT logistica.UnitConversion(FromUnitId,ToUnitId,Dimension,Factor,IsActive,Rfc)
  SELECT fromMap.TargetId,toMap.TargetId,source.Dimension,source.Factor,source.IsActive,company.Rfc
  FROM logistica.UnitConversion source
  CROSS JOIN orion.Company company
  JOIN #UnitMap fromMap ON fromMap.Rfc=company.Rfc AND fromMap.SourceId=source.FromUnitId
  JOIN #UnitMap toMap ON toMap.Rfc=company.Rfc AND toMap.SourceId=source.ToUnitId
  WHERE source.Rfc=@OhmRfc AND company.IsActive=1 AND company.Rfc<>@OhmRfc;
  ALTER TABLE logistica.UnitConversion ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE logistica.UnitConversion ADD CONSTRAINT FK_UnitConversion_Company FOREIGN KEY(Rfc) REFERENCES orion.Company(Rfc);

  ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_Rfc_BaseUnit FOREIGN KEY(Rfc,BaseUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.Material ADD CONSTRAINT FK_Material_Rfc_PurchaseUnit FOREIGN KEY(Rfc,PurchaseUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.MaterialVendor ADD CONSTRAINT FK_MaterialVendor_Rfc_PurchaseUnit FOREIGN KEY(Rfc,PurchaseUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.BomComponent ADD CONSTRAINT FK_BomComponent_Rfc_Unit FOREIGN KEY(Rfc,UnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.BomVersion ADD CONSTRAINT FK_BomVersion_Rfc_YieldUnit FOREIGN KEY(Rfc,YieldUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.MaterialUnitConversion ADD CONSTRAINT FK_MaterialUnitConversion_Rfc_FromUnit FOREIGN KEY(Rfc,FromUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.MaterialUnitConversion ADD CONSTRAINT FK_MaterialUnitConversion_Rfc_ToUnit FOREIGN KEY(Rfc,ToUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.ProductionOrder ADD CONSTRAINT FK_ProductionOrder_Rfc_Unit FOREIGN KEY(Rfc,UnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE restaurante.ModifierIngredientDelta ADD CONSTRAINT FK_ModifierIngredientDelta_Rfc_Unit FOREIGN KEY(Rfc,UnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE restaurante.OrderLineModifierIngredientEffect ADD CONSTRAINT FK_OrderLineModifierEffect_Rfc_Unit FOREIGN KEY(Rfc,UnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.UnitConversion ADD CONSTRAINT FK_UnitConversion_Rfc_FromUnit FOREIGN KEY(Rfc,FromUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);
  ALTER TABLE logistica.UnitConversion ADD CONSTRAINT FK_UnitConversion_Rfc_ToUnit FOREIGN KEY(Rfc,ToUnitId) REFERENCES logistica.UnitOfMeasure(Rfc,Id);

  /* ---------- Per-RFC allergen catalog ---------- */
  ALTER TABLE logistica.MaterialAllergen DROP CONSTRAINT FK_MaterialAllergen_Allergen;
  ALTER TABLE logistica.Allergen DROP CONSTRAINT UX_Allergen_Code;
  UPDATE logistica.Allergen SET Rfc=@OhmRfc;
  CREATE TABLE #AllergenMap(Rfc varchar(50) NOT NULL,SourceId int NOT NULL,TargetId int NOT NULL,PRIMARY KEY(Rfc,SourceId),UNIQUE(Rfc,TargetId));
  INSERT #AllergenMap SELECT @OhmRfc,Id,Id FROM logistica.Allergen;
  MERGE logistica.Allergen AS target
  USING
  (
    SELECT company.Rfc,allergen.Id SourceId,allergen.Code,allergen.Name,allergen.IsActive
    FROM orion.Company company CROSS JOIN logistica.Allergen allergen
    WHERE company.IsActive=1 AND company.Rfc<>@OhmRfc AND allergen.Rfc=@OhmRfc
  ) source ON 1=0
  WHEN NOT MATCHED THEN INSERT(Code,Name,IsActive,Rfc) VALUES(source.Code,source.Name,source.IsActive,source.Rfc)
  OUTPUT source.Rfc,source.SourceId,inserted.Id INTO #AllergenMap(Rfc,SourceId,TargetId);
  ALTER TABLE logistica.Allergen ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE logistica.Allergen ADD CONSTRAINT FK_Allergen_Company FOREIGN KEY(Rfc) REFERENCES orion.Company(Rfc);
  CREATE UNIQUE INDEX UX_Allergen_Rfc_Id ON logistica.Allergen(Rfc,Id);
  CREATE UNIQUE INDEX UX_Allergen_Rfc_Code ON logistica.Allergen(Rfc,Code);
  UPDATE link SET AllergenId=map.TargetId FROM logistica.MaterialAllergen link JOIN #AllergenMap map ON map.Rfc=link.Rfc AND map.SourceId=link.AllergenId;
  ALTER TABLE logistica.MaterialAllergen ADD CONSTRAINT FK_MaterialAllergen_Rfc_Allergen FOREIGN KEY(Rfc,AllergenId) REFERENCES logistica.Allergen(Rfc,Id);

  /* ---------- Per-RFC payment forms ---------- */
  ALTER TABLE dbo.Formas_Pago DROP CONSTRAINT PK_Formas_Pago;
  UPDATE dbo.Formas_Pago SET Rfc=@OhmRfc;
  INSERT dbo.Formas_Pago(Clave,Descripcion,Rfc)
  SELECT seed.Clave,seed.Descripcion,@OhmRfc
  FROM (VALUES
    (CONVERT(varchar(10),''),CONVERT(varchar(100),'Sin especificar (heredado)')),
    ('0','Sin especificar (heredado)'),
    ('30','Aplicación de anticipos'),
    ('31','Intermediario pagos')) seed(Clave,Descripcion)
  WHERE NOT EXISTS(SELECT 1 FROM dbo.Formas_Pago existing WHERE existing.Rfc=@OhmRfc AND existing.Clave=seed.Clave);
  INSERT dbo.Formas_Pago(Clave,Descripcion,Rfc)
  SELECT source.Clave,source.Descripcion,company.Rfc
  FROM dbo.Formas_Pago source CROSS JOIN orion.Company company
  WHERE source.Rfc=@OhmRfc AND company.IsActive=1 AND company.Rfc<>@OhmRfc;
  ALTER TABLE dbo.Formas_Pago ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE dbo.Formas_Pago ADD CONSTRAINT PK_Formas_Pago PRIMARY KEY(Rfc,Clave);
  ALTER TABLE dbo.Formas_Pago ADD CONSTRAINT FK_FormasPago_Company FOREIGN KEY(Rfc) REFERENCES orion.Company(Rfc);
  DECLARE @MissingPaymentRfc varchar(50),@MissingPaymentCode varchar(10),@MissingPaymentMessage nvarchar(2048);
  SELECT TOP(1) @MissingPaymentRfc=payment.RFC,@MissingPaymentCode=payment.Forma_Pago
  FROM dbo.Transacciones payment
  LEFT JOIN dbo.Formas_Pago paymentForm ON paymentForm.Rfc=payment.RFC AND paymentForm.Clave=payment.Forma_Pago
  WHERE paymentForm.Clave IS NULL;
  IF @MissingPaymentRfc IS NOT NULL
  BEGIN
    SET @MissingPaymentMessage=CONCAT(N'Forma de pago sin catálogo RFC: ',@MissingPaymentRfc,N' / ',COALESCE(@MissingPaymentCode,N'<NULL>'));
    THROW 53422,@MissingPaymentMessage,1;
  END;
  ALTER TABLE dbo.Transacciones WITH NOCHECK ADD CONSTRAINT FK_Transacciones_Rfc_FormaPago FOREIGN KEY(RFC,Forma_Pago) REFERENCES dbo.Formas_Pago(Rfc,Clave);
  ALTER TABLE dbo.Transacciones WITH CHECK CHECK CONSTRAINT FK_Transacciones_Rfc_FormaPago;

  /* ---------- Per-RFC work-order categories ---------- */
  ALTER TABLE dbo.OrdenTrabajo DROP CONSTRAINT FK_OrdenTrabajo_Categoria;
  ALTER TABLE dbo.OrdenTrabajoPlantilla DROP CONSTRAINT FK_OrdenTrabajoPlantilla_Categoria;
  DROP INDEX UX_OrdenTrabajoCategoria_Codigo ON dbo.OrdenTrabajoCategoria;
  UPDATE dbo.OrdenTrabajoCategoria SET Rfc=@OhmRfc;
  CREATE TABLE #WorkCategoryMap(Rfc varchar(50) NOT NULL,SourceId int NOT NULL,TargetId int NOT NULL,PRIMARY KEY(Rfc,SourceId),UNIQUE(Rfc,TargetId));
  INSERT #WorkCategoryMap SELECT @OhmRfc,Id,Id FROM dbo.OrdenTrabajoCategoria;
  MERGE dbo.OrdenTrabajoCategoria AS target
  USING
  (
    SELECT company.Rfc,category.Id SourceId,category.Codigo,category.Nombre,category.Activa,category.Orden
    FROM orion.Company company CROSS JOIN dbo.OrdenTrabajoCategoria category
    WHERE company.IsActive=1 AND company.Rfc<>@OhmRfc AND category.Rfc=@OhmRfc
  ) source ON 1=0
  WHEN NOT MATCHED THEN INSERT(Codigo,Nombre,Activa,Orden,CreadaEn,Rfc)
    VALUES(source.Codigo,source.Nombre,source.Activa,source.Orden,SYSUTCDATETIME(),source.Rfc)
  OUTPUT source.Rfc,source.SourceId,inserted.Id INTO #WorkCategoryMap(Rfc,SourceId,TargetId);
  ALTER TABLE dbo.OrdenTrabajoCategoria ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE dbo.OrdenTrabajoCategoria ADD CONSTRAINT FK_OrdenTrabajoCategoria_Company FOREIGN KEY(Rfc) REFERENCES orion.Company(Rfc);
  CREATE UNIQUE INDEX UX_OrdenTrabajoCategoria_Rfc_Id ON dbo.OrdenTrabajoCategoria(Rfc,Id);
  CREATE UNIQUE INDEX UX_OrdenTrabajoCategoria_Rfc_Codigo ON dbo.OrdenTrabajoCategoria(Rfc,Codigo);
  UPDATE workOrder SET CategoriaId=map.TargetId FROM dbo.OrdenTrabajo workOrder JOIN #WorkCategoryMap map ON map.Rfc=workOrder.Rfc AND map.SourceId=workOrder.CategoriaId;
  UPDATE template SET CategoriaId=map.TargetId FROM dbo.OrdenTrabajoPlantilla template JOIN #WorkCategoryMap map ON map.Rfc=template.Rfc AND map.SourceId=template.CategoriaId;
  ALTER TABLE dbo.OrdenTrabajo ADD CONSTRAINT FK_OrdenTrabajo_Rfc_Categoria FOREIGN KEY(Rfc,CategoriaId) REFERENCES dbo.OrdenTrabajoCategoria(Rfc,Id);
  ALTER TABLE dbo.OrdenTrabajoPlantilla ADD CONSTRAINT FK_OrdenTrabajoPlantilla_Rfc_Categoria FOREIGN KEY(Rfc,CategoriaId) REFERENCES dbo.OrdenTrabajoCategoria(Rfc,Id);

  /* ---------- Room and employee relationship repair ---------- */
  DELETE roomScope
  OUTPUT @MigrationId,'PurchaseOrderRoomScope','REMOVE_CROSS_COMPANY_ROOM',CONVERT(nvarchar(100),deleted.PurchaseOrderId),CONVERT(nvarchar(100),deleted.RoomId),
    @BrunoRfc,@OhmRfc,N'LONDON removido de una orden Bruno en borrador.'
  INTO orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  FROM logistica.PurchaseOrderRoomScope roomScope
  WHERE roomScope.Rfc=@BrunoRfc AND roomScope.PurchaseOrderId IN(18,26) AND roomScope.RoomId=1016;

  UPDATE roomScope SET CompanyId=company.CompanyId,SiteId=room.OrionSiteId
  FROM logistica.PurchaseOrderRoomScope roomScope
  JOIN orion.Company company ON company.Rfc=roomScope.Rfc
  JOIN dbo.ROOM room ON room.ID=roomScope.RoomId AND room.OrionCompanyId=company.CompanyId;
  IF EXISTS(SELECT 1 FROM logistica.PurchaseOrderRoomScope WHERE CompanyId IS NULL OR SiteId IS NULL)
    THROW 53411,'Quedaron habitaciones de órdenes de compra fuera de su empresa.',1;
  ALTER TABLE logistica.PurchaseOrderRoomScope ALTER COLUMN CompanyId bigint NOT NULL;
  ALTER TABLE logistica.PurchaseOrderRoomScope ALTER COLUMN SiteId bigint NOT NULL;
  ALTER TABLE logistica.PurchaseOrderRoomScope DROP CONSTRAINT FK_PurchaseOrderRoomScope_Room;
  ALTER TABLE logistica.PurchaseOrderRoomScope ADD CONSTRAINT FK_PurchaseOrderRoomScope_CompanyRfc FOREIGN KEY(CompanyId,Rfc) REFERENCES orion.Company(CompanyId,Rfc);
  ALTER TABLE logistica.PurchaseOrderRoomScope ADD CONSTRAINT FK_PurchaseOrderRoomScope_Rfc_Order FOREIGN KEY(Rfc,PurchaseOrderId) REFERENCES logistica.PurchaseOrder(Rfc,Id);
  ALTER TABLE logistica.PurchaseOrderRoomScope ADD CONSTRAINT FK_PurchaseOrderRoomScope_CompanyRoom FOREIGN KEY(CompanyId,SiteId,RoomId) REFERENCES dbo.ROOM(OrionCompanyId,OrionSiteId,ID);

  INSERT orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  SELECT @MigrationId,'Location','REMOVE_CROSS_COMPANY_ROOM',CONVERT(nvarchar(100),locationInfo.Id),CONVERT(nvarchar(100),locationInfo.RoomId),
    locationInfo.Rfc,company.Rfc,N'La ubicación conserva su identidad y pierde el vínculo a una habitación de otra empresa.'
  FROM logistica.Location locationInfo
  JOIN dbo.ROOM room ON room.ID=locationInfo.RoomId
  JOIN orion.Company company ON company.CompanyId=room.OrionCompanyId
  WHERE locationInfo.Rfc<>company.Rfc;
  UPDATE locationInfo SET RoomId=NULL
  FROM logistica.Location locationInfo JOIN dbo.ROOM room ON room.ID=locationInfo.RoomId
  JOIN orion.Company company ON company.CompanyId=room.OrionCompanyId
  WHERE locationInfo.Rfc<>company.Rfc;
  UPDATE locationInfo SET LegacyRoomId=NULL
  FROM logistica.Location locationInfo JOIN dbo.ROOM room ON room.ID=locationInfo.LegacyRoomId
  JOIN orion.Company company ON company.CompanyId=room.OrionCompanyId
  WHERE locationInfo.Rfc<>company.Rfc;

  UPDATE locationInfo SET CompanyId=company.CompanyId,RoomSiteId=room.OrionSiteId
  FROM logistica.Location locationInfo JOIN orion.Company company ON company.Rfc=locationInfo.Rfc
  LEFT JOIN dbo.ROOM room ON room.ID=locationInfo.RoomId AND room.OrionCompanyId=company.CompanyId;
  IF EXISTS(SELECT 1 FROM logistica.Location WHERE CompanyId IS NULL OR (RoomId IS NOT NULL AND RoomSiteId IS NULL))
    THROW 53412,'Quedaron ubicaciones logísticas con una habitación de otra empresa.',1;
  ALTER TABLE logistica.Location ALTER COLUMN CompanyId bigint NOT NULL;
  ALTER TABLE logistica.Location DROP CONSTRAINT FK_Location_Room;
  ALTER TABLE logistica.Location ADD CONSTRAINT FK_Location_CompanyRfc FOREIGN KEY(CompanyId,Rfc) REFERENCES orion.Company(CompanyId,Rfc);
  ALTER TABLE logistica.Location ADD CONSTRAINT FK_Location_CompanyRoom FOREIGN KEY(CompanyId,RoomSiteId,RoomId) REFERENCES dbo.ROOM(OrionCompanyId,OrionSiteId,ID);

  UPDATE dbo.OrdenTrabajo SET OwnerEmployeeId=96,ActualizadaEn=SYSUTCDATETIME(),ActualizadaPor=N'20260912 tenant isolation'
  OUTPUT @MigrationId,'OrdenTrabajo','REMAP_EMPLOYEE',CONVERT(nvarchar(100),deleted.OwnerEmployeeId),CONVERT(nvarchar(100),inserted.OwnerEmployeeId),
    @OhmRfc,@BrunoRfc,N'Orden 431 reasignada al registro Bruno del mismo empleado.'
  INTO orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  WHERE Id=431 AND Rfc=@BrunoRfc AND OwnerEmployeeId=83;
  IF EXISTS(SELECT 1 FROM dbo.OrdenTrabajo workOrder LEFT JOIN dbo.Capital_Humano employee ON employee.ID=workOrder.OwnerEmployeeId AND employee.RFC=workOrder.Rfc WHERE employee.ID IS NULL)
    THROW 53413,'Quedaron órdenes de trabajo con propietario de otra empresa.',1;
  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.Capital_Humano') AND name=N'UX_Capital_Humano_RFC_ID')
    CREATE UNIQUE INDEX UX_Capital_Humano_RFC_ID ON dbo.Capital_Humano(RFC,ID);
  IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.OrdenTrabajo') AND name=N'UX_OrdenTrabajo_Rfc_Id')
    CREATE UNIQUE INDEX UX_OrdenTrabajo_Rfc_Id ON dbo.OrdenTrabajo(Rfc,Id);
  ALTER TABLE dbo.OrdenTrabajo DROP CONSTRAINT FK_OrdenTrabajo_Owner;
  ALTER TABLE dbo.OrdenTrabajo ADD CONSTRAINT FK_OrdenTrabajo_Rfc_Owner FOREIGN KEY(Rfc,OwnerEmployeeId) REFERENCES dbo.Capital_Humano(RFC,ID);
  CREATE TABLE #EmployeeMap(SourceId int NOT NULL PRIMARY KEY,TargetId int NOT NULL UNIQUE);
  INSERT #EmployeeMap(SourceId,TargetId) VALUES(1,93),(83,96),(85,88),(86,87),(89,94),(90,92);
  INSERT orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,TargetId,SourceOwnerRfc,TargetOwnerRfc,Details)
  SELECT @MigrationId,'OrdenTrabajoParticipante','REMAP_EMPLOYEE',CONVERT(nvarchar(100),participant.EmployeeId),CONVERT(nvarchar(100),map.TargetId),
    @OhmRfc,@BrunoRfc,CONCAT(N'Participante de orden ',participant.OrdenTrabajoId,N' remapeado al registro Bruno de la misma persona.')
  FROM dbo.OrdenTrabajoParticipante participant
  JOIN dbo.OrdenTrabajo workOrder ON workOrder.Id=participant.OrdenTrabajoId AND workOrder.Rfc=@BrunoRfc
  JOIN #EmployeeMap map ON map.SourceId=participant.EmployeeId;
  DELETE participant
  FROM dbo.OrdenTrabajoParticipante participant
  JOIN dbo.OrdenTrabajo workOrder ON workOrder.Id=participant.OrdenTrabajoId AND workOrder.Rfc=@BrunoRfc
  JOIN #EmployeeMap map ON map.SourceId=participant.EmployeeId
  WHERE EXISTS(SELECT 1 FROM dbo.OrdenTrabajoParticipante existing WHERE existing.OrdenTrabajoId=participant.OrdenTrabajoId AND existing.EmployeeId=map.TargetId);
  UPDATE participant SET EmployeeId=map.TargetId
  FROM dbo.OrdenTrabajoParticipante participant
  JOIN dbo.OrdenTrabajo workOrder ON workOrder.Id=participant.OrdenTrabajoId AND workOrder.Rfc=@BrunoRfc
  JOIN #EmployeeMap map ON map.SourceId=participant.EmployeeId;
  UPDATE participant SET Rfc=workOrder.Rfc FROM dbo.OrdenTrabajoParticipante participant JOIN dbo.OrdenTrabajo workOrder ON workOrder.Id=participant.OrdenTrabajoId;
  ALTER TABLE dbo.OrdenTrabajoParticipante ALTER COLUMN Rfc varchar(50) NOT NULL;
  ALTER TABLE dbo.OrdenTrabajoParticipante DROP CONSTRAINT FK_OrdenTrabajoParticipante_Empleado;
  ALTER TABLE dbo.OrdenTrabajoParticipante ADD CONSTRAINT FK_OrdenTrabajoParticipante_Rfc_Orden FOREIGN KEY(Rfc,OrdenTrabajoId) REFERENCES dbo.OrdenTrabajo(Rfc,Id);
  ALTER TABLE dbo.OrdenTrabajoParticipante ADD CONSTRAINT FK_OrdenTrabajoParticipante_Rfc_Empleado FOREIGN KEY(Rfc,EmployeeId) REFERENCES dbo.Capital_Humano(RFC,ID);

  INSERT orion.TenantIsolationMigrationAudit(MigrationId,EntityType,ActionCode,SourceId,SourceOwnerRfc,TargetOwnerRfc,Details)
  SELECT @MigrationId,'Roles_Usuario','REMOVE_CROSS_RFC_ROLE',CONVERT(nvarchar(100),legacyRole.id),legacyRole.RFC,employee.RFC,
    CASE WHEN EXISTS(SELECT 1 FROM auth.AspNetUserCompanies membership WHERE membership.EmployeeId=employee.ID AND membership.Rfc=employee.RFC)
      THEN N'El empleado conserva membresía moderna en su RFC real.'
      ELSE N'Empleado inactivo sin identidad moderna; se elimina la concesión cruzada obsoleta.' END
  FROM dbo.Roles_Usuario legacyRole JOIN dbo.Capital_Humano employee ON employee.ID=legacyRole.Usuario_ID
  WHERE legacyRole.RFC<>employee.RFC;
  DELETE legacyRole FROM dbo.Roles_Usuario legacyRole JOIN dbo.Capital_Humano employee ON employee.ID=legacyRole.Usuario_ID WHERE legacyRole.RFC<>employee.RFC;
  ALTER TABLE dbo.Roles_Usuario DROP CONSTRAINT FK_Roles_Usuario_Capital_Humano;
  ALTER TABLE dbo.Roles_Usuario ADD CONSTRAINT FK_Roles_Usuario_Rfc_Employee FOREIGN KEY(RFC,Usuario_ID) REFERENCES dbo.Capital_Humano(RFC,ID);

  /* ---------- Explicit ownership for legacy provider/customer masters ---------- */
  IF EXISTS
  (
    SELECT room.OWNER_ID FROM dbo.ROOM room WHERE room.OrionCompanyId IS NOT NULL
    GROUP BY room.OWNER_ID HAVING COUNT(DISTINCT room.OrionCompanyId)>1
  ) THROW 53414,'Un proveedor heredado posee habitaciones en más de una empresa; debe clonarse antes de aplicar.',1;
  UPDATE provider SET OwnerCompanyId=COALESCE(roomOwner.CompanyId,partnerOwner.CompanyId,ohm.CompanyId)
  FROM dbo.Proveedores provider
  CROSS JOIN (SELECT CompanyId FROM orion.Company WHERE Rfc=@OhmRfc) ohm
  OUTER APPLY(SELECT MIN(room.OrionCompanyId) CompanyId FROM dbo.ROOM room WHERE room.OWNER_ID=provider.id) roomOwner
  OUTER APPLY(SELECT MIN(company.CompanyId) CompanyId FROM dbo.BusinessPartner partner JOIN orion.Company company ON company.Rfc=partner.OwnerRfc WHERE partner.LegacyProveedorId=provider.id) partnerOwner;
  UPDATE provider SET OwnerRfc=company.Rfc FROM dbo.Proveedores provider JOIN orion.Company company ON company.CompanyId=provider.OwnerCompanyId;
  ALTER TABLE dbo.Proveedores ALTER COLUMN OwnerCompanyId bigint NOT NULL;
  ALTER TABLE dbo.Proveedores ALTER COLUMN OwnerRfc varchar(50) NOT NULL;
  ALTER TABLE dbo.Proveedores ADD CONSTRAINT FK_Proveedores_OwnerCompany FOREIGN KEY(OwnerCompanyId,OwnerRfc) REFERENCES orion.Company(CompanyId,Rfc);
  CREATE UNIQUE INDEX UX_Proveedores_OwnerCompany_Id ON dbo.Proveedores(OwnerCompanyId,id);
  CREATE UNIQUE INDEX UX_Proveedores_OwnerRfc_Id ON dbo.Proveedores(OwnerRfc,id);
  ALTER TABLE dbo.ROOM DROP CONSTRAINT FK_ROOM_HospitalitySiteOwner;
  ALTER TABLE dbo.ROOM ADD CONSTRAINT FK_ROOM_OwnerCompanyProvider FOREIGN KEY(OrionCompanyId,OWNER_ID) REFERENCES dbo.Proveedores(OwnerCompanyId,id);
  ALTER TABLE orion.HospitalitySiteOwner DROP CONSTRAINT FK_HospitalitySiteOwner_Provider;
  ALTER TABLE orion.HospitalitySiteOwner ADD CONSTRAINT FK_HospitalitySiteOwner_CompanyProvider FOREIGN KEY(CompanyId,ProveedorId) REFERENCES dbo.Proveedores(OwnerCompanyId,id);

  IF EXISTS
  (
    SELECT reservation.CLIENTE_ID FROM dbo.RESERVATION reservation WHERE reservation.OrionCompanyId IS NOT NULL
    GROUP BY reservation.CLIENTE_ID HAVING COUNT(DISTINCT reservation.OrionCompanyId)>1
  ) THROW 53415,'Un cliente heredado tiene reservas en más de una empresa; debe clonarse antes de aplicar.',1;
  UPDATE customer SET OwnerCompanyId=COALESCE(reservationOwner.CompanyId,ohm.CompanyId)
  FROM dbo.Clientes customer
  CROSS JOIN(SELECT CompanyId FROM orion.Company WHERE Rfc=@OhmRfc) ohm
  OUTER APPLY(SELECT MIN(reservation.OrionCompanyId) CompanyId FROM dbo.RESERVATION reservation WHERE reservation.CLIENTE_ID=customer.ID) reservationOwner;
  UPDATE customer SET OwnerRfc=company.Rfc FROM dbo.Clientes customer JOIN orion.Company company ON company.CompanyId=customer.OwnerCompanyId;
  ALTER TABLE dbo.Clientes ALTER COLUMN OwnerCompanyId bigint NOT NULL;
  ALTER TABLE dbo.Clientes ALTER COLUMN OwnerRfc varchar(50) NOT NULL;
  ALTER TABLE dbo.Clientes ADD CONSTRAINT FK_Clientes_OwnerCompany FOREIGN KEY(OwnerCompanyId,OwnerRfc) REFERENCES orion.Company(CompanyId,Rfc);
  CREATE UNIQUE INDEX UX_Clientes_OwnerCompany_Id ON dbo.Clientes(OwnerCompanyId,ID);
  CREATE UNIQUE INDEX UX_Clientes_OwnerRfc_Id ON dbo.Clientes(OwnerRfc,ID);
  ALTER TABLE orion.HospitalitySiteCustomer DROP CONSTRAINT FK_orion_HospitalitySiteCustomer_Cliente;
  ALTER TABLE orion.HospitalitySiteCustomer ADD CONSTRAINT FK_HospitalitySiteCustomer_CompanyCustomer FOREIGN KEY(CompanyId,ClienteId) REFERENCES dbo.Clientes(OwnerCompanyId,ID);

  /* ---------- Fail-closed RLS for every mutable RFC-bearing operational table ---------- */
  CREATE TABLE #RlsTargets(SchemaName sysname NOT NULL,TableName sysname NOT NULL,RfcColumn sysname NOT NULL,PRIMARY KEY(SchemaName,TableName));
  INSERT #RlsTargets VALUES
    (N'dbo',N'BusinessPartner',N'OwnerRfc'),
    (N'dbo',N'BusinessPartnerRole',N'Rfc'),
    (N'dbo',N'BusinessPartnerCfdiProfile',N'Rfc'),
    (N'dbo',N'Formas_Pago',N'Rfc'),
    (N'dbo',N'OrdenTrabajoCategoria',N'Rfc'),
    (N'dbo',N'Proveedores',N'OwnerRfc'),
    (N'dbo',N'Clientes',N'OwnerRfc'),
    (N'logistica',N'UnitOfMeasure',N'Rfc'),
    (N'logistica',N'UnitConversion',N'Rfc'),
    (N'logistica',N'Allergen',N'Rfc');

  INSERT #RlsTargets(SchemaName,TableName,RfcColumn)
  SELECT schemaInfo.name,tableInfo.name,columnInfo.name
  FROM sys.tables tableInfo
  JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
  JOIN sys.columns columnInfo ON columnInfo.object_id=tableInfo.object_id AND columnInfo.name IN(N'Rfc',N'RFC')
  WHERE schemaInfo.name NOT IN(N'auth',N'orion',N'cfdi',N'codex_recovery')
    AND tableInfo.name NOT LIKE N'%Audit%' AND tableInfo.name NOT LIKE N'%Backfill%' AND tableInfo.name NOT LIKE N'%Log%'
    AND NOT(schemaInfo.name=N'dbo' AND tableInfo.name IN(N'BusinessPartner',N'Proveedores'))
    AND NOT EXISTS(SELECT 1 FROM #RlsTargets target WHERE target.SchemaName=schemaInfo.name AND target.TableName=tableInfo.name)
    AND NOT EXISTS(SELECT 1 FROM sys.security_predicates predicateInfo WHERE predicateInfo.target_object_id=tableInfo.object_id AND predicateInfo.predicate_type=1);

  DECLARE @RlsSchema sysname,@RlsTable sysname,@RlsColumn sysname,@RlsSql nvarchar(max);
  DECLARE rls_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT SchemaName,TableName,RfcColumn FROM #RlsTargets;
  OPEN rls_cursor;
  FETCH NEXT FROM rls_cursor INTO @RlsSchema,@RlsTable,@RlsColumn;
  WHILE @@FETCH_STATUS=0
  BEGIN
    IF NOT EXISTS(SELECT 1 FROM sys.security_predicates WHERE target_object_id=OBJECT_ID(QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)) AND predicate_type=1)
    BEGIN
      SET @RlsSql=N'ALTER SECURITY POLICY logistica.RfcSecurityPolicy ADD FILTER PREDICATE logistica.fn_RfcAccessPredicate('+QUOTENAME(@RlsColumn)+N') ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N', ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate('+QUOTENAME(@RlsColumn)+N') ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER INSERT, ADD BLOCK PREDICATE logistica.fn_RfcAccessPredicate('+QUOTENAME(@RlsColumn)+N') ON '+QUOTENAME(@RlsSchema)+N'.'+QUOTENAME(@RlsTable)+N' AFTER UPDATE;';
      EXEC sys.sp_executesql @RlsSql;
    END;
    FETCH NEXT FROM rls_cursor INTO @RlsSchema,@RlsTable,@RlsColumn;
  END;
  CLOSE rls_cursor;
  DEALLOCATE rls_cursor;

  /* Durable classification: future tables are unknown until deliberately added. */
  IF OBJECT_ID(N'orion.TenantTableClassification',N'U') IS NULL
  BEGIN
    CREATE TABLE orion.TenantTableClassification
    (
      SchemaName sysname NOT NULL,
      TableName sysname NOT NULL,
      Classification varchar(40) NOT NULL,
      OwnerColumn sysname NULL,
      ReviewedInMigrationId nvarchar(200) NOT NULL,
      Notes nvarchar(1000) NULL,
      CONSTRAINT PK_orion_TenantTableClassification PRIMARY KEY(SchemaName,TableName),
      CONSTRAINT CK_orion_TenantTableClassification_Class CHECK(Classification IN('TENANT_OWNED','AUTHENTICATION_BRIDGE','GLOBAL_PLATFORM_METADATA','ADMINISTRATIVE_ARCHIVE'))
    );
  END;
  INSERT orion.TenantTableClassification(SchemaName,TableName,Classification,OwnerColumn,ReviewedInMigrationId,Notes)
  SELECT schemaInfo.name,tableInfo.name,
    CASE
      WHEN schemaInfo.name=N'auth' THEN 'AUTHENTICATION_BRIDGE'
      WHEN schemaInfo.name=N'codex_recovery' OR tableInfo.name LIKE N'%Audit%' OR tableInfo.name LIKE N'%Backfill%' OR tableInfo.name LIKE N'%Log%' THEN 'ADMINISTRATIVE_ARCHIVE'
      WHEN schemaInfo.name=N'orion' AND tableInfo.name IN(N'SchemaMigration',N'TenantIsolationMigrationAudit',N'TenantTableClassification',N'Module',N'Company',N'Site',N'PublicSite',N'PublicSqlPrincipalBinding',N'PermissionProfileEntry') THEN 'GLOBAL_PLATFORM_METADATA'
      ELSE 'TENANT_OWNED' END,
    CASE
      WHEN COL_LENGTH(QUOTENAME(schemaInfo.name)+N'.'+QUOTENAME(tableInfo.name),N'OwnerRfc') IS NOT NULL THEN N'OwnerRfc'
      WHEN COL_LENGTH(QUOTENAME(schemaInfo.name)+N'.'+QUOTENAME(tableInfo.name),N'Rfc') IS NOT NULL AND NOT(schemaInfo.name=N'cfdi' AND tableInfo.name IN(N'Emisor',N'Receptor')) THEN N'Rfc'
      WHEN COL_LENGTH(QUOTENAME(schemaInfo.name)+N'.'+QUOTENAME(tableInfo.name),N'RFC') IS NOT NULL AND NOT(schemaInfo.name=N'dbo' AND tableInfo.name=N'Proveedores') THEN N'RFC'
      WHEN COL_LENGTH(QUOTENAME(schemaInfo.name)+N'.'+QUOTENAME(tableInfo.name),N'CompanyId') IS NOT NULL THEN N'CompanyId'
      WHEN COL_LENGTH(QUOTENAME(schemaInfo.name)+N'.'+QUOTENAME(tableInfo.name),N'OrionCompanyId') IS NOT NULL THEN N'OrionCompanyId'
      ELSE NULL END,
    @MigrationId,N'Clasificación inicial versionada por la remediación RFC.'
  FROM sys.tables tableInfo JOIN sys.schemas schemaInfo ON schemaInfo.schema_id=tableInfo.schema_id
  WHERE tableInfo.is_ms_shipped=0
    AND NOT EXISTS(SELECT 1 FROM orion.TenantTableClassification existing WHERE existing.SchemaName=schemaInfo.name AND existing.TableName=tableInfo.name);

  /* Migration invariants. */
  IF EXISTS(SELECT OwnerRfc,Id FROM dbo.BusinessPartner GROUP BY OwnerRfc,Id HAVING COUNT(*)>1)
     OR EXISTS(SELECT 1 FROM dbo.BusinessPartner WHERE OwnerRfc IS NULL)
    THROW 53416,'La propiedad de socios no quedó total y única.',1;
  IF EXISTS(SELECT 1 FROM logistica.MaterialVendor vendorLink JOIN dbo.BusinessPartner partner ON partner.Id=vendorLink.BusinessPartnerId WHERE partner.OwnerRfc<>vendorLink.Rfc)
    THROW 53417,'Persisten vínculos material-proveedor cruzados.',1;
  IF EXISTS(SELECT 1 FROM logistica.PurchaseOrder purchaseOrder JOIN dbo.BusinessPartner partner ON partner.Id=purchaseOrder.BusinessPartnerId WHERE partner.OwnerRfc<>purchaseOrder.Rfc)
    THROW 53418,'Persisten órdenes de compra con proveedor cruzado.',1;
  IF EXISTS(SELECT 1 FROM dbo.Roles_Usuario legacyRole JOIN dbo.Capital_Humano employee ON employee.ID=legacyRole.Usuario_ID WHERE legacyRole.RFC<>employee.RFC)
    THROW 53419,'Persisten Roles_Usuario cruzados.',1;
  IF EXISTS(SELECT 1 FROM #RlsTargets target WHERE NOT EXISTS(SELECT 1 FROM sys.security_predicates predicateInfo WHERE predicateInfo.target_object_id=OBJECT_ID(QUOTENAME(target.SchemaName)+N'.'+QUOTENAME(target.TableName)) AND predicateInfo.predicate_type=1))
    THROW 53420,'Falta un filtro RLS solicitado.',1;

  SELECT
    (SELECT COUNT(*) FROM dbo.BusinessPartner WHERE OwnerRfc=@OhmRfc) OhmPartners,
    (SELECT COUNT(*) FROM dbo.BusinessPartner WHERE OwnerRfc=@BrunoRfc) BrunoPartners,
    (SELECT COUNT(*) FROM logistica.MaterialVendor WHERE Rfc=@BrunoRfc AND BusinessPartnerId=112) BrunoBodegaMaterialLinks,
    (SELECT COUNT(*) FROM logistica.MaterialVendor WHERE Rfc=@OhmRfc AND BusinessPartnerId=8) OhmBodegaMaterialLinks,
    (SELECT COUNT(*) FROM logistica.PurchaseOrder WHERE Rfc=@OhmRfc AND BusinessPartnerId=8) OhmBodegaPurchaseOrders,
    (SELECT COUNT(*) FROM orion.TenantIsolationMigrationAudit WHERE MigrationId=@MigrationId) AuditRows,
    (SELECT COUNT(*) FROM #RlsTargets) RlsTargets;

  DECLARE @EnablePolicies nvarchar(max);
  SELECT @EnablePolicies=STRING_AGG(CONVERT(nvarchar(max),N'ALTER SECURITY POLICY '+QUOTENAME(SchemaName)+N'.'+QUOTENAME(PolicyName)+N' WITH (STATE=ON);'),CHAR(10))
  FROM #EnabledSecurityPolicies;
  EXEC sys.sp_executesql @EnablePolicies;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','rls_cursor')>=0 CLOSE rls_cursor;
  IF CURSOR_STATUS('local','rls_cursor')>=-1 DEALLOCATE rls_cursor;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
