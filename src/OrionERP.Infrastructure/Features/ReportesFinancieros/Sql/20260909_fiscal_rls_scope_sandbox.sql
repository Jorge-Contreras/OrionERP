/*
  E8d: RLS fail-closed del agregado fiscal propio, sólo Orion_Sandbox.

  Lote exacto: las seis tablas fiscal.PerfilFiscal, EjercicioFiscal,
  CoeficienteUtilidad, DeclaracionPresentada, DeclaracionCierre y
  DeclaracionIsrProvisional. cfdi.Comprobante y sus relaciones NO se mueven:
  conservan el contrato emisor/receptor de E3 y un CFDI legítimamente compartido
  no adquiere dueño exclusivo por esta migración.

  No cierra declaraciones, no timbra, no recalcula y no incluye ni modifica
  20260907_fiscal_declaracion_deploy.ps1.
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
BEGIN IF @ApplyChangesInput NOT IN(N'0',N'1') THROW 52300,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput); END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox'
  THROW 52301,'Esta migracion admite exclusivamente Orion_Sandbox.',1;
IF DB_NAME()<>N'Orion_Sandbox' THROW 52302,'La conexion no apunta a Orion_Sandbox.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52303,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52304,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');
IF SESSION_CONTEXT(N'OrionRfc') IS NOT NULL THROW 52305,'La migracion exige OrionRfc en NULL para inventariar todas las filas.',1;

DECLARE @Lote TABLE(Tabla sysname NOT NULL PRIMARY KEY);
INSERT @Lote VALUES
  (N'PerfilFiscal'),(N'EjercicioFiscal'),(N'CoeficienteUtilidad'),
  (N'DeclaracionPresentada'),(N'DeclaracionCierre'),(N'DeclaracionIsrProvisional');
IF EXISTS(SELECT 1 FROM @Lote WHERE OBJECT_ID(N'fiscal.'+Tabla,N'U') IS NULL OR COL_LENGTH(N'fiscal.'+Tabla,N'Rfc') IS NULL)
  THROW 52306,'Falta una tabla o columna Rfc del agregado fiscal.',1;
IF OBJECT_ID(N'fiscal.RfcSecurityPolicy') IS NULL OR OBJECT_ID(N'fiscal.fn_RfcAccessPredicate') IS NULL
  THROW 52307,'Falta la politica heredada fiscal.',1;

DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52308,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'fiscal.DeclarationScopePolicy') IS NULL THROW 52309,'La migracion esta registrada pero falta la politica.',1;
  SELECT N'YA_APLICADO_SOLO_SANDBOX' Estado,@MigrationId MigrationId; RETURN;
END;
IF OBJECT_ID(N'fiscal.DeclarationScopePolicy') IS NOT NULL OR OBJECT_ID(N'fiscal.fn_DeclarationScopePredicate') IS NOT NULL
  THROW 52310,'Ya existe aislamiento fiscal sin registrar.',1;
IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'fiscal.RfcSecurityPolicy'))<>18
  THROW 52311,'La politica fiscal heredada no tiene los dieciocho predicados inventariados.',1;
IF EXISTS
(
  SELECT usedRfc.Rfc FROM
  (
    SELECT Rfc FROM fiscal.PerfilFiscal UNION SELECT Rfc FROM fiscal.EjercicioFiscal
    UNION SELECT Rfc FROM fiscal.CoeficienteUtilidad UNION SELECT Rfc FROM fiscal.DeclaracionPresentada
    UNION SELECT Rfc FROM fiscal.DeclaracionCierre UNION SELECT Rfc FROM fiscal.DeclaracionIsrProvisional
  ) usedRfc
  WHERE NULLIF(LTRIM(RTRIM(usedRfc.Rfc)),'') IS NULL
     OR NOT EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=usedRfc.Rfc AND company.IsActive=1)
) THROW 52312,'El agregado fiscal contiene un RFC sin empresa activa.',1;

PRINT 'Estado previo del agregado fiscal.';
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
  EXEC @LockResult=sys.sp_getapplock @Resource=N'OrionERP:Fiscal:DeclarationScope:Sandbox',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52313,'No se obtuvo el candado del lote E8d.',1;

  EXEC(N'CREATE FUNCTION fiscal.fn_DeclarationScopePredicate(@Rfc varchar(50))
  RETURNS TABLE WITH SCHEMABINDING AS
  RETURN SELECT 1 AS IsAllowed
  WHERE @Rfc=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''))
    AND EXISTS(SELECT 1 FROM orion.Company company WHERE company.Rfc=@Rfc AND company.IsActive=1);');

  DECLARE @Tabla sysname,@Drop nvarchar(max)=N'ALTER SECURITY POLICY fiscal.RfcSecurityPolicy ',
          @Add nvarchar(max)=N'CREATE SECURITY POLICY fiscal.DeclarationScopePolicy ';
  DECLARE lote_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Tabla FROM @Lote ORDER BY Tabla;
  OPEN lote_cursor; FETCH NEXT FROM lote_cursor INTO @Tabla;
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
  CLOSE lote_cursor; DEALLOCATE lote_cursor;
  SET @Drop=LEFT(@Drop,LEN(@Drop)-1)+N';';
  SET @Add=LEFT(@Add,LEN(@Add)-1)+N' WITH(STATE=ON,SCHEMABINDING=ON);';
  EXEC sys.sp_executesql @Drop;
  DROP SECURITY POLICY fiscal.RfcSecurityPolicy;
  EXEC sys.sp_executesql @Add;

  IF (SELECT COUNT(*) FROM sys.security_predicates WHERE object_id=OBJECT_ID(N'fiscal.DeclarationScopePolicy'))<>18
    THROW 52314,'La politica E8d no tiene sus dieciocho predicados.',1;
  IF OBJECT_ID(N'fiscal.RfcSecurityPolicy') IS NOT NULL
    THROW 52315,'La politica fiscal heredada vacia no fue retirada.',1;
  IF OBJECT_DEFINITION(OBJECT_ID(N'fiscal.fn_DeclarationScopePredicate')) LIKE '%IS NULL%'
    THROW 52316,'El predicado E8d no debe admitir bypass por NULL.',1;
  IF EXISTS(SELECT 1 FROM sys.security_predicates WHERE target_object_id=OBJECT_ID(N'cfdi.Comprobante'))
    THROW 52317,'E8d no debe imponer propiedad exclusiva a cfdi.Comprobante.',1;

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
    SELECT N'APLICADO_SOLO_SANDBOX' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  END
  ELSE
  BEGIN ROLLBACK TRANSACTION; SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId; END;
END TRY
BEGIN CATCH
  IF CURSOR_STATUS('local','lote_cursor')>=0 CLOSE lote_cursor;
  IF CURSOR_STATUS('local','lote_cursor')>=-1 DEALLOCATE lote_cursor;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;
