/*
  E8a production: fail-closed RLS for the accounting core.

  Exact batch: dbo.Transacciones and dbo.Registro_Contable. Attachments,
  catalogs, and cfdi.Comprobante remain outside this batch. The predicate
  requires both the technical CompanyId and the exact legacy RFC in session.

  Production baseline reviewed on 2026-09-09:
  - transactions 53495 and 53496 are the only headers without CompanyId;
  - accounting rows 44843 through 44846 are the only lines without CompanyId;
  - all six rows belong to OHM191112Q26 through the exact active-company link.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyChangesInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 52400,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52401,'Esta migracion admite solo produccion o su base de ensayo autorizada.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52402,'La conexion no apunta a la base declarada.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52403,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52404,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260908_production_accounting_company_identity')
   OR COL_LENGTH(N'dbo.Transacciones',N'CompanyId') IS NULL
   OR COL_LENGTH(N'dbo.Registro_Contable',N'CompanyId') IS NULL
  THROW 52405,'Falta la identidad contable productiva requerida por E8a.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52406,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'contabilidad.AccountingScopePolicy') IS NULL
     OR OBJECT_ID(N'contabilidad.fn_AccountingScopePredicate',N'IF') IS NULL
     OR (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'contabilidad.AccountingScopePolicy'))<>6
    THROW 52407,'La migracion esta registrada pero falta parte de la politica E8a.',1;
  SELECT N'YA_APLICADO_PRODUCCION' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  RETURN;
END;
IF OBJECT_ID(N'contabilidad.AccountingScopePolicy') IS NOT NULL
   OR OBJECT_ID(N'contabilidad.fn_AccountingScopePredicate') IS NOT NULL
  THROW 52408,'Ya existe aislamiento contable sin registrar; requiere reconciliacion explicita.',1;
IF EXISTS(SELECT 1 FROM sys.security_predicates WHERE target_object_id IN(OBJECT_ID(N'dbo.Transacciones'),OBJECT_ID(N'dbo.Registro_Contable')))
  THROW 52409,'El lote contable ya tiene predicados no inventariados.',1;
IF (SELECT COUNT(*) FROM dbo.Transacciones WHERE CompanyId IS NULL)<>2
   OR EXISTS(SELECT 1 FROM dbo.Transacciones WHERE CompanyId IS NULL AND ID NOT IN(53495,53496))
   OR EXISTS
   (
     SELECT 1 FROM dbo.Transacciones
     WHERE ID IN(53495,53496)
       AND (CompanyId IS NOT NULL OR RFC<>'OHM191112Q26')
   )
  THROW 52410,'El conjunto productivo de dos polizas sin CompanyId cambio.',1;
IF (SELECT COUNT(*) FROM dbo.Registro_Contable WHERE CompanyId IS NULL)<>4
   OR EXISTS(SELECT 1 FROM dbo.Registro_Contable WHERE CompanyId IS NULL AND id NOT IN(44843,44844,44845,44846))
   OR EXISTS
   (
     SELECT 1 FROM dbo.Registro_Contable
     WHERE id IN(44843,44844,44845,44846)
       AND (CompanyId IS NOT NULL OR TransaccionID NOT IN(53495,53496))
   )
  THROW 52411,'El conjunto productivo de cuatro movimientos sin CompanyId cambio.',1;

PRINT 'Estado previo del nucleo contable productivo.';
SELECT
  (SELECT COUNT_BIG(*) FROM dbo.Transacciones) Polizas,
  (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE CompanyId IS NULL) PolizasSinEmpresa,
  (SELECT COUNT_BIG(*) FROM dbo.Registro_Contable) Movimientos,
  (SELECT COUNT_BIG(*) FROM dbo.Registro_Contable WHERE CompanyId IS NULL) MovimientosSinEmpresa;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Accounting:RlsScope:Production',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52412,'No se obtuvo el candado del lote productivo E8a.',1;

  IF EXISTS(SELECT 1 FROM sys.triggers WHERE object_id IN(OBJECT_ID(N'dbo.trg_Transacciones_Audit'),OBJECT_ID(N'dbo.trg_Registro_Contable_Audit')) AND is_disabled=1)
    THROW 52413,'Un trigger de auditoria contable ya estaba deshabilitado.',1;
  ALTER TABLE dbo.Transacciones DISABLE TRIGGER trg_Transacciones_Audit;
  ALTER TABLE dbo.Registro_Contable DISABLE TRIGGER trg_Registro_Contable_Audit;

  UPDATE policyRow SET CompanyId=company.CompanyId
  FROM dbo.Transacciones policyRow
  JOIN orion.Company company ON company.Rfc=policyRow.RFC AND company.IsActive=1
  WHERE policyRow.CompanyId IS NULL;
  IF @@ROWCOUNT<>2 THROW 52414,'No se asignaron exactamente las dos polizas revisadas.',1;

  UPDATE line SET CompanyId=policyRow.CompanyId
  FROM dbo.Registro_Contable line
  JOIN dbo.Transacciones policyRow ON policyRow.ID=line.TransaccionID
  WHERE line.CompanyId IS NULL;
  IF @@ROWCOUNT<>4 THROW 52415,'No se asignaron exactamente los cuatro movimientos revisados.',1;

  ALTER TABLE dbo.Transacciones ENABLE TRIGGER trg_Transacciones_Audit;
  ALTER TABLE dbo.Registro_Contable ENABLE TRIGGER trg_Registro_Contable_Audit;
  IF EXISTS(SELECT 1 FROM sys.triggers WHERE object_id IN(OBJECT_ID(N'dbo.trg_Transacciones_Audit'),OBJECT_ID(N'dbo.trg_Registro_Contable_Audit')) AND is_disabled=1)
    THROW 52416,'La auditoria contable no quedo restaurada.',1;

  IF EXISTS
  (
    SELECT 1 FROM dbo.Transacciones policyRow
    LEFT JOIN orion.Company company ON company.CompanyId=policyRow.CompanyId AND company.Rfc=policyRow.RFC AND company.IsActive=1
    WHERE policyRow.CompanyId IS NULL OR company.CompanyId IS NULL
  ) THROW 52417,'Una poliza no tiene correspondencia exacta CompanyId/RFC activa.',1;
  IF EXISTS
  (
    SELECT 1 FROM dbo.Registro_Contable line
    JOIN dbo.Transacciones policyRow ON policyRow.ID=line.TransaccionID
    WHERE line.CompanyId IS NULL OR line.CompanyId<>policyRow.CompanyId
  ) THROW 52418,'Un movimiento no coincide con la empresa de su poliza.',1;

  ALTER TABLE dbo.Transacciones ALTER COLUMN CompanyId bigint NOT NULL;
  ALTER TABLE dbo.Registro_Contable ALTER COLUMN CompanyId bigint NOT NULL;
  CREATE UNIQUE INDEX UX_Transacciones_Company_Id ON dbo.Transacciones(CompanyId,ID);
  ALTER TABLE dbo.Registro_Contable WITH CHECK ADD CONSTRAINT FK_RegistroContable_Company_Transaccion
    FOREIGN KEY(CompanyId,TransaccionID) REFERENCES dbo.Transacciones(CompanyId,ID);

  EXEC(N'CREATE FUNCTION contabilidad.fn_AccountingScopePredicate(@CompanyId bigint)
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE @CompanyId=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.CompanyId''))
    AND EXISTS
    (
      SELECT 1 FROM orion.Company company
      WHERE company.CompanyId=@CompanyId AND company.IsActive=1
        AND company.Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
    );');
  EXEC(N'CREATE SECURITY POLICY contabilidad.AccountingScopePolicy
    ADD FILTER PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Transacciones,
    ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Transacciones AFTER INSERT,
    ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Transacciones AFTER UPDATE,
    ADD FILTER PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Registro_Contable,
    ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Registro_Contable AFTER INSERT,
    ADD BLOCK PREDICATE contabilidad.fn_AccountingScopePredicate(CompanyId) ON dbo.Registro_Contable AFTER UPDATE
    WITH(STATE=ON,SCHEMABINDING=ON);');

  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'contabilidad.AccountingScopePolicy'))<>6
    THROW 52419,'La politica E8a no tiene sus seis predicados.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'contabilidad.fn_AccountingScopePredicate')) LIKE '%IS NULL%'
    THROW 52420,'El predicado contable no debe admitir bypass por NULL.',1;

  SELECT policy.name Politica,policy.is_enabled Habilitada,policy.is_schema_bound ConEsquema,
         (SELECT COUNT(*) FROM sys.security_predicates predicateInfo WHERE predicateInfo.object_id=policy.object_id) Predicados
  FROM sys.security_policies policy WHERE policy.object_id=OBJECT_ID(N'contabilidad.AccountingScopePolicy');

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES(@MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME());
    COMMIT TRANSACTION;
    SELECT N'APLICADO_PRODUCCION' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
