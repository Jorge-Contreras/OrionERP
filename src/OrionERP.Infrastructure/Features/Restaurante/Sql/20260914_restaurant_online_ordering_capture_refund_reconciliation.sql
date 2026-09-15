/*
  Reconciles completed PayPal refunds reported as PAYMENT.CAPTURE.REFUNDED.

  PayPal sends a completed refund as PAYMENT.CAPTURE.REFUNDED. Its resource is
  the refund itself: it names the capture only through the "up" link and omits
  related_ids. PaymentGatewayRefundEventBind accepted only PAYMENT.REFUND.* with
  a stored related capture, so every completed refund stayed failed for manual
  reconciliation. The worker passes the capture it reads back from PayPal; this
  version accepts the event while still requiring that capture, the refund
  identity, the checkout, the amount and the currency to match the local refund.
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
  IF @ApplyInput NOT IN(N'0',N'1') THROW 53880,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN(N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 53881,'Base esperada no autorizada para conciliar reembolsos PayPal.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 53882,'La conexion no apunta a la base declarada.',1;
IF @MigrationId<>N'20260914_restaurant_online_ordering_capture_refund_reconciliation'
  THROW 53883,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+N'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 53884,'MigrationChecksum invalido.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%'
  SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'restaurante.PaymentGatewayRefundEventBind',N'P') IS NULL
  THROW 53885,'Faltan objetos requeridos para conciliar reembolsos PayPal.',1;

IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering_runtime_corrections'
    AND UPPER(Checksum)='77204A1874353A6DA27A739D80F7B67478EC5527FAE7DF04A76009CB06C3747E'
)
  THROW 53886,'La correccion de online ordering no coincide con la version revisada.',1;
IF NOT EXISTS
(
  SELECT 1 FROM orion.SchemaMigration
  WHERE MigrationId=N'20260914_restaurant_online_ordering_status_view_permission_hotfix'
    AND UPPER(Checksum)='71E83BE23B5FB6673DA6824291BFF9C488A194152403CF93B47FB56C8606F0A9'
)
  THROW 53887,'El hotfix de permisos de online ordering no coincide con la version revisada.',1;

DECLARE @ExistingChecksum char(64)=
  (SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 53888,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum;
  RETURN;
END;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Restaurant:OnlineOrdering:CaptureRefundReconciliation',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=30000;
  IF @LockResult<0 THROW 53889,'No fue posible obtener el bloqueo de la migracion.',1;

  EXEC(N'
CREATE OR ALTER PROCEDURE restaurante.PaymentGatewayRefundEventBind
  @EventId bigint,
  @LeaseId uniqueidentifier,
  @ProviderRefundId varchar(64),
  @ProviderCaptureId varchar(64),
  @Amount decimal(18,2),
  @CurrencyCode char(3)
AS
BEGIN
  SET NOCOUNT ON;
  SET XACT_ABORT ON;

  DECLARE @PublicSiteId bigint=TRY_CONVERT(bigint,SESSION_CONTEXT(N''OrionERP.PublicSiteId''));
  DECLARE @Rfc varchar(50)=CONVERT(varchar(50),SESSION_CONTEXT(N''OrionRfc''));
  DECLARE @EventType varchar(100);
  DECLARE @ResourceType varchar(80);
  DECLARE @ResourceId varchar(100);
  DECLARE @RelatedOrderId varchar(64);
  DECLARE @RelatedCaptureId varchar(64);
  DECLARE @RelatedRefundId varchar(64);
  DECLARE @EventCheckoutAttemptId uniqueidentifier;
  DECLARE @RefundId uniqueidentifier;
  DECLARE @ExistingProviderRefundId varchar(64);
  DECLARE @RefundStatus varchar(30);
  DECLARE @KnownCheckoutAttemptId uniqueidentifier;
  DECLARE @KnownCaptureId varchar(64);
  DECLARE @KnownOrderId varchar(64);
  DECLARE @KnownAmount decimal(18,2);
  DECLARE @KnownCurrencyCode char(3);
  DECLARE @WasBound bit=0;
  DECLARE @CandidateCount int;
  DECLARE @Candidates TABLE
  (
    RefundId uniqueidentifier NOT NULL PRIMARY KEY,
    ProviderRefundId varchar(64) NULL,
    RefundStatus varchar(30) NOT NULL
  );

  SET @ProviderRefundId=NULLIF(LTRIM(RTRIM(@ProviderRefundId)),'''');
  SET @ProviderCaptureId=NULLIF(LTRIM(RTRIM(@ProviderCaptureId)),'''');
  SET @CurrencyCode=UPPER(@CurrencyCode);
  IF @PublicSiteId IS NULL OR NULLIF(@Rfc,'''') IS NULL
    THROW 53762,''No existe un contexto publico de restaurante verificado.'',1;
  IF @EventId IS NULL OR @LeaseId IS NULL OR @ProviderRefundId IS NULL
     OR @ProviderCaptureId IS NULL OR @Amount<=0
     OR LEN(@CurrencyCode)<>3 OR @CurrencyCode LIKE ''%[^A-Z]%'' COLLATE Latin1_General_100_BIN2
    THROW 53763,''Los datos verificados del reembolso son invalidos.'',1;

  BEGIN TRANSACTION;

  SELECT @EventType=eventInfo.EventType,@ResourceType=eventInfo.ResourceType,
    @ResourceId=eventInfo.ResourceId,@RelatedOrderId=eventInfo.RelatedOrderId,
    @RelatedCaptureId=eventInfo.RelatedCaptureId,@RelatedRefundId=eventInfo.RelatedRefundId,
    @EventCheckoutAttemptId=eventInfo.CheckoutAttemptId
  FROM restaurante.PaymentGatewayEvent eventInfo WITH(UPDLOCK,HOLDLOCK)
  WHERE eventInfo.Id=@EventId AND eventInfo.PublicSiteId=@PublicSiteId
    AND eventInfo.Rfc=@Rfc AND eventInfo.VerificationStatus=''Verified''
    AND eventInfo.ProcessingStatus=''Processing'' AND eventInfo.LeaseId=@LeaseId;

  IF @EventCheckoutAttemptId IS NULL
  BEGIN
    ROLLBACK TRANSACTION;
    THROW 53764,''La concesion del webhook ya no es valida.'',1;
  END;

  IF (@EventType NOT LIKE ''PAYMENT.REFUND.%'' AND @EventType<>''PAYMENT.CAPTURE.REFUNDED'')
     OR LOWER(COALESCE(@ResourceType,''''))<>''refund''
     OR (@RelatedCaptureId IS NULL AND @EventType LIKE ''PAYMENT.REFUND.%'')
     OR (@RelatedRefundId IS NULL AND @ResourceId IS NULL)
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Unsupported'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  IF (@RelatedCaptureId IS NOT NULL AND @RelatedCaptureId<>@ProviderCaptureId)
     OR COALESCE(@RelatedRefundId,@ResourceId)<>@ProviderRefundId
     OR (@RelatedRefundId IS NOT NULL AND @ResourceId IS NOT NULL
         AND @RelatedRefundId<>@ResourceId)
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  SELECT @RefundId=refundInfo.Id,@RefundStatus=refundInfo.[Status],
    @KnownCheckoutAttemptId=transactionInfo.CheckoutAttemptId,
    @KnownCaptureId=transactionInfo.ProviderCaptureId,
    @KnownOrderId=transactionInfo.ProviderOrderId,
    @KnownAmount=refundInfo.Amount,@KnownCurrencyCode=refundInfo.CurrencyCode
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.ProviderRefundId=@ProviderRefundId;

  IF @RefundId IS NOT NULL
  BEGIN
    IF @KnownCheckoutAttemptId=@EventCheckoutAttemptId
       AND @KnownCaptureId=@ProviderCaptureId
       AND (@RelatedOrderId IS NULL OR @KnownOrderId=@RelatedOrderId)
       AND @KnownAmount=@Amount AND @KnownCurrencyCode=@CurrencyCode
    BEGIN
      COMMIT TRANSACTION;
      SELECT CONVERT(varchar(20),''Matched'') MatchOutcome,@RefundId RefundId,
        CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
        @ProviderRefundId ProviderRefundId;
      RETURN;
    END;

    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,@RefundId RefundId,
      CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
      @ProviderRefundId ProviderRefundId;
    RETURN;
  END;

  INSERT @Candidates(RefundId,ProviderRefundId,RefundStatus)
  SELECT refundInfo.Id,refundInfo.ProviderRefundId,refundInfo.[Status]
  FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
  JOIN restaurante.PaymentGatewayTransaction transactionInfo
    ON transactionInfo.PublicSiteId=refundInfo.PublicSiteId
   AND transactionInfo.Rfc=refundInfo.Rfc
   AND transactionInfo.SiteId=refundInfo.SiteId
   AND transactionInfo.Id=refundInfo.GatewayTransactionId
  WHERE refundInfo.PublicSiteId=@PublicSiteId AND refundInfo.Rfc=@Rfc
    AND refundInfo.LocalRefundId IS NULL
    AND transactionInfo.CheckoutAttemptId=@EventCheckoutAttemptId
    AND transactionInfo.ProviderCaptureId=@ProviderCaptureId
    AND (@RelatedOrderId IS NULL OR transactionInfo.ProviderOrderId=@RelatedOrderId)
    AND refundInfo.Amount=@Amount AND refundInfo.CurrencyCode=@CurrencyCode;

  SELECT @CandidateCount=COUNT(*) FROM @Candidates;
  IF @CandidateCount=0
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Unmatched'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;
  IF @CandidateCount>1
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Ambiguous'') MatchOutcome,
      CONVERT(uniqueidentifier,NULL) RefundId,CONVERT(bit,0) WasBound,
      CONVERT(varchar(30),NULL) RefundStatus,CONVERT(varchar(64),NULL) ProviderRefundId;
    RETURN;
  END;

  SELECT @RefundId=RefundId,@ExistingProviderRefundId=ProviderRefundId,
    @RefundStatus=RefundStatus FROM @Candidates;
  IF (@ExistingProviderRefundId IS NOT NULL AND @ExistingProviderRefundId<>@ProviderRefundId)
     OR EXISTS
     (
       SELECT 1 FROM restaurante.PaymentGatewayRefund refundInfo WITH(UPDLOCK,HOLDLOCK)
       WHERE refundInfo.PublicSiteId=@PublicSiteId
         AND refundInfo.ProviderRefundId=@ProviderRefundId AND refundInfo.Id<>@RefundId
     )
  BEGIN
    COMMIT TRANSACTION;
    SELECT CONVERT(varchar(20),''Conflict'') MatchOutcome,
      @RefundId RefundId,CONVERT(bit,0) WasBound,@RefundStatus RefundStatus,
      @ExistingProviderRefundId ProviderRefundId;
    RETURN;
  END;

  IF @ExistingProviderRefundId IS NULL
  BEGIN
    UPDATE restaurante.PaymentGatewayRefund
    SET ProviderRefundId=@ProviderRefundId,
        NextRetryAtUtc=CASE WHEN [Status] IN(''Requested'',''Pending'',''Failed'')
          THEN SYSUTCDATETIME() ELSE NextRetryAtUtc END,
        UpdatedAtUtc=SYSUTCDATETIME()
    WHERE Id=@RefundId AND PublicSiteId=@PublicSiteId AND Rfc=@Rfc
      AND ProviderRefundId IS NULL AND LocalRefundId IS NULL;
    IF @@ROWCOUNT<>1
    BEGIN
      ROLLBACK TRANSACTION;
      THROW 53765,''El reembolso cambio durante la conciliacion del webhook.'',1;
    END;
    SET @WasBound=1;
  END;

  COMMIT TRANSACTION;

  SELECT CONVERT(varchar(20),''Matched'') MatchOutcome,@RefundId RefundId,
    @WasBound WasBound,refundInfo.[Status] RefundStatus,
    refundInfo.ProviderRefundId
  FROM restaurante.PaymentGatewayRefund refundInfo
  WHERE refundInfo.Id=@RefundId AND refundInfo.PublicSiteId=@PublicSiteId
    AND refundInfo.Rfc=@Rfc;
END;');

  DECLARE @Definition nvarchar(max)=
    COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'restaurante.PaymentGatewayRefundEventBind')),N'');
  IF CHARINDEX(N'@EventType<>''PAYMENT.CAPTURE.REFUNDED''',@Definition)=0
     OR CHARINDEX(N'@RelatedCaptureId IS NOT NULL AND @RelatedCaptureId<>@ProviderCaptureId',@Definition)=0
     OR CHARINDEX(N'transactionInfo.ProviderCaptureId=@ProviderCaptureId',@Definition)=0
    THROW 53890,'El procedimiento de conciliacion de reembolsos no quedo actualizado.',1;

  SELECT DB_NAME() DatabaseName,@ApplyChanges ApplyChanges,
    CONVERT(bit,1) CaptureRefundedAccepted,
    CONVERT(bit,1) ProviderCaptureStillRequired;

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
