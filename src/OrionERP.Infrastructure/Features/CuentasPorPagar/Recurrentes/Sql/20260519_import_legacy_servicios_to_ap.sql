SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

IF OBJECT_ID('AP.RecurringPayable', 'U') IS NULL
BEGIN
  THROW 51000, 'Run 20260519_recurrent_ap_v1.sql before importing legacy servicios.', 1;
END;
GO

IF OBJECT_ID('AP.OccurrenceAttachment', 'U') IS NULL
BEGIN
  THROW 51000, 'Run 20260519_recurrent_ap_v1.sql before importing legacy servicios.', 1;
END;
GO

IF OBJECT_ID('dbo.Servicios', 'U') IS NULL
BEGIN
  THROW 51001, 'Legacy table dbo.Servicios was not found.', 1;
END;
GO

IF OBJECT_ID('dbo.SERVICIOS_ATTACHMENT', 'U') IS NULL
BEGIN
  THROW 51001, 'Legacy table dbo.SERVICIOS_ATTACHMENT was not found.', 1;
END;
GO

IF COL_LENGTH('AP.RecurringPayable', 'LegacyServicioId') IS NULL
BEGIN
  ALTER TABLE AP.RecurringPayable
    ADD LegacyServicioId int NULL;
END;
GO

IF NOT EXISTS (
  SELECT 1
  FROM sys.indexes
  WHERE object_id = OBJECT_ID('AP.RecurringPayable')
    AND name = 'UX_AP_RecurringPayable_LegacyServicioId'
)
BEGIN
  CREATE UNIQUE INDEX UX_AP_RecurringPayable_LegacyServicioId
    ON AP.RecurringPayable (LegacyServicioId)
    WHERE LegacyServicioId IS NOT NULL;
END;
GO

IF COL_LENGTH('AP.RecurringPayable', 'Website') IS NULL
BEGIN
  ALTER TABLE AP.RecurringPayable
    ADD Website nvarchar(500) NULL;
END;
GO

IF COL_LENGTH('AP.RecurringPayable', 'UserName') IS NULL
BEGIN
  ALTER TABLE AP.RecurringPayable
    ADD UserName nvarchar(200) NULL;
END;
GO

IF COL_LENGTH('AP.RecurringPayable', 'PasswordEnc') IS NULL
BEGIN
  ALTER TABLE AP.RecurringPayable
    ADD PasswordEnc varbinary(max) NULL;
END;
GO

IF COL_LENGTH('AP.OccurrenceAttachment', 'LegacyServiciosAttachmentId') IS NULL
BEGIN
  ALTER TABLE AP.OccurrenceAttachment
    ADD LegacyServiciosAttachmentId int NULL;
END;
GO

IF NOT EXISTS (
  SELECT 1
  FROM sys.indexes
  WHERE object_id = OBJECT_ID('AP.OccurrenceAttachment')
    AND name = 'UX_AP_OccurrenceAttachment_LegacyServiciosAttachmentId'
)
BEGIN
  CREATE UNIQUE INDEX UX_AP_OccurrenceAttachment_LegacyServiciosAttachmentId
    ON AP.OccurrenceAttachment (LegacyServiciosAttachmentId)
    WHERE LegacyServiciosAttachmentId IS NOT NULL;
END;
GO

SET XACT_ABORT ON;

DECLARE @Today date = CONVERT(date, SYSUTCDATETIME());
DECLARE @ThroughDate date = DATEADD(MONTH, 18, @Today);
DECLARE @PayablesInserted int = 0;
DECLARE @OccurrencesInserted int = 0;
DECLARE @PaymentsInserted int = 0;
DECLARE @AttachmentsInserted int = 0;
DECLARE @OccurrencesRecalculated int = 0;
DECLARE @AuditRowsInserted int = 0;
DECLARE @PayablePortalFieldsUpdated int = 0;
DECLARE @LegacyServiciosTotal bigint = 0;
DECLARE @PayablesLinkedToLegacy bigint = 0;
DECLARE @CandidateTransactions bigint = 0;
DECLARE @AlreadyLinkedTransactions bigint = 0;
DECLARE @UnmatchedTransactions bigint = 0;
DECLARE @TransactionsWithoutOccurrence bigint = 0;
DECLARE @LegacyAttachmentsTotal bigint = 0;
DECLARE @AttachmentRowsWithoutOccurrence bigint = 0;

SELECT @LegacyServiciosTotal = COUNT_BIG(*)
FROM dbo.Servicios;

SELECT @LegacyAttachmentsTotal = COUNT_BIG(*)
FROM dbo.SERVICIOS_ATTACHMENT;

BEGIN TRY
  BEGIN TRANSACTION;

  WITH TransactionMin AS (
      SELECT
          t.ServicioID,
          MIN(CONVERT(date, t.Fecha)) AS FirstTransactionDate
      FROM dbo.Transacciones t
      WHERE t.ServicioID IS NOT NULL
      GROUP BY t.ServicioID
  ),
  Prepared AS (
      SELECT
          s.id AS LegacyServicioId,
          UPPER(LTRIM(RTRIM(s.RFC))) AS Rfc,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Descripcion))), N''), 200) AS [Name],
          bp.Id AS BusinessPartnerId,
          LEFT(COALESCE(
              NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), bp.PartnerName))), N''),
              NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.RazonSocial))), N''),
              NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), p.RazonSocial))), N'')), 200) AS PayeeNameSnapshot,
          LEFT(COALESCE(
              NULLIF(LTRIM(RTRIM(bp.Rfc)), ''),
              NULLIF(LTRIM(RTRIM(p.RFC)), '')), 50) AS PayeeRfcSnapshot,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Categoria))), N''), 80) AS Category,
          CASE
              WHEN s.Periodicidad IN (30, 90) THEN 'Days'
              WHEN s.Periodicidad = 12 THEN 'Years'
              ELSE 'Months'
          END AS FrequencyUnit,
          CASE
              WHEN s.Periodicidad IN (1, 2, 3, 30, 90) THEN s.Periodicidad
              ELSE 1
          END AS IntervalCount,
          COALESCE(legacyDates.StartDate, @Today) AS StartDate,
          CAST(NULL AS date) AS EndDate,
          CASE
              WHEN s.Periodicidad IN (30, 90) THEN NULL
              ELSE dueParts.DueDayOfMonth
          END AS DueDayOfMonth,
          CASE
              WHEN s.Periodicidad = 12 THEN dueParts.DueMonth
              ELSE NULL
          END AS DueMonth,
          CASE
              WHEN s.Monto_Utimo_Pago IS NOT NULL AND s.Monto_Utimo_Pago >= 0
                  THEN CONVERT(decimal(18, 2), s.Monto_Utimo_Pago)
              ELSE NULL
          END AS ExpectedAmount,
          CASE
              WHEN UPPER(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(100), s.[Status]))), N'')) IN
                   (N'INACTIVO', N'INACTIVA', N'CANCELADO', N'CANCELADA', N'BAJA', N'SUSPENDIDO', N'SUSPENDIDA')
              THEN CONVERT(bit, 0)
              ELSE CONVERT(bit, 1)
          END AS IsActive,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web))), N''), 500) AS Website,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Usuario))), N''), 200) AS UserName,
          LEFT(CONCAT_WS(
              CHAR(10),
              CONCAT(N'Importado de dbo.Servicios ID ', CONVERT(nvarchar(20), s.id), N'.'),
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.RazonSocial))), N'') IS NOT NULL
                   THEN CONCAT(N'Razon social legacy: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.RazonSocial)))) END,
              CASE WHEN s.Entidad_Cobro_ID IS NOT NULL
                   THEN CONCAT(N'Entidad_Cobro_ID: ', CONVERT(nvarchar(20), s.Entidad_Cobro_ID)) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Numero_Folio))), N'') IS NOT NULL
                   THEN CONCAT(N'Folio: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Numero_Folio)))) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Numero_Cuenta))), N'') IS NOT NULL
                   THEN CONCAT(N'Cuenta: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Numero_Cuenta)))) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web))), N'') IS NOT NULL
                   THEN CONCAT(N'Pagina web: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web)))) END,
              CASE WHEN s.Pago_Domiciliado IS NOT NULL
                   THEN CONCAT(N'Pago domiciliado: ', CASE WHEN s.Pago_Domiciliado = 1 THEN N'Si' ELSE N'No' END) END,
              CASE WHEN s.Dia_Corte IS NOT NULL
                   THEN CONCAT(N'Dia de corte: ', CONVERT(nvarchar(10), s.Dia_Corte)) END,
              CASE WHEN s.Dia_Pago IS NOT NULL
                   THEN CONCAT(N'Dia de pago: ', CONVERT(nvarchar(10), s.Dia_Pago)) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Cuenta_Domiciliada))), N'') IS NOT NULL
                   THEN CONCAT(N'Cuenta domiciliada: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Cuenta_Domiciliada)))) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Comentarios))), N'') IS NOT NULL
                   THEN CONCAT(N'Comentarios: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Comentarios)))) END,
              CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Claves_de_Acceso))), N'') IS NOT NULL
                   THEN CONCAT(N'Claves de acceso legacy: ', LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Claves_de_Acceso)))) END
          ), 1000) AS [Description]
      FROM dbo.Servicios s
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.LegacyProveedorId = s.Entidad_Cobro_ID
      LEFT JOIN dbo.Proveedores p
        ON p.id = s.Entidad_Cobro_ID
      LEFT JOIN TransactionMin tm
        ON tm.ServicioID = s.id
      OUTER APPLY (
          SELECT MIN(v.SourceDate) AS StartDate
          FROM (VALUES
              (CONVERT(date, s.Inicio_Periodo)),
              (tm.FirstTransactionDate),
              (CONVERT(date, s.Fecha_Ultimo_Pago)),
              (CONVERT(date, s.Fecha_Proximo_Pago)),
              (CONVERT(date, s.Fecha_Vencimiento))
          ) AS v(SourceDate)
          WHERE v.SourceDate IS NOT NULL
            AND v.SourceDate >= CONVERT(date, '20000101', 112)
      ) legacyDates
      CROSS APPLY (
          SELECT
              CASE
                  WHEN COALESCE(s.Dia_Pago, DAY(s.Fecha_Proximo_Pago), DAY(s.Fecha_Vencimiento), DAY(s.Fecha_Ultimo_Pago), DAY(s.Inicio_Periodo), 1) < 1 THEN 1
                  WHEN COALESCE(s.Dia_Pago, DAY(s.Fecha_Proximo_Pago), DAY(s.Fecha_Vencimiento), DAY(s.Fecha_Ultimo_Pago), DAY(s.Inicio_Periodo), 1) > 31 THEN 31
                  ELSE COALESCE(s.Dia_Pago, DAY(s.Fecha_Proximo_Pago), DAY(s.Fecha_Vencimiento), DAY(s.Fecha_Ultimo_Pago), DAY(s.Inicio_Periodo), 1)
              END AS DueDayOfMonth,
              CASE
                  WHEN COALESCE(MONTH(s.Fecha_Proximo_Pago), MONTH(s.Fecha_Vencimiento), MONTH(s.Fecha_Ultimo_Pago), MONTH(s.Inicio_Periodo), 1) < 1 THEN 1
                  WHEN COALESCE(MONTH(s.Fecha_Proximo_Pago), MONTH(s.Fecha_Vencimiento), MONTH(s.Fecha_Ultimo_Pago), MONTH(s.Inicio_Periodo), 1) > 12 THEN 12
                  ELSE COALESCE(MONTH(s.Fecha_Proximo_Pago), MONTH(s.Fecha_Vencimiento), MONTH(s.Fecha_Ultimo_Pago), MONTH(s.Inicio_Periodo), 1)
              END AS DueMonth
      ) dueParts
  )
  INSERT INTO AP.RecurringPayable
  (
      Rfc,
      [Name],
      BusinessPartnerId,
      PayeeNameSnapshot,
      PayeeRfcSnapshot,
      Category,
      [Description],
      Website,
      UserName,
      FrequencyUnit,
      IntervalCount,
      StartDate,
      EndDate,
      DueDayOfMonth,
      DueMonth,
      ExpectedAmount,
      Currency,
      IsActive,
      CreatedBy,
      LegacyServicioId
  )
  SELECT
      p.Rfc,
      p.[Name],
      p.BusinessPartnerId,
      p.PayeeNameSnapshot,
      p.PayeeRfcSnapshot,
      p.Category,
      p.[Description],
      p.Website,
      p.UserName,
      p.FrequencyUnit,
      p.IntervalCount,
      p.StartDate,
      p.EndDate,
      p.DueDayOfMonth,
      p.DueMonth,
      p.ExpectedAmount,
      'MXN',
      p.IsActive,
      N'LegacyServiciosImport',
      p.LegacyServicioId
  FROM Prepared p
  WHERE p.Rfc IS NOT NULL
    AND p.Rfc <> ''
    AND p.[Name] IS NOT NULL
    AND p.[Name] <> ''
    AND NOT EXISTS (
        SELECT 1
        FROM AP.RecurringPayable existing
        WHERE existing.LegacyServicioId = p.LegacyServicioId
    );

  SET @PayablesInserted = @@ROWCOUNT;

  WITH PreparedPortalFields AS (
      SELECT
          s.id AS LegacyServicioId,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Pagina_Web))), N''), 500) AS Website,
          LEFT(NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), s.Usuario))), N''), 200) AS UserName
      FROM dbo.Servicios s
  )
  UPDATE rp
  SET
      Website = COALESCE(NULLIF(LTRIM(RTRIM(rp.Website)), N''), p.Website),
      UserName = COALESCE(NULLIF(LTRIM(RTRIM(rp.UserName)), N''), p.UserName),
      UpdatedAt = SYSUTCDATETIME(),
      UpdatedBy = N'LegacyServiciosImport'
  FROM AP.RecurringPayable rp
  JOIN PreparedPortalFields p
    ON p.LegacyServicioId = rp.LegacyServicioId
  WHERE (NULLIF(LTRIM(RTRIM(rp.Website)), N'') IS NULL AND p.Website IS NOT NULL)
     OR (NULLIF(LTRIM(RTRIM(rp.UserName)), N'') IS NULL AND p.UserName IS NOT NULL);

  SET @PayablePortalFieldsUpdated = @@ROWCOUNT;

  WITH Payables AS (
      SELECT
          rp.Id,
          rp.Rfc,
          rp.StartDate,
          rp.EndDate,
          rp.FrequencyUnit,
          rp.IntervalCount,
          rp.DueDayOfMonth,
          rp.DueMonth,
          rp.ExpectedAmount,
          CASE
              WHEN rp.EndDate IS NOT NULL AND rp.EndDate < @ThroughDate THEN rp.EndDate
              ELSE @ThroughDate
          END AS MaxDate
      FROM AP.RecurringPayable rp
      WHERE rp.LegacyServicioId IS NOT NULL
        AND rp.StartDate <= COALESCE(rp.EndDate, @ThroughDate)
  ),
  Series AS (
      SELECT
          p.Id,
          p.Rfc,
          p.StartDate,
          p.MaxDate,
          p.FrequencyUnit,
          p.IntervalCount,
          p.DueDayOfMonth,
          p.DueMonth,
          p.ExpectedAmount,
          p.StartDate AS PeriodStartDate,
          0 AS StepNumber
      FROM Payables p
      WHERE p.StartDate <= p.MaxDate

      UNION ALL

      SELECT
          s.Id,
          s.Rfc,
          s.StartDate,
          s.MaxDate,
          s.FrequencyUnit,
          s.IntervalCount,
          s.DueDayOfMonth,
          s.DueMonth,
          s.ExpectedAmount,
          CASE
              WHEN s.FrequencyUnit = 'Days' THEN DATEADD(DAY, s.IntervalCount, s.PeriodStartDate)
              WHEN s.FrequencyUnit = 'Weeks' THEN DATEADD(DAY, s.IntervalCount * 7, s.PeriodStartDate)
              WHEN s.FrequencyUnit = 'Years' THEN DATEADD(YEAR, s.IntervalCount, s.PeriodStartDate)
              ELSE DATEADD(MONTH, s.IntervalCount, s.PeriodStartDate)
          END AS PeriodStartDate,
          s.StepNumber + 1
      FROM Series s
      WHERE s.PeriodStartDate < s.MaxDate
        AND s.StepNumber < 2000
  ),
  Resolved AS (
      SELECT
          s.Id,
          s.Rfc,
          s.StartDate,
          s.MaxDate,
          s.PeriodStartDate,
          CASE
              WHEN s.FrequencyUnit = 'Years' THEN DATEFROMPARTS(
                  YEAR(s.PeriodStartDate),
                  yearParts.DueMonth,
                  CASE WHEN yearParts.DueDayOfMonth > yearParts.LastDayOfMonth THEN yearParts.LastDayOfMonth ELSE yearParts.DueDayOfMonth END)
              WHEN s.FrequencyUnit = 'Months' THEN DATEFROMPARTS(
                  YEAR(s.PeriodStartDate),
                  MONTH(s.PeriodStartDate),
                  CASE WHEN monthParts.DueDayOfMonth > monthParts.LastDayOfMonth THEN monthParts.LastDayOfMonth ELSE monthParts.DueDayOfMonth END)
              ELSE s.PeriodStartDate
          END AS DueDate,
          s.ExpectedAmount
      FROM Series s
      CROSS APPLY (
          SELECT
              COALESCE(s.DueMonth, MONTH(s.PeriodStartDate)) AS DueMonth,
              COALESCE(s.DueDayOfMonth, DAY(s.PeriodStartDate)) AS DueDayOfMonth,
              DAY(EOMONTH(DATEFROMPARTS(YEAR(s.PeriodStartDate), COALESCE(s.DueMonth, MONTH(s.PeriodStartDate)), 1))) AS LastDayOfMonth
      ) yearParts
      CROSS APPLY (
          SELECT
              COALESCE(s.DueDayOfMonth, DAY(s.PeriodStartDate)) AS DueDayOfMonth,
              DAY(EOMONTH(s.PeriodStartDate)) AS LastDayOfMonth
      ) monthParts
  )
  INSERT INTO AP.PayableOccurrence
  (
      RecurringPayableId,
      Rfc,
      PeriodStartDate,
      DueDate,
      ExpectedAmount
  )
  SELECT
      r.Id,
      r.Rfc,
      r.PeriodStartDate,
      r.DueDate,
      r.ExpectedAmount
  FROM Resolved r
  WHERE r.DueDate >= r.StartDate
    AND r.DueDate <= r.MaxDate
    AND NOT EXISTS (
        SELECT 1
        FROM AP.PayableOccurrence existing
        WHERE existing.RecurringPayableId = r.Id
          AND existing.DueDate = r.DueDate
    )
  OPTION (MAXRECURSION 0);

  SET @OccurrencesInserted = @@ROWCOUNT;

  WITH CandidateTransactions AS (
      SELECT
          t.ID AS TransaccionId,
          rp.Id AS RecurringPayableId,
          rp.Rfc
      FROM dbo.Transacciones t
      JOIN dbo.Servicios s
        ON s.id = t.ServicioID
      JOIN AP.RecurringPayable rp
        ON rp.LegacyServicioId = s.id
       AND rp.Rfc = UPPER(LTRIM(RTRIM(t.RFC)))
      WHERE t.ServicioID IS NOT NULL
        AND UPPER(LTRIM(RTRIM(s.RFC))) = UPPER(LTRIM(RTRIM(t.RFC)))
        AND t.Monto IS NOT NULL
        AND t.Monto <> 0
  )
  SELECT @CandidateTransactions = COUNT_BIG(*)
  FROM CandidateTransactions;

  WITH CandidateTransactions AS (
      SELECT
          t.ID AS TransaccionId,
          rp.Id AS RecurringPayableId,
          rp.Rfc
      FROM dbo.Transacciones t
      JOIN dbo.Servicios s
        ON s.id = t.ServicioID
      JOIN AP.RecurringPayable rp
        ON rp.LegacyServicioId = s.id
       AND rp.Rfc = UPPER(LTRIM(RTRIM(t.RFC)))
      WHERE t.ServicioID IS NOT NULL
        AND UPPER(LTRIM(RTRIM(s.RFC))) = UPPER(LTRIM(RTRIM(t.RFC)))
        AND t.Monto IS NOT NULL
        AND t.Monto <> 0
  )
  SELECT @AlreadyLinkedTransactions = COUNT_BIG(*)
  FROM CandidateTransactions ct
  WHERE EXISTS (
      SELECT 1
      FROM AP.OccurrencePayment existing
      WHERE existing.TransaccionId = ct.TransaccionId
  );

  WITH CandidateTransactions AS (
      SELECT
          t.ID AS TransaccionId,
          rp.Id AS RecurringPayableId,
          rp.Rfc
      FROM dbo.Transacciones t
      JOIN dbo.Servicios s
        ON s.id = t.ServicioID
      JOIN AP.RecurringPayable rp
        ON rp.LegacyServicioId = s.id
       AND rp.Rfc = UPPER(LTRIM(RTRIM(t.RFC)))
      WHERE t.ServicioID IS NOT NULL
        AND UPPER(LTRIM(RTRIM(s.RFC))) = UPPER(LTRIM(RTRIM(t.RFC)))
        AND t.Monto IS NOT NULL
        AND t.Monto <> 0
  )
  SELECT @TransactionsWithoutOccurrence = COUNT_BIG(*)
  FROM CandidateTransactions ct
  WHERE NOT EXISTS (
      SELECT 1
      FROM AP.PayableOccurrence o
      WHERE o.RecurringPayableId = ct.RecurringPayableId
        AND o.Rfc = ct.Rfc
  );

  WITH CandidateTransactions AS (
      SELECT
          t.ID AS TransaccionId,
          rp.Id AS RecurringPayableId,
          rp.Rfc,
          CONVERT(date, t.Fecha) AS PaymentDate,
          ABS(CONVERT(decimal(18, 2), t.Monto)) AS Amount,
          LEFT(CONCAT(N'Importado desde dbo.Transacciones ID ', CONVERT(nvarchar(20), t.ID), N'. ', NULLIF(CONVERT(nvarchar(900), t.Concepto), N'')), 1000) AS Notes
      FROM dbo.Transacciones t
      JOIN dbo.Servicios s
        ON s.id = t.ServicioID
      JOIN AP.RecurringPayable rp
        ON rp.LegacyServicioId = s.id
       AND rp.Rfc = UPPER(LTRIM(RTRIM(t.RFC)))
      WHERE t.ServicioID IS NOT NULL
        AND UPPER(LTRIM(RTRIM(s.RFC))) = UPPER(LTRIM(RTRIM(t.RFC)))
        AND t.Monto IS NOT NULL
        AND t.Monto <> 0
  ),
  LinkableTransactions AS (
      SELECT ct.*
      FROM CandidateTransactions ct
      WHERE NOT EXISTS (
          SELECT 1
          FROM AP.OccurrencePayment existing
          WHERE existing.TransaccionId = ct.TransaccionId
      )
  )
  INSERT INTO AP.OccurrencePayment
  (
      OccurrenceId,
      Rfc,
      TransaccionId,
      Amount,
      PaymentDate,
      Notes,
      CreatedBy
  )
  SELECT
      occurrenceMatch.Id,
      lt.Rfc,
      lt.TransaccionId,
      lt.Amount,
      lt.PaymentDate,
      lt.Notes,
      N'LegacyServiciosImport'
  FROM LinkableTransactions lt
  CROSS APPLY (
      SELECT TOP (1) o.Id
      FROM AP.PayableOccurrence o
      WHERE o.RecurringPayableId = lt.RecurringPayableId
        AND o.Rfc = lt.Rfc
      ORDER BY
          ABS(DATEDIFF(DAY, o.DueDate, lt.PaymentDate)),
          CASE WHEN o.DueDate >= lt.PaymentDate THEN 0 ELSE 1 END,
          o.DueDate,
          o.Id
  ) occurrenceMatch;

  SET @PaymentsInserted = @@ROWCOUNT;

  WITH PaymentTotals AS (
      SELECT
          o.Id AS OccurrenceId,
          o.ExpectedAmount,
          CONVERT(decimal(18, 2), SUM(p.Amount)) AS TotalPaid,
          MAX(p.PaymentDate) AS LastPaymentDate
      FROM AP.PayableOccurrence o
      JOIN AP.RecurringPayable rp
        ON rp.Id = o.RecurringPayableId
      JOIN AP.OccurrencePayment p
        ON p.OccurrenceId = o.Id
      WHERE rp.LegacyServicioId IS NOT NULL
      GROUP BY o.Id, o.ExpectedAmount
  )
  UPDATE o
  SET ActualPaidAmount = pt.TotalPaid,
      PaymentDate = pt.LastPaymentDate,
      [Status] = CASE
          WHEN pt.TotalPaid <= 0 THEN 'Pending'
          WHEN pt.ExpectedAmount IS NULL OR pt.ExpectedAmount <= 0 THEN 'Paid'
          WHEN pt.TotalPaid + 0.005 >= pt.ExpectedAmount THEN 'Paid'
          ELSE 'PartiallyPaid'
      END,
      UpdatedAt = SYSUTCDATETIME(),
      UpdatedBy = N'LegacyServiciosImport'
  FROM AP.PayableOccurrence o
  JOIN PaymentTotals pt
    ON pt.OccurrenceId = o.Id
  WHERE o.ActualPaidAmount <> pt.TotalPaid
     OR ISNULL(o.PaymentDate, CONVERT(date, '19000101', 112)) <> pt.LastPaymentDate
     OR o.[Status] <> CASE
          WHEN pt.TotalPaid <= 0 THEN 'Pending'
          WHEN pt.ExpectedAmount IS NULL OR pt.ExpectedAmount <= 0 THEN 'Paid'
          WHEN pt.TotalPaid + 0.005 >= pt.ExpectedAmount THEN 'Paid'
          ELSE 'PartiallyPaid'
      END;

  SET @OccurrencesRecalculated = @@ROWCOUNT;

  WITH SourceAttachments AS (
      SELECT
          a.ID AS LegacyServiciosAttachmentId,
          rp.Id AS RecurringPayableId,
          rp.Rfc,
          a.ServiciosID,
          a.Attachment,
          NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), a.AttachmentName))), N'') AS BaseFileName,
          NULLIF(REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(4000), a.AttachmentExtension))), N'.', N''), N'') AS CleanExtension
      FROM dbo.SERVICIOS_ATTACHMENT a
      JOIN AP.RecurringPayable rp
        ON rp.LegacyServicioId = a.ServiciosID
      WHERE DATALENGTH(a.Attachment) > 0
        AND NOT EXISTS (
            SELECT 1
            FROM AP.OccurrenceAttachment existing
            WHERE existing.LegacyServiciosAttachmentId = a.ID
        )
  ),
  PreparedAttachments AS (
      SELECT
          sa.LegacyServiciosAttachmentId,
          sa.Rfc,
          sa.Attachment,
          sa.CleanExtension,
          LEFT(
              COALESCE(sa.BaseFileName, CONCAT(N'servicio-', CONVERT(nvarchar(20), sa.ServiciosID), N'-attachment-', CONVERT(nvarchar(20), sa.LegacyServiciosAttachmentId))) +
              CASE
                  WHEN sa.CleanExtension IS NULL THEN N''
                  WHEN RIGHT(COALESCE(sa.BaseFileName, N''), LEN(sa.CleanExtension) + 1) = CONCAT(N'.', sa.CleanExtension) THEN N''
                  ELSE CONCAT(N'.', sa.CleanExtension)
              END,
              260) AS FileName,
          occurrenceMatch.Id AS OccurrenceId
      FROM SourceAttachments sa
      CROSS APPLY (
          SELECT TOP (1) o.Id
          FROM AP.PayableOccurrence o
          WHERE o.RecurringPayableId = sa.RecurringPayableId
            AND o.Rfc = sa.Rfc
          ORDER BY
              CASE WHEN o.DueDate >= @Today THEN 0 ELSE 1 END,
              CASE WHEN o.DueDate >= @Today THEN o.DueDate END ASC,
              o.DueDate DESC,
              o.Id DESC
      ) occurrenceMatch
  )
  INSERT INTO AP.OccurrenceAttachment
  (
      OccurrenceId,
      Rfc,
      FileName,
      ContentType,
      Content,
      SizeBytes,
      UploadedBy,
      LegacyServiciosAttachmentId
  )
  SELECT
      pa.OccurrenceId,
      pa.Rfc,
      pa.FileName,
      CASE LOWER(pa.CleanExtension)
          WHEN 'pdf' THEN 'application/pdf'
          WHEN 'xml' THEN 'application/xml'
          WHEN 'jpg' THEN 'image/jpeg'
          WHEN 'jpeg' THEN 'image/jpeg'
          WHEN 'png' THEN 'image/png'
          WHEN 'txt' THEN 'text/plain'
          ELSE 'application/octet-stream'
      END,
      pa.Attachment,
      DATALENGTH(pa.Attachment),
      N'LegacyServiciosImport',
      pa.LegacyServiciosAttachmentId
  FROM PreparedAttachments pa;

  SET @AttachmentsInserted = @@ROWCOUNT;

  INSERT INTO AP.AuditLog
  (
      Rfc,
      EntityType,
      EntityId,
      EventName,
      Detail,
      CreatedBy
  )
  SELECT
      rp.Rfc,
      'RecurringPayable',
      rp.Id,
      'LegacyServicioImported',
      CONCAT(N'dbo.Servicios ID ', CONVERT(nvarchar(20), rp.LegacyServicioId)),
      N'LegacyServiciosImport'
  FROM AP.RecurringPayable rp
  WHERE rp.LegacyServicioId IS NOT NULL
    AND NOT EXISTS (
        SELECT 1
        FROM AP.AuditLog existing
        WHERE existing.EntityType = 'RecurringPayable'
          AND existing.EntityId = rp.Id
          AND existing.EventName = 'LegacyServicioImported'
    );

  SET @AuditRowsInserted = @@ROWCOUNT;

  COMMIT TRANSACTION;
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0
  BEGIN
    ROLLBACK TRANSACTION;
  END;

  THROW;
END CATCH;

SELECT @PayablesLinkedToLegacy = COUNT_BIG(*)
FROM AP.RecurringPayable
WHERE LegacyServicioId IS NOT NULL;

SELECT @UnmatchedTransactions = COUNT_BIG(*)
FROM dbo.Transacciones t
WHERE t.ServicioID IS NOT NULL
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Servicios s
      WHERE s.id = t.ServicioID
        AND UPPER(LTRIM(RTRIM(s.RFC))) = UPPER(LTRIM(RTRIM(t.RFC)))
  );

SELECT @AttachmentRowsWithoutOccurrence = COUNT_BIG(*)
FROM dbo.SERVICIOS_ATTACHMENT a
JOIN AP.RecurringPayable rp
  ON rp.LegacyServicioId = a.ServiciosID
WHERE NOT EXISTS (
    SELECT 1
    FROM AP.PayableOccurrence o
    WHERE o.RecurringPayableId = rp.Id
      AND o.Rfc = rp.Rfc
);

SELECT Metric, [Value]
FROM (VALUES
    (N'LegacyServiciosTotal', @LegacyServiciosTotal),
    (N'RecurringPayablesInserted', CONVERT(bigint, @PayablesInserted)),
    (N'RecurringPayablesLinkedToLegacy', @PayablesLinkedToLegacy),
    (N'RecurringPayablePortalFieldsUpdated', CONVERT(bigint, @PayablePortalFieldsUpdated)),
    (N'OccurrencesInserted', CONVERT(bigint, @OccurrencesInserted)),
    (N'CandidateTransactionLinks', @CandidateTransactions),
    (N'AlreadyLinkedTransactions', @AlreadyLinkedTransactions),
    (N'TransactionLinksInserted', CONVERT(bigint, @PaymentsInserted)),
    (N'UnmatchedTransactions', @UnmatchedTransactions),
    (N'TransactionsWithoutOccurrence', @TransactionsWithoutOccurrence),
    (N'OccurrencesRecalculated', CONVERT(bigint, @OccurrencesRecalculated)),
    (N'LegacyAttachmentsTotal', @LegacyAttachmentsTotal),
    (N'OccurrenceAttachmentsInserted', CONVERT(bigint, @AttachmentsInserted)),
    (N'AttachmentRowsWithoutOccurrence', @AttachmentRowsWithoutOccurrence),
    (N'AuditRowsInserted', CONVERT(bigint, @AuditRowsInserted))
) AS summary(Metric, [Value])
ORDER BY Metric;
