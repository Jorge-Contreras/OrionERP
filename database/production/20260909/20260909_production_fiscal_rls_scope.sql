/*
  E8d production: fail-closed RLS for the owned fiscal declaration aggregate.

  Exact batch: fiscal.PerfilFiscal, EjercicioFiscal, CoeficienteUtilidad,
  DeclaracionPresentada, DeclaracionCierre, and DeclaracionIsrProvisional.
  cfdi.Comprobante and its relationships are not moved or changed.

  This script does not close declarations, stamp documents, recalculate taxes,
  or include or modify 20260907_fiscal_declaracion_deploy.ps1.
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
  IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 52460,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52461,'Esta migracion admite solo produccion o su base de ensayo autorizada.',1;
IF DB_NAME()<>@ExpectedDatabase THROW 52462,'La conexion no apunta a la base declarada.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52463,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52464,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL
  THROW 52465,'La migracion exige OrionRfc en NULL para inventariar todas las filas.',1;
IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR NOT EXISTS(SELECT 1 FROM orion.SchemaMigration WHERE MigrationId=N'20260908_production_accounting_company_identity')
  THROW 52466,'Falta la identidad contable productiva requerida por E8d.',1;

DECLARE @Lote TABLE(Tabla sysname NOT NULL PRIMARY KEY);
INSERT @Lote VALUES
  (N'PerfilFiscal'),(N'EjercicioFiscal'),(N'CoeficienteUtilidad'),
  (N'DeclaracionPresentada'),(N'DeclaracionCierre'),(N'DeclaracionIsrProvisional');
IF EXISTS(SELECT 1 FROM @Lote WHERE OBJECT_ID(N'fiscal.'+Tabla,N'U') IS NULL OR COL_LENGTH(N'fiscal.'+Tabla,N'Rfc') IS NULL)
  THROW 52467,'Falta una tabla o columna Rfc del agregado fiscal.',1;
IF OBJECT_ID(N'fiscal.RfcSecurityPolicy') IS NULL OR OBJECT_ID(N'fiscal.fn_RfcAccessPredicate') IS NULL
  THROW 52468,'Falta la politica heredada fiscal.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52469,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'fiscal.DeclarationScopePolicy') IS NULL
     OR OBJECT_ID(N'fiscal.fn_DeclarationScopePredicate',N'IF') IS NULL
     OR (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'fiscal.DeclarationScopePolicy'))<>18
    THROW 52470,'La migracion esta registrada pero falta parte de la politica E8d.',1;
  SELECT N'YA_APLICADO_PRODUCCION' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  RETURN;
END;
IF OBJECT_ID(N'fiscal.DeclarationScopePolicy') IS NOT NULL
   OR OBJECT_ID(N'fiscal.fn_DeclarationScopePredicate') IS NOT NULL
  THROW 52471,'Ya existe aislamiento fiscal sin registrar.',1;
IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'fiscal.RfcSecurityPolicy'))<>18
  THROW 52472,'La politica fiscal heredada no tiene los dieciocho predicados inventariados.',1;
IF EXISTS
(
  SELECT usedRfc.Rfc FROM
  (
    SELECT Rfc FROM fiscal.PerfilFiscal
    UNION SELECT Rfc FROM fiscal.EjercicioFiscal
    UNION SELECT Rfc FROM fiscal.CoeficienteUtilidad
    UNION SELECT Rfc FROM fiscal.DeclaracionPresentada
    UNION SELECT Rfc FROM fiscal.DeclaracionCierre
    UNION SELECT Rfc FROM fiscal.DeclaracionIsrProvisional
  ) usedRfc
  WHERE NULLIF(LTRIM(RTRIM(usedRfc.Rfc)),'') IS NULL
     OR NOT EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=usedRfc.Rfc AND company.IsActive=1)
) THROW 52473,'El agregado fiscal contiene un RFC sin empresa activa.',1;
IF EXISTS(SELECT 1 FROM sys.security_predicates WHERE target_object_id=OBJECT_ID(N'cfdi.Comprobante'))
  THROW 52474,'E8d no debe imponer propiedad exclusiva a cfdi.Comprobante.',1;

PRINT 'Estado previo del agregado fiscal productivo.';
SELECT source.Rfc,SUM(source.Rows) Filas
FROM
(
  SELECT Rfc,COUNT_BIG(*) Rows FROM fiscal.PerfilFiscal GROUP BY Rfc
  UNION ALL SELECT Rfc,COUNT_BIG(*) FROM fiscal.EjercicioFiscal GROUP BY Rfc
  UNION ALL SELECT Rfc,COUNT_BIG(*) FROM fiscal.CoeficienteUtilidad GROUP BY Rfc
  UNION ALL SELECT Rfc,COUNT_BIG(*) FROM fiscal.DeclaracionPresentada GROUP BY Rfc
  UNION ALL SELECT Rfc,COUNT_BIG(*) FROM fiscal.DeclaracionCierre GROUP BY Rfc
  UNION ALL SELECT Rfc,COUNT_BIG(*) FROM fiscal.DeclaracionIsrProvisional GROUP BY Rfc
) source GROUP BY source.Rfc ORDER BY SUM(source.Rows) DESC;

BEGIN TRY
  BEGIN TRANSACTION;
  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Fiscal:DeclarationScope:Production',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52475,'No se obtuvo el candado del lote productivo E8d.',1;

  EXEC(N'CREATE FUNCTION fiscal.fn_DeclarationScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
    AND EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=@Rfc AND company.IsActive=1);');

  DECLARE @Tabla sysname,@Drop nvarchar(max)=N'ALTER SECURITY POLICY fiscal.RfcSecurityPolicy ',
          @Add nvarchar(max)=N'CREATE SECURITY POLICY fiscal.DeclarationScopePolicy ';
  DECLARE lote_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Tabla FROM @Lote ORDER BY Tabla;
  OPEN lote_cursor;
  FETCH NEXT FROM lote_cursor INTO @Tabla;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Drop+=N'DROP FILTER PREDICATE ON fiscal.'+QUOTENAME(@Tabla)+N',
      DROP BLOCK PREDICATE ON fiscal.'+QUOTENAME(@Tabla)+N' AFTER INSERT,
      DROP BLOCK PREDICATE ON fiscal.'+QUOTENAME(@Tabla)+N' AFTER UPDATE,';
    SET @Add+=N'ADD FILTER PREDICATE fiscal.fn_DeclarationScopePredicate(Rfc) ON fiscal.'+QUOTENAME(@Tabla)+N',
      ADD BLOCK PREDICATE fiscal.fn_DeclarationScopePredicate(Rfc) ON fiscal.'+QUOTENAME(@Tabla)+N' AFTER INSERT,
      ADD BLOCK PREDICATE fiscal.fn_DeclarationScopePredicate(Rfc) ON fiscal.'+QUOTENAME(@Tabla)+N' AFTER UPDATE,';
    FETCH NEXT FROM lote_cursor INTO @Tabla;
  END;
  CLOSE lote_cursor;
  DEALLOCATE lote_cursor;
  SET @Drop=LEFT(@Drop,LEN(@Drop)-1)+N';';
  SET @Add=LEFT(@Add,LEN(@Add)-1)+N' WITH(STATE=ON,SCHEMABINDING=ON);';
  EXEC sys.sp_executesql @Drop;
  DROP SECURITY POLICY fiscal.RfcSecurityPolicy;
  EXEC sys.sp_executesql @Add;

  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'fiscal.DeclarationScopePolicy'))<>18
    THROW 52476,'La politica E8d no tiene sus dieciocho predicados.',1;
  IF OBJECT_ID(N'fiscal.RfcSecurityPolicy') IS NOT NULL
    THROW 52477,'La politica fiscal heredada vacia no fue retirada.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'fiscal.fn_DeclarationScopePredicate')) LIKE '%IS NULL%'
    THROW 52478,'El predicado E8d no debe admitir bypass por NULL.',1;
  IF EXISTS(SELECT 1 FROM sys.security_predicates WHERE target_object_id=OBJECT_ID(N'cfdi.Comprobante'))
    THROW 52479,'E8d no debe imponer propiedad exclusiva a cfdi.Comprobante.',1;

  SELECT policy.name Politica,policy.is_enabled Habilitada,policy.is_schema_bound ConEsquema,
    (SELECT COUNT(*) FROM sys.security_predicates predicateInfo WHERE predicateInfo.object_id=policy.object_id) Predicados
  FROM sys.security_policies policy WHERE policy.object_id=OBJECT_ID(N'fiscal.DeclarationScopePolicy');

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
  IF CURSOR_STATUS('local','lote_cursor')>=0 CLOSE lote_cursor;
  IF CURSOR_STATUS('local','lote_cursor')>=-1 DEALLOCATE lote_cursor;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
