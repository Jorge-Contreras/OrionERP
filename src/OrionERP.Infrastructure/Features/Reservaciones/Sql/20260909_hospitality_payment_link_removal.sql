/*
  E7: audited removal of the 288 erroneous OHM reservation -> BSU payment links.

  Business decision recorded on 2026-09-09:
  - every affected dbo.Transacciones row belongs to BSU210121M77;
  - none of those transactions may be related to OHM191112Q26;
  - transaction headers, accounting rows, CFDI links, amounts and balances remain intact;
  - only dbo.Reservation_Transacciones rows in the reviewed set are removed.

  The reviewed set is locked by its SHA-256 fingerprint, split into at most 25 rows
  per logical batch, previewed with one checksum per batch, and applied atomically.
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
DECLARE @ReviewedSetChecksum char(64)='CE25D7CFD35BCDD6554E2C4798A2FA51825E04B7849B3DBB5FAF99197862A40A';
DECLARE @ExpectedRows int=288;
DECLARE @ExpectedAmount decimal(19,2)=852308.75;
DECLARE @OhmCompanyId bigint=9;
DECLARE @OhmSiteId bigint=3;
DECLARE @OhmRfc varchar(50)='OHM191112Q26';
DECLARE @BsuCompanyId bigint=3;
DECLARE @BsuRfc varchar(50)='BSU210121M77';
DECLARE @PreviousHospitalityCompany sql_variant=SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId');
DECLARE @PreviousHospitalitySite sql_variant=SESSION_CONTEXT(N'OrionERP.HospitalitySiteId');
DECLARE @PreviousHospitalityRfc sql_variant=SESSION_CONTEXT(N'OrionERP.HospitalityRfc');
DECLARE @PreviousAccountingCompany sql_variant=SESSION_CONTEXT(N'OrionERP.CompanyId');
DECLARE @PreviousAccountingRfc sql_variant=SESSION_CONTEXT(N'OrionRfc');

IF @ApplyChangesInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyChangesInput NOT IN (N'0',N'1') THROW 52300,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyChangesInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%'
   OR @ExpectedDatabase NOT IN (N'Orion_Sandbox',N'grupocarpio',N'Orion_CutoverValidation_20260908')
  THROW 52301,'La base esperada no esta autorizada para este corrector E7.',1;
IF DB_NAME()<>@ExpectedDatabase
  THROW 52302,'La conexion no apunta a la base declarada.',1;
IF @MigrationId LIKE N'$'+N'(%' OR NULLIF(LTRIM(RTRIM(@MigrationId)),N'') IS NULL OR LEN(@MigrationId)>200
  THROW 52303,'MigrationId es obligatorio y debe provenir del manifiesto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52304,'MigrationChecksum debe ser un SHA-256 hexadecimal de 64 caracteres.',1;
IF @AppVersionInput NOT LIKE N'$'+N'(%' SET @AppVersion=NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'');

IF OBJECT_ID(N'orion.SchemaMigration',N'U') IS NULL
   OR OBJECT_ID(N'orion.HospitalityScopePolicy') IS NULL
   OR OBJECT_ID(N'orion.fn_HospitalityScopePredicate',N'IF') IS NULL
   OR OBJECT_ID(N'orion.fn_HospitalityPaymentScopePredicate',N'IF') IS NULL
   OR OBJECT_ID(N'dbo.RESERVATION',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Reservation_Transacciones',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transacciones',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Registro_Contable',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Transaccion_Comprobante',N'U') IS NULL
  THROW 52305,'Faltan prerrequisitos de Hospedaje o Contabilidad.',1;

IF DB_NAME()=N'Orion_Sandbox'
   AND NOT EXISTS
   (
     SELECT 1 FROM orion.SchemaMigration
     WHERE MigrationId=N'20260909_hospitality_payment_policy_switch_sandbox'
   )
  THROW 52306,'Sandbox no tiene el corrector E7 protegido completo.',1;
IF DB_NAME()<>N'Orion_Sandbox'
   AND NOT EXISTS
   (
     SELECT 1 FROM orion.SchemaMigration
     WHERE MigrationId=N'20260908_production_hospitality_administration_scope'
   )
  THROW 52307,'Produccion no tiene el aislamiento administrativo de Hospedaje requerido.',1;

DECLARE @ExistingChecksum char(64)=
(
  SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId
);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52308,'El mismo MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN
  IF OBJECT_ID(N'orion.HospitalityPaymentLinkRemovalManifest',N'U') IS NULL
     OR OBJECT_ID(N'orion.HospitalityPaymentLinkRemovalAudit',N'U') IS NULL
    THROW 52309,'La migracion esta registrada pero falta su evidencia E7.',1;
  SELECT N'YA_APLICADO' Estado,DB_NAME() BaseDatos,@MigrationId MigrationId;
  RETURN;
END;

IF OBJECT_ID(N'orion.HospitalityPaymentLinkRemovalManifest',N'U') IS NOT NULL
   OR OBJECT_ID(N'orion.HospitalityPaymentLinkRemovalAudit',N'U') IS NOT NULL
  THROW 52310,'Existen objetos de eliminacion E7 sin ledger; requieren revision manual.',1;
IF NOT EXISTS
(
  SELECT 1 FROM sys.security_policies
  WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy') AND is_enabled=1 AND is_schema_bound=1
)
  THROW 52311,'La politica de Hospedaje no esta activa.',1;
IF
(
  SELECT COUNT(*) FROM sys.security_predicates
  WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')
    AND target_object_id=OBJECT_ID(N'dbo.Reservation_Transacciones')
    AND predicate_definition LIKE N'%fn_HospitalityPaymentScopePredicate%'
)<>3
  THROW 52312,'Reservation_Transacciones no tiene el predicado fuerte esperado.',1;
IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.Reservation_Transacciones') AND is_disabled=0)
  THROW 52313,'Reservation_Transacciones ahora tiene triggers; debe revisarse su efecto antes de eliminar vinculos.',1;

BEGIN TRY
  BEGIN TRANSACTION;

  DECLARE @LockResult int;
  EXEC @LockResult=sys.sp_getapplock
    @Resource=N'OrionERP:Hospitality:PaymentCorrection:9:3',
    @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
  IF @LockResult<0 THROW 52314,'No se obtuvo el candado exclusivo del corrector de pagos.',1;

  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@OhmCompanyId,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@OhmSiteId,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=@OhmRfc,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@BsuCompanyId,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@BsuRfc,@read_only=0;

  /* Keep RLS enabled while exposing only the selected OHM site for reconciliation. */
  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    DROP FILTER PREDICATE ON dbo.Reservation_Transacciones,
    DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER INSERT,
    DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;');
  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(OrionCompanyId,OrionSiteId) ON dbo.Reservation_Transacciones AFTER UPDATE;');

  ;WITH reviewed AS
  (
    SELECT
      ROW_NUMBER() OVER (ORDER BY link.ReservationID,link.TransaccionID) RowNumber,
      link.ReservationID,
      link.TransaccionID,
      link.Amount LinkAmount,
      payment.RFC PaymentRfc,
      payment.CompanyId PaymentCompanyId,
      payment.Monto PaymentAmount,
      accountingRows.AccountingLineCount,
      cfdiRows.CfdiLinkCount,
      scopedLinks.ScopedLinkCount
    FROM dbo.Reservation_Transacciones link
    JOIN dbo.RESERVATION reservation
      ON reservation.ID=link.ReservationID
     AND reservation.OrionCompanyId=@OhmCompanyId
     AND reservation.OrionSiteId=@OhmSiteId
    JOIN dbo.Transacciones payment ON payment.ID=link.TransaccionID
    OUTER APPLY
    (
      SELECT COUNT_BIG(*) AccountingLineCount
      FROM dbo.Registro_Contable accountingLine
      WHERE accountingLine.TransaccionID=payment.ID
    ) accountingRows
    OUTER APPLY
    (
      SELECT COUNT_BIG(*) CfdiLinkCount
      FROM dbo.Transaccion_Comprobante cfdiLink
      WHERE cfdiLink.Transaccion_ID=payment.ID
    ) cfdiRows
    OUTER APPLY
    (
      SELECT COUNT_BIG(*) ScopedLinkCount
      FROM dbo.Reservation_Transacciones otherLink
      WHERE otherLink.OrionCompanyId=@OhmCompanyId
        AND otherLink.OrionSiteId=@OhmSiteId
        AND otherLink.TransaccionID=payment.ID
    ) scopedLinks
    WHERE link.OrionCompanyId=@OhmCompanyId
      AND link.OrionSiteId=@OhmSiteId
      AND payment.RFC=@BsuRfc
  )
  SELECT
    CONVERT(smallint,((RowNumber-1)/25)+1) BatchNumber,
    CONVERT(uniqueidentifier,HASHBYTES('MD5',CONVERT(nvarchar(200),
      CONCAT(N'OrionERP:E7:OHM-BSU:20260909:',CONVERT(smallint,((RowNumber-1)/25)+1))))) BatchKey,
    ReservationID,TransaccionID,LinkAmount,PaymentRfc,PaymentCompanyId,PaymentAmount,
    CONVERT(int,AccountingLineCount) AccountingLineCount,
    CONVERT(int,CfdiLinkCount) CfdiLinkCount,
    CONVERT(int,ScopedLinkCount) ScopedLinkCount
  INTO #ApprovedLinks
  FROM reviewed;

  IF (SELECT COUNT(*) FROM #ApprovedLinks)<>@ExpectedRows
    THROW 52315,'El conjunto ya no contiene exactamente los 288 vinculos revisados.',1;
  IF (SELECT COUNT(DISTINCT TransaccionID) FROM #ApprovedLinks)<>@ExpectedRows
    THROW 52316,'Una transaccion del conjunto aparece en mas de un vinculo.',1;
  IF EXISTS
  (
    SELECT 1 FROM #ApprovedLinks
    WHERE PaymentRfc<>@BsuRfc OR PaymentCompanyId<>@BsuCompanyId
      OR LinkAmount<>PaymentAmount OR ScopedLinkCount<>1
  )
    THROW 52317,'El estado actual contradice la decision empresarial sobre BSU.',1;
  IF (SELECT CONVERT(decimal(19,2),SUM(CONVERT(decimal(19,2),LinkAmount))) FROM #ApprovedLinks)<>@ExpectedAmount
    THROW 52318,'El total de los vinculos revisados cambio.',1;

  DECLARE @ActualReviewedSetChecksum char(64);
  SELECT @ActualReviewedSetChecksum=CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),
    STRING_AGG(CONVERT(nvarchar(max),CONCAT(ReservationID,N'|',TransaccionID,N'|',
      CONVERT(decimal(19,4),LinkAmount),N'|',PaymentRfc,N'|',PaymentCompanyId,N'|',
      CONVERT(decimal(19,4),PaymentAmount))),NCHAR(10))
      WITHIN GROUP (ORDER BY ReservationID,TransaccionID))),2)
  FROM #ApprovedLinks;
  IF @ActualReviewedSetChecksum<>@ReviewedSetChecksum
    THROW 52319,'El fingerprint de los 288 vinculos no coincide con el conjunto aprobado.',1;
  IF (SELECT COUNT(DISTINCT BatchKey) FROM #ApprovedLinks)<>12
     OR EXISTS (SELECT 1 FROM #ApprovedLinks GROUP BY BatchKey HAVING COUNT(*) NOT BETWEEN 1 AND 25)
    THROW 52320,'La division del conjunto no respeta los lotes maximos de 25.',1;

  CREATE TABLE orion.HospitalityPaymentLinkRemovalManifest
  (
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    BatchNumber smallint NOT NULL,
    BatchKey uniqueidentifier NOT NULL,
    ReservationId int NOT NULL,
    TransactionId int NOT NULL,
    ExpectedLinkAmount money NOT NULL,
    ExpectedPaymentRfc varchar(50) NOT NULL,
    ExpectedPaymentCompanyId bigint NOT NULL,
    ExpectedPaymentAmount money NOT NULL,
    ExpectedAccountingLineCount int NOT NULL,
    ExpectedCfdiLinkCount int NOT NULL,
    ReviewedSetChecksum char(64) NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    Reason nvarchar(1000) NOT NULL,
    IsApproved bit NOT NULL,
    ApprovedAtUtc datetime2(3) NOT NULL,
    ApprovedBy nvarchar(256) NOT NULL,
    CreatedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityPaymentLinkRemovalManifest_Created DEFAULT (SYSUTCDATETIME()),
    CreatedBy nvarchar(256) NOT NULL,
    CONSTRAINT PK_HospitalityPaymentLinkRemovalManifest
      PRIMARY KEY (CompanyId,SiteId,BatchKey,ReservationId,TransactionId),
    CONSTRAINT UQ_HospitalityPaymentLinkRemovalManifest_Source
      UNIQUE (CompanyId,SiteId,ReservationId,TransactionId),
    CONSTRAINT CK_HospitalityPaymentLinkRemovalManifest_Values CHECK
    (
      CompanyId>0 AND SiteId>0 AND BatchNumber>0 AND ReservationId>0 AND TransactionId>0
      AND ExpectedLinkAmount>=0 AND ExpectedPaymentAmount>=0
      AND ExpectedAccountingLineCount>=0 AND ExpectedCfdiLinkCount>=0
      AND LEN(ReviewedSetChecksum)=64
      AND NULLIF(LTRIM(RTRIM(Evidence)),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(Reason)),N'') IS NOT NULL
      AND IsApproved=1
    ),
    CONSTRAINT FK_HospitalityPaymentLinkRemovalManifest_Site
      FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId)
  );

  CREATE TABLE orion.HospitalityPaymentLinkRemovalAudit
  (
    AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalityPaymentLinkRemovalAudit PRIMARY KEY,
    CompanyId bigint NOT NULL,
    SiteId bigint NOT NULL,
    BatchNumber smallint NOT NULL,
    BatchKey uniqueidentifier NOT NULL,
    ReservationId int NOT NULL,
    TransactionId int NOT NULL,
    RemovedLinkAmount money NOT NULL,
    PaymentRfc varchar(50) NOT NULL,
    PaymentCompanyId bigint NOT NULL,
    PaymentAmount money NOT NULL,
    AccountingLineCount int NOT NULL,
    CfdiLinkCount int NOT NULL,
    ReviewedSetChecksum char(64) NOT NULL,
    PreviewChecksum char(64) NOT NULL,
    Evidence nvarchar(1000) NOT NULL,
    Reason nvarchar(1000) NOT NULL,
    AppliedAtUtc datetime2(3) NOT NULL CONSTRAINT DF_HospitalityPaymentLinkRemovalAudit_Applied DEFAULT (SYSUTCDATETIME()),
    AppliedBy nvarchar(256) NOT NULL,
    CONSTRAINT UQ_HospitalityPaymentLinkRemovalAudit_Source
      UNIQUE (CompanyId,SiteId,BatchKey,ReservationId,TransactionId),
    CONSTRAINT FK_HospitalityPaymentLinkRemovalAudit_Site
      FOREIGN KEY (CompanyId,SiteId) REFERENCES orion.Site(CompanyId,SiteId),
    CONSTRAINT FK_HospitalityPaymentLinkRemovalAudit_Manifest
      FOREIGN KEY (CompanyId,SiteId,BatchKey,ReservationId,TransactionId)
      REFERENCES orion.HospitalityPaymentLinkRemovalManifest(CompanyId,SiteId,BatchKey,ReservationId,TransactionId)
  );

  INSERT orion.HospitalityPaymentLinkRemovalManifest
  (
    CompanyId,SiteId,BatchNumber,BatchKey,ReservationId,TransactionId,
    ExpectedLinkAmount,ExpectedPaymentRfc,ExpectedPaymentCompanyId,ExpectedPaymentAmount,
    ExpectedAccountingLineCount,ExpectedCfdiLinkCount,ReviewedSetChecksum,
    Evidence,Reason,IsApproved,ApprovedAtUtc,ApprovedBy,CreatedBy
  )
  SELECT
    @OhmCompanyId,@OhmSiteId,BatchNumber,BatchKey,ReservationID,TransaccionID,
    LinkAmount,PaymentRfc,PaymentCompanyId,PaymentAmount,AccountingLineCount,CfdiLinkCount,
    @ReviewedSetChecksum,
    N'Confirmacion empresarial del 2026-09-09: las 288 transacciones pertenecen a BSU210121M77 y no deben relacionarse con OHM191112Q26.',
    N'Vinculo cruzado erroneo; se conserva integra la transaccion BSU y se elimina solo la relacion con la reservacion OHM.',
    1,CONVERT(datetime2(3),'2026-09-09T00:00:00.000'),
    N'Decision empresarial proporcionada por el usuario el 2026-09-09',
    COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
  FROM #ApprovedLinks;
  IF @@ROWCOUNT<>@ExpectedRows THROW 52321,'No se creo el manifiesto completo de 288 filas.',1;

  EXEC(N'CREATE TRIGGER orion.TR_HospitalityPaymentLinkRemovalManifest_ApprovedImmutable
    ON orion.HospitalityPaymentLinkRemovalManifest AFTER UPDATE,DELETE AS
  BEGIN
    SET NOCOUNT ON;
    IF EXISTS (SELECT 1 FROM deleted WHERE IsApproved=1)
      THROW 52322,''No se puede editar ni eliminar evidencia aprobada del corrector E7.'',1;
  END;');
  EXEC(N'CREATE TRIGGER orion.TR_HospitalityPaymentLinkRemovalAudit_Immutable
    ON orion.HospitalityPaymentLinkRemovalAudit AFTER UPDATE,DELETE AS
  BEGIN
    SET NOCOUNT ON;
    THROW 52323,''La auditoria del corrector E7 es inmutable.'',1;
  END;');

  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalManifest,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalManifest AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalManifest AFTER UPDATE,
    ADD FILTER PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalAudit,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalAudit AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityScopePredicate(CompanyId,SiteId) ON orion.HospitalityPaymentLinkRemovalAudit AFTER UPDATE;');

  SELECT
    manifest.BatchNumber,
    manifest.BatchKey,
    COUNT(*) RowsInBatch,
    CONVERT(decimal(19,2),SUM(CONVERT(decimal(19,2),manifest.ExpectedLinkAmount))) LinkAmount,
    CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(nvarchar(max),
      STRING_AGG(CONVERT(nvarchar(max),CONCAT(
        manifest.CompanyId,N'|',manifest.SiteId,N'|',manifest.ReservationId,N'|',manifest.TransactionId,N'|',
        CONVERT(decimal(19,4),manifest.ExpectedLinkAmount),N'|',manifest.ExpectedPaymentRfc,N'|',
        manifest.ExpectedPaymentCompanyId,N'|',CONVERT(decimal(19,4),manifest.ExpectedPaymentAmount),N'|',
        manifest.ExpectedAccountingLineCount,N'|',manifest.ExpectedCfdiLinkCount,N'|',manifest.ReviewedSetChecksum,N'|',
        manifest.Evidence,N'|',manifest.Reason)),NCHAR(10))
        WITHIN GROUP (ORDER BY manifest.ReservationId,manifest.TransactionId))),2) PreviewChecksum
  INTO #BatchPreview
  FROM orion.HospitalityPaymentLinkRemovalManifest manifest
  GROUP BY manifest.BatchNumber,manifest.BatchKey;

  IF (SELECT COUNT(*) FROM #BatchPreview)<>12
     OR EXISTS (SELECT 1 FROM #BatchPreview WHERE RowsInBatch NOT BETWEEN 1 AND 25)
    THROW 52324,'El preview no contiene los doce lotes controlados.',1;

  SELECT N'PREVIEW_VALIDADO' Estado,BatchNumber,BatchKey,RowsInBatch,LinkAmount,PreviewChecksum
  FROM #BatchPreview ORDER BY BatchNumber;

  IF @ApplyChanges=1
  BEGIN
    DECLARE @BatchNumber smallint,@BatchKey uniqueidentifier,@BatchRows int,@PreviewChecksum char(64);
    DECLARE batch_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT BatchNumber,BatchKey,RowsInBatch,PreviewChecksum FROM #BatchPreview ORDER BY BatchNumber;
    OPEN batch_cursor;
    FETCH NEXT FROM batch_cursor INTO @BatchNumber,@BatchKey,@BatchRows,@PreviewChecksum;
    WHILE @@FETCH_STATUS=0
    BEGIN
      DELETE link
      FROM dbo.Reservation_Transacciones link
      JOIN orion.HospitalityPaymentLinkRemovalManifest manifest
        ON manifest.CompanyId=@OhmCompanyId AND manifest.SiteId=@OhmSiteId AND manifest.BatchKey=@BatchKey
       AND manifest.ReservationId=link.ReservationID AND manifest.TransactionId=link.TransaccionID
      WHERE link.OrionCompanyId=@OhmCompanyId AND link.OrionSiteId=@OhmSiteId;
      IF @@ROWCOUNT<>@BatchRows THROW 52325,'No se eliminaron exactamente las relaciones aprobadas del lote.',1;

      INSERT orion.HospitalityPaymentLinkRemovalAudit
      (
        CompanyId,SiteId,BatchNumber,BatchKey,ReservationId,TransactionId,
        RemovedLinkAmount,PaymentRfc,PaymentCompanyId,PaymentAmount,
        AccountingLineCount,CfdiLinkCount,ReviewedSetChecksum,PreviewChecksum,
        Evidence,Reason,AppliedBy
      )
      SELECT
        CompanyId,SiteId,BatchNumber,BatchKey,ReservationId,TransactionId,
        ExpectedLinkAmount,ExpectedPaymentRfc,ExpectedPaymentCompanyId,ExpectedPaymentAmount,
        ExpectedAccountingLineCount,ExpectedCfdiLinkCount,ReviewedSetChecksum,@PreviewChecksum,
        Evidence,Reason,
        COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN()))
      FROM orion.HospitalityPaymentLinkRemovalManifest
      WHERE CompanyId=@OhmCompanyId AND SiteId=@OhmSiteId AND BatchKey=@BatchKey;
      IF @@ROWCOUNT<>@BatchRows THROW 52326,'No se auditaron exactamente las relaciones eliminadas del lote.',1;

      FETCH NEXT FROM batch_cursor INTO @BatchNumber,@BatchKey,@BatchRows,@PreviewChecksum;
    END;
    CLOSE batch_cursor;
    DEALLOCATE batch_cursor;

    IF EXISTS
    (
      SELECT 1
      FROM dbo.Reservation_Transacciones link
      JOIN orion.HospitalityPaymentLinkRemovalManifest manifest
        ON manifest.CompanyId=@OhmCompanyId AND manifest.SiteId=@OhmSiteId
       AND manifest.ReservationId=link.ReservationID AND manifest.TransactionId=link.TransaccionID
    )
      THROW 52327,'Persistio al menos uno de los vinculos aprobados para eliminacion.',1;
    IF (SELECT COUNT(*) FROM orion.HospitalityPaymentLinkRemovalAudit)<>@ExpectedRows
      THROW 52328,'La auditoria no contiene las 288 eliminaciones.',1;
    IF EXISTS
    (
      SELECT 1
      FROM orion.HospitalityPaymentLinkRemovalManifest manifest
      LEFT JOIN dbo.Transacciones payment
        ON payment.ID=manifest.TransactionId AND payment.RFC=manifest.ExpectedPaymentRfc
       AND payment.CompanyId=manifest.ExpectedPaymentCompanyId AND payment.Monto=manifest.ExpectedPaymentAmount
      OUTER APPLY
      (
        SELECT COUNT_BIG(*) AccountingLineCount
        FROM dbo.Registro_Contable accountingLine
        WHERE accountingLine.TransaccionID=manifest.TransactionId
      ) accountingRows
      OUTER APPLY
      (
        SELECT COUNT_BIG(*) CfdiLinkCount
        FROM dbo.Transaccion_Comprobante cfdiLink
        WHERE cfdiLink.Transaccion_ID=manifest.TransactionId
      ) cfdiRows
      WHERE payment.ID IS NULL
         OR accountingRows.AccountingLineCount<>manifest.ExpectedAccountingLineCount
         OR cfdiRows.CfdiLinkCount<>manifest.ExpectedCfdiLinkCount
    )
      THROW 52329,'Una transaccion BSU, sus movimientos contables o sus CFDI cambiaron durante la correccion.',1;
  END;

  /* Restore the strong payment predicate before either commit or dry-run rollback. */
  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    DROP FILTER PREDICATE ON dbo.Reservation_Transacciones,
    DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER INSERT,
    DROP BLOCK PREDICATE ON dbo.Reservation_Transacciones AFTER UPDATE;');
  EXEC(N'ALTER SECURITY POLICY orion.HospitalityScopePolicy
    ADD FILTER PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones,
    ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER INSERT,
    ADD BLOCK PREDICATE orion.fn_HospitalityPaymentScopePredicate(OrionCompanyId,OrionSiteId,TransaccionID) ON dbo.Reservation_Transacciones AFTER UPDATE;');
  IF
  (
    SELECT COUNT(*) FROM sys.security_predicates
    WHERE object_id=OBJECT_ID(N'orion.HospitalityScopePolicy')
      AND target_object_id=OBJECT_ID(N'dbo.Reservation_Transacciones')
      AND predicate_definition LIKE N'%fn_HospitalityPaymentScopePredicate%'
  )<>3
    THROW 52330,'No se restauro el predicado fuerte de pagos.',1;

  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@PreviousHospitalityCompany,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@PreviousHospitalitySite,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=@PreviousHospitalityRfc,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@PreviousAccountingCompany,@read_only=0;
  EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@PreviousAccountingRfc,@read_only=0;

  IF @ApplyChanges=1
  BEGIN
    INSERT orion.SchemaMigration (MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
    VALUES
    (
      @MigrationId,@MigrationChecksum,
      COALESCE(CONVERT(nvarchar(256),SESSION_CONTEXT(N'OrionERP.UserName')),CONVERT(nvarchar(256),ORIGINAL_LOGIN())),
      @AppVersion,DB_NAME()
    );
    COMMIT TRANSACTION;
    SELECT N'APLICADO' Estado,DB_NAME() BaseDatos,@ExpectedRows VinculosEliminados,
      @ExpectedRows TransaccionesBsuConservadas,@MigrationId MigrationId;
  END
  ELSE
  BEGIN
    ROLLBACK TRANSACTION;
    SELECT N'VALIDADO_SIN_CAMBIOS' Estado,DB_NAME() BaseDatos,@ExpectedRows VinculosRevisados,
      @ExpectedRows TransaccionesBsuSinCambios,@MigrationId MigrationId;
  END;
END TRY
BEGIN CATCH
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  BEGIN TRY
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityCompanyId',@value=@PreviousHospitalityCompany,@read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalitySiteId',@value=@PreviousHospitalitySite,@read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.HospitalityRfc',@value=@PreviousHospitalityRfc,@read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId',@value=@PreviousAccountingCompany,@read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionRfc',@value=@PreviousAccountingRfc,@read_only=0;
  END TRY
  BEGIN CATCH
    /* The connection is disposed by the runner if context restoration itself fails. */
  END CATCH;
  THROW;
END CATCH;
