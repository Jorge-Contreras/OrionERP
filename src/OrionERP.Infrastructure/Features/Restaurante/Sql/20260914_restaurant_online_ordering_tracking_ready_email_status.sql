/*
  Lets the public order tracking page tell whether the "order ready" email was sent.

  The page used to say "Te enviamos un correo" as soon as the kitchen marked the
  order ready, even while the durable email was still queued or failing. The
  scoped status procedure now also returns the status of that checkout's Ready
  notification, still resolved only from the verified PublicSite/RFC session
  context and the tracking-token hash.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53891,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53892,'Base esperada no autorizada para el estado del correo de pedido listo.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53893,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_restaurant_online_ordering_tracking_ready_email_status'
  THROW 53894,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53895,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet',N'P') IS NULL
   OR OBJECT_ID(N'restaurante.OnlineOrderNotification',N'U') IS NULL
  THROW 53896,'Faltan objetos requeridos para el estado del correo de pedido listo.',1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering_capture_refund_reconciliation'
    AND UPPER(Checksum)='FC5B5F3BF37427E9F5F7938F63075369FADADFAC68BC71E157578511CE57CDA0'
)
  THROW 53897,'La conciliacion de reembolsos PayPal no coincide con la version revisada.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53898,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:OnlineOrdering:TrackingReadyEmailStatus',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 53899,'No fue posible obtener el bloqueo de la migracion.',1;

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.OnlineCheckoutStatusGet
  @TrackingTokenHash binary(32)
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));

  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL OR @TrackingTokenHash IS NULL
    THROW 53653,''El contexto o token de seguimiento es invalido.'',1;

  SELECT
    attempt.[State] CheckoutStatus,
    CASE
      WHEN attempt.[State]=''Refunded'' OR transactionInfo.[Status]=''Refunded'' THEN ''Refunded''
      WHEN transactionInfo.[Status]=''PartiallyRefunded'' THEN ''PartiallyRefunded''
      WHEN attempt.[State] IN(''Captured'',''CapturedNeedsOrder'',''PosCreated'',''RefundRequested'',''RefundPending'') THEN ''Paid''
      WHEN attempt.[State]=''CapturePending'' THEN ''Pending''
      WHEN attempt.[State]=''PaymentDenied'' THEN ''Denied''
      ELSE ''NotPaid'' END PaymentStatus,
    orderInfo.[Status] OrderStatus,
    orderInfo.Folio OrderFolio,
    attempt.Total,
    attempt.CurrencyCode Currency,
    CONVERT(bit,CASE
      WHEN attempt.[State] IN(''Refunded'',''PaymentDenied'',''Expired'',''Failed'')
        OR orderInfo.[Status] IN(''Completed'',''Cancelled'') THEN 1 ELSE 0 END) IsTerminal,
    CONVERT(nvarchar(240),CASE
      WHEN attempt.[State]=''PosCreated'' AND orderInfo.[Status]=''Ready'' THEN N''Tu pedido esta listo para recoger.''
      WHEN attempt.[State]=''PosCreated'' THEN N''Tu pedido esta confirmado. Te enviaremos un correo cuando este listo.''
      WHEN attempt.[State] IN(''Captured'',''CapturedNeedsOrder'') THEN N''Pago recibido. Estamos confirmando tu pedido.''
      WHEN attempt.[State] IN(''RefundRequested'',''RefundPending'') THEN N''Estamos procesando el reembolso de tu pago.''
      WHEN attempt.[State]=''Refunded'' THEN N''Tu pago fue reembolsado.''
      WHEN attempt.[State]=''PaymentDenied'' THEN N''PayPal no completo el cobro.''
      WHEN attempt.[State]=''RequoteRequired'' THEN N''El menu o el total cambio. Revisa nuevamente tu pedido.''
      WHEN attempt.[State]=''CapturePending'' THEN N''Estamos confirmando el pago con PayPal.''
      ELSE N''El pedido esta pendiente.'' END) [Message],
    attempt.UpdatedAtUtc,
    readyNotification.[Status] ReadyNotificationStatus
  FROM restaurante.OnlineCheckoutAttempt attempt
  LEFT JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=attempt.PublicSiteId
   AND transactionInfo.Rfc=attempt.Rfc
   AND transactionInfo.SiteId=attempt.SiteId
   AND transactionInfo.CheckoutAttemptId=attempt.Id
  LEFT JOIN restaurante.[Order] orderInfo
    ON orderInfo.PublicSiteId=attempt.PublicSiteId
   AND orderInfo.Rfc=attempt.Rfc
   AND orderInfo.SiteId=attempt.SiteId
   AND orderInfo.Id=attempt.RestaurantOrderId
  OUTER APPLY
  (
    SELECT TOP(1) notification.[Status]
    FROM restaurante.OnlineOrderNotification notification
    WHERE notification.PublicSiteId=attempt.PublicSiteId
      AND notification.Rfc=attempt.Rfc
      AND notification.CheckoutAttemptId=attempt.Id
      AND notification.NotificationType=''Ready''
    ORDER BY notification.Id DESC
  ) readyNotification
  WHERE attempt.PublicSiteId=@PublicSiteId
    AND attempt.Rfc=@Rfc
    AND attempt.TrackingTokenHash=@TrackingTokenHash;
END;');

  DECLARE @Definition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.OnlineCheckoutStatusGet')),N'');
  IF CHARINDEX(N'readyNotification.[Status] ReadyNotificationStatus',@Definition)=0
     OR CHARINDEX(N'notification.CheckoutAttemptId=attempt.Id',@Definition)=0
     OR CHARINDEX(N'attempt.TrackingTokenHash=@TrackingTokenHash',@Definition)=0
    THROW 53900,'El procedimiento de estado de seguimiento no quedo actualizado.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    CONVERT(bit,1) ReadyNotificationStatusReturned,
    CONVERT(bit,1) TrackingTokenScopeRetained;

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
  THROW;
END CATCH;
