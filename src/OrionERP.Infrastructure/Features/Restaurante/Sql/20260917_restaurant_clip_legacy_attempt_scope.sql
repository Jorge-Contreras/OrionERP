/*
  Cierra los intentos de la era PayPal que 20260916_restaurant_online_ordering_clip
  dejo abiertos.

  Aquella migracion si traia los dos UPDATE y hasta una post-condicion que los
  verificaba, pero los tres corrieron ciegos. restaurante.OnlineCheckoutAttempt
  esta bajo OnlineOrderingScopePolicy, y el predicado solo deja ver filas cuando
  la sesion trae OrionRfc y OrionERP.CompanyId. El migrador abre su conexion sin
  contexto de sesion y su login no es sa, asi que la tabla se veia vacia: los
  UPDATE tocaron cero filas, el conteo de control devolvio cero y la migracion
  se registro como aplicada. El DDL si entro, porque el DDL no pasa por RLS.

  De ahi que esta correccion resuelva el binding de Bruno's y fije el contexto
  antes de tocar la tabla, igual que 20260914_restaurant_online_ordering.sql, y
  que su verificacion final corra dentro de ese mismo contexto. Una comprobacion
  que corre mas ciega que la escritura que revisa no comprueba nada.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=NULL;

IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 54070,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio')
  THROW 54071,'Base esperada no autorizada para esta correccion.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 54072,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260917_restaurant_clip_legacy_attempt_scope'
  THROW 54073,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 54074,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

-- Solo tiene sentido despues de la migracion a Clip: son sus columnas las que
-- esta correccion rellena.
IF NOT EXISTS(SELECT 1 FROM orion.SchemaMigration
              WHERE MigrationId=N'20260916_restaurant_online_ordering_clip')
  THROW 54075,'Falta 20260916_restaurant_online_ordering_clip.',1;
IF COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'Provider') IS NULL
   OR COL_LENGTH(N'restaurante.OnlineCheckoutAttempt',N'ProviderOrderId') IS NULL
  THROW 54076,'Faltan las columnas neutrales de proveedor.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 54077,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:OnlineOrdering:ClipMigration',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 54078,'No fue posible obtener el bloqueo de la migracion.',1;

  /* ---------- Contexto de inquilino ---------- */

  DECLARE @TargetPublicSiteId bigint;
  DECLARE @TargetCompanyId bigint;
  DECLARE @TargetOrionSiteId bigint;
  DECLARE @TargetRfc varchar(50)='BRUNOS260707L26';

  SELECT @TargetPublicSiteId=publicSite.PublicSiteId,
    @TargetCompanyId=publicSite.CompanyId,@TargetOrionSiteId=publicSite.SiteId
  FROM orion.PublicSite publicSite
  JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
  JOIN orion.Site platformSite
    ON platformSite.CompanyId=publicSite.CompanyId AND platformSite.SiteId=publicSite.SiteId
  WHERE publicSite.PublicSiteKey='brunos-main'
    AND publicSite.ModuleCode='RESTAURANT'
    AND companyInfo.Rfc=@TargetRfc
    AND platformSite.SiteKey='brunos-01'
    AND publicSite.IsActive=1 AND companyInfo.IsActive=1 AND platformSite.IsActive=1;

  IF @TargetPublicSiteId IS NULL
    THROW 54079,'No existe el binding central exacto brunos-main/BRUNOS260707L26/brunos-01.',1;

  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@TargetRfc;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@TargetCompanyId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=@TargetOrionSiteId;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=@TargetPublicSiteId;

  /* ---------- Prueba de que el contexto abre toda la tabla ---------- */

  -- El fallo original fue escribir sobre una tabla que se veia vacia. Antes de
  -- tocar nada se compara lo visible contra el conteo fisico, que RLS no filtra.
  -- Si el contexto solo abriera una parte, esta correccion volveria a dejar
  -- filas sin convertir y hay que detenerse.
  DECLARE @VisibleRows int;
  DECLARE @PhysicalRows bigint;
  SELECT @VisibleRows=COUNT(*) FROM restaurante.OnlineCheckoutAttempt;
  SELECT @PhysicalRows=SUM(partitionInfo.row_count)
  FROM sys.dm_db_partition_stats partitionInfo
  WHERE partitionInfo.object_id=OBJECT_ID(N'restaurante.OnlineCheckoutAttempt')
    AND partitionInfo.index_id IN(0,1);
  IF @VisibleRows<>@PhysicalRows
    THROW 54080,'El contexto de sesion no abre todos los intentos: RLS sigue ocultando filas.',1;

  /* ---------- Conversion de los intentos de la era PayPal ---------- */

  -- La migracion a Clip marca el corte: todo intento anterior nacio con PayPal.
  -- Acotar por esa fecha importa porque un cobro Clip real tambien llena
  -- ProviderOrderId, y la condicion original lo habria reetiquetado como PayPal.
  DECLARE @ClipAppliedAtUtc datetime2(3)=
    (SELECT MAX(AppliedAtUtc) FROM orion.SchemaMigration
     WHERE MigrationId=N'20260916_restaurant_online_ordering_clip');
  IF @ClipAppliedAtUtc IS NULL
    THROW 54081,'No se pudo fechar la migracion a Clip.',1;

  DECLARE @Relabeled int;
  UPDATE restaurante.OnlineCheckoutAttempt
  SET Provider=N'PayPal'
  WHERE Provider=N'Clip'
    AND CreatedAtUtc<@ClipAppliedAtUtc
    AND (ProviderOrderId IS NOT NULL OR [State]=N'PayPalCreated' OR [State]=N'CapturePending');
  SET @Relabeled=@@ROWCOUNT;

  DECLARE @Closed int;
  UPDATE restaurante.OnlineCheckoutAttempt
  SET [State]=N'Expired',
      FailureCode=N'PROVIDER_MIGRATED',
      FailureMessage=N'El intento quedo abierto con PayPal, que no podia cobrar tarjetas en esta cuenta.',
      NextRetryAtUtc=NULL,
      RecoveryLeaseId=NULL,
      RecoveryLeaseExpiresAtUtc=NULL,
      UpdatedAtUtc=SYSUTCDATETIME()
  WHERE Provider=N'PayPal'
    AND [State] IN(N'Quoted',N'PayPalCreated',N'CapturePending',N'RequoteRequired');
  SET @Closed=@@ROWCOUNT;

  /* ---------- Verificacion, ya dentro del contexto ---------- */

  DECLARE @OpenLegacyAttempts int;
  SELECT @OpenLegacyAttempts=COUNT(*)
  FROM restaurante.OnlineCheckoutAttempt
  WHERE [State] IN(N'PayPalCreated',N'CapturePending')
     OR (Provider=N'PayPal'
         AND [State] IN(N'Quoted',N'RequoteRequired'));
  IF @OpenLegacyAttempts>0
    THROW 54082,'Quedaron intentos de PayPal sin cerrar.',1;

  DECLARE @LegacyAttempts int;
  SELECT @LegacyAttempts=COUNT(*)
  FROM restaurante.OnlineCheckoutAttempt
  WHERE Provider=N'PayPal';

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    @VisibleRows AttemptsInScope,
    @Relabeled RelabeledAsPayPal,
    @Closed ClosedAsExpired,
    @LegacyAttempts LegacyAttempts,
    @OpenLegacyAttempts OpenLegacyAttempts;

  -- El contexto se limpia antes de registrar la migracion para que el asiento
  -- en orion.SchemaMigration no dependa del alcance de un inquilino.
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES
    (
      @MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),
        CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME()
    );
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'PREVIEW_VALIDADO_SIN_CAMBIOS' Estado,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  EXEC sys.sp_set_session_context @key=N'OrionERP.PublicSiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.SiteId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=NULL;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=NULL;
  THROW;
END CATCH;

/*
  Reversa manual: los intentos cerrados aqui nunca cobraron nada, asi que no hay
  dinero que devolver. Para revertir basta con volver a poner su estado anterior
  (PayPalCreated) y limpiar FailureCode/FailureMessage, fijando antes el mismo
  contexto de sesion que usa esta migracion. Sin ese contexto el UPDATE vuelve a
  no tocar ninguna fila, que es justo el fallo que esto corrige.
*/
