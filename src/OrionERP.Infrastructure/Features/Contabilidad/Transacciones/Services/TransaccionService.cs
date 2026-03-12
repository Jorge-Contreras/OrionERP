using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;

public sealed class TransaccionService : ITransaccionService
{
  private const int DefaultPublicoTemplateComprobanteId = 21539;
  private const string DefaultPublicoResetMes = "01";
  private const int DefaultPublicoResetAnio = 1982;

  private readonly IConfiguration _cfg;
  private readonly string _cs;
  private readonly IDbStoredProcService _storedProcService;
  private readonly IFacturamaApiClient _facturamaApiClient;
  private readonly ILogger<TransaccionService> _logger;

  public TransaccionService(
      IConfiguration cfg,
      IDbStoredProcService storedProcService,
      IFacturamaApiClient facturamaApiClient,
      ILogger<TransaccionService> logger)
  {
    _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    _cs = _cfg.GetConnectionString("OrionDb")
         ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _storedProcService = storedProcService ?? throw new ArgumentNullException(nameof(storedProcService));
    _facturamaApiClient = facturamaApiClient ?? throw new ArgumentNullException(nameof(facturamaApiClient));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<TransaccionHeaderDto?> GetHeaderAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (1)
    t.ID                AS Id,
    t.Concepto          AS Concepto,
    t.Fecha             AS Fecha,
    CAST(t.Monto AS decimal(18,4)) AS Monto,
    t.Cuenta            AS Cuenta,
    t.RFC               AS Rfc,
    t.Categoria         AS Categoria,
    t.Facturado         AS Facturado,
    t.Referencia        AS Referencia,
    t.Memo              AS Memo,
    t.ProyectoID        AS ProyectoId,
    t.CompraID          AS CompraId,
    t.ServicioID        AS ServicioId,
    t.NominaID          AS NominaId,
    t.Tipo_Poliza       AS TipoPoliza,
    t.Forma_Pago        AS FormaPago,
    tc.Comprobante_ID   AS ComprobanteId,
    CAST(tc.Monto AS decimal(18,4)) AS ComprobanteMonto
FROM dbo.Transacciones t
LEFT JOIN dbo.Transaccion_Comprobante tc
  ON tc.Transaccion_ID = t.ID
WHERE t.ID = @TransaccionId;";

    using var conn = new SqlConnection(_cs);
    return await conn.QueryFirstOrDefaultAsync<TransaccionHeaderDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<IReadOnlyList<TransaccionListItem>> GetCandidatesAsync(
      DateTime fechaXml,
      decimal montoAbs,
      string rfc,
      int daysBack = 60,
      int top = 200,
      CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (@Top)
    t.ID                                    AS Id,
    t.Concepto                              AS Concepto,
    t.Fecha                                 AS Fecha,
    ABS(CONVERT(decimal(18,4), t.Monto))    AS Monto1,
    t.Cuenta                                AS Cuenta,
    COUNT(ta.ID)                            AS Adjuntos,
    c.Comprobante_Id                        AS ComprobanteId
FROM dbo.Transacciones t
LEFT JOIN dbo.TRANSACTION_ATTACHMENT ta
       ON ta.TranID = t.ID
LEFT JOIN dbo.Transaccion_Comprobante tc
       ON tc.Transaccion_ID = t.ID
LEFT JOIN cfdi.Comprobante c
       ON c.Comprobante_Id = tc.Comprobante_ID
WHERE t.Fecha > DATEADD(DAY, -@DaysBack, @FechaXml)
  AND ABS(CONVERT(decimal(18,4), t.Monto)) = @MontoAbs
  AND t.RFC = @Rfc
GROUP BY t.ID, t.Concepto, t.Fecha, t.Monto, t.Cuenta, c.Comprobante_Id
ORDER BY t.Fecha;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionListItem>(
        new CommandDefinition(
            sql,
            new
            {
              FechaXml = fechaXml,
              DaysBack = daysBack,
              MontoAbs = montoAbs,
              Top = top,
              Rfc = rfc
            },
            commandType: CommandType.Text,
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionCfdiCandidateDto>> GetCfdiCandidatesAsync(
      TransaccionCfdiSearchRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var parameters = new DynamicParameters();
    parameters.Add("@Monto", request.Monto);
    parameters.Add("@Concepto", request.Concepto);
    parameters.Add("@Rfc", request.Rfc);
    parameters.Add("@Comprobante_ID", request.ComprobanteId);
    parameters.Add("@Renglones", request.Renglones);
    parameters.Add("@Tipo", request.Tipo);
    parameters.Add("@Comprobantes_In", string.IsNullOrWhiteSpace(request.ComprobantesCsv) ? null : request.ComprobantesCsv);

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<CfdiCandidateRow>(
        new CommandDefinition(
            "cfdi.CFDIs_Candidatos_Para_Poliza",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    return rows
        .Select(row => new TransaccionCfdiCandidateDto
        {
          ComprobanteId = row.Comprobante_Id,
          Fecha = row.Fecha,
          Tipo = row.Tipo,
          Serie = row.Serie,
          Folio = row.Folio,
          EmisorRfc = row.Emisor_Rfc,
          ReceptorRfc = row.Receptor_Rfc,
          Uuid = row.UUID,
          FormaPago = row.FormaPago,
          Total = row.Total,
          Polizas = row.Polizas,
          Asignado = row.Asignado,
          MetodoPago = row.MetodoPago,
          UsoCfdi = row.UsoCFDI,
          Conceptos = row.Conceptos,
          XmlAttachmentId = row.XML_Attachment_ID
        })
        .ToList();
  }

  public async Task<IReadOnlyList<long>> GetLinkedCfdiIdsAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT CAST(TC.Comprobante_ID AS bigint) AS Id
FROM dbo.Transaccion_Comprobante AS TC
WHERE TC.Transaccion_ID = @Id
UNION
SELECT CAST(TD.DoctoRelacionado_Id AS bigint) AS Id
FROM dbo.Transaccion_DoctoRelacionado AS TD
WHERE TD.Transaccion_ID = @Id;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<long>(
        new CommandDefinition(sql, new { Id = transaccionId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCommandResult> LinkCfdiAsync(
      TransaccionCfdiLinkRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);

    try
    {
      await conn.ExecuteAsync(
          new CommandDefinition(
              "contabilidad.Ligar_CFDI_Poliza",
              new
              {
                TransaccionId = request.TransaccionId,
                ComprobanteId = request.ComprobanteId,
                Monto = request.Monto,
                UseDoctoRelacionadoTable = request.UseDoctoRelacionadoTable
              },
              commandType: CommandType.StoredProcedure,
              cancellationToken: ct));

      return TransaccionCommandResult.Ok("Transacción ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
    {
      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to link CFDI {ComprobanteId} to transaction {TransaccionId}",
          request.ComprobanteId,
          request.TransaccionId);

      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
  }

  public async Task<IReadOnlyList<TransaccionMovimientoDto>> GetMovimientosAsync(int transaccionId, CancellationToken ct = default)
  {
      const string sql = @"SELECT
    rc.ID                 AS Id,
    rc.TransaccionID     AS TransaccionId,
    rc.Nivel1,
    rc.Nivel2,
    rc.Nivel3,
    rc.Nombre_Cuenta      AS NombreCuenta,
    rc.Concepto           AS Concepto,
    CAST(ISNULL(rc.Debe, 0) AS decimal(18,4))  AS Debe,
    CAST(ISNULL(rc.Haber, 0) AS decimal(18,4)) AS Haber
FROM dbo.Registro_Contable rc
WHERE rc.TransaccionID = @TransaccionId
ORDER BY rc.ID;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionMovimientoDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetCategoriasAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    c.ID           AS Id,
    c.Descripcion  AS Description
FROM dbo.Categorias c
WHERE c.GrupoCategoria = 'PLANTILLA'
  AND c.RFC = @Rfc
ORDER BY c.Descripcion ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetActividadesAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    a.ID          AS Id,
    a.Descripcion AS Description
FROM dbo.Actividad a
WHERE a.RFC = @Rfc
ORDER BY a.Descripcion ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetComprasAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    c.ID          AS Id,
    c.Descripcion AS Description
FROM dbo.Compra c
WHERE c.RFC = @Rfc
ORDER BY c.Descripcion ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetServiciosAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    s.ID          AS Id,
    s.Descripcion AS Description
FROM dbo.Servicios s
WHERE s.RFC = @Rfc
ORDER BY s.Descripcion ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetNominasAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    ch.ID          AS Id,
    ch.NombreCorto AS Description
FROM dbo.Capital_Humano ch
WHERE ch.RFC = @Rfc 
      AND ch.Status='ACTIVO'
ORDER BY ch.NombreCorto ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<FormaPagoLookupDto>> GetFormasPagoAsync(CancellationToken ct = default)
  {
    const string sql = @"SELECT
    fp.Clave        AS Clave,
    fp.Descripcion  AS Descripcion
FROM dbo.Formas_Pago fp
ORDER BY fp.Clave ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<FormaPagoLookupDto>(
        new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<MovimientoTotalsDto> GetMovimientoTotalsAsync(int transaccionId, CancellationToken ct = default)
  {
    using var conn = new SqlConnection(_cs);
    return await LoadTotalsAsync(conn, transaction: null, transaccionId, ct);
  }

  public async Task<IReadOnlyList<TransaccionAttachmentDto>> GetAttachmentsAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    ta.ID                    AS Id,
    ta.TranID                AS TransaccionId,
    ta.AttachmentName        AS AttachmentName,
    ta.AttachmentExtension   AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ta.Attachment) AS bigint) AS Length
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.TranID = @TransaccionId
ORDER BY ta.ID DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionAttachmentDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<TransaccionAttachmentDto> AddAttachmentAsync(TransaccionAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.Content is null || request.Content.Length == 0)
      throw new ArgumentException("El archivo adjunto no contiene datos.", nameof(request));

    if (request.Content.Length > TransaccionAttachmentCreateRequest.MaxFileSizeBytes)
      throw new InvalidOperationException("El archivo adjunto excede el tamaño máximo permitido (5 MB).");

    const string insertSql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    var newId = await ExecuteInsertAsync(
      insertSql,
      new
      {
        TranID = request.TransaccionId,
        Attachment = request.Content,
        AttachmentName = request.FileName,
        AttachmentExtension = string.IsNullOrWhiteSpace(request.Extension) ? null : request.Extension,
        AttachmentDescription = string.IsNullOrWhiteSpace(request.Description)
          ? "Archivo adjunto (carga manual)"
          : request.Description
      },
      ct);

    const string selectSql = @"SELECT
    ta.ID                    AS Id,
    ta.TranID                AS TransaccionId,
    ta.AttachmentName        AS AttachmentName,
    ta.AttachmentExtension   AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ta.Attachment) AS bigint) AS Length
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId;";

    using var conn = new SqlConnection(_cs);
    var dto = await conn.QueryFirstOrDefaultAsync<TransaccionAttachmentDto>(
      new CommandDefinition(selectSql, new { AttachmentId = newId }, cancellationToken: ct));

    if (dto is null)
      throw new InvalidOperationException("No se pudo recuperar el adjunto creado.");

    return dto;
  }

  public async Task<TransaccionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (1)
    ta.AttachmentName      AS AttachmentName,
    ta.AttachmentExtension AS AttachmentExtension,
    ta.Attachment          AS Attachment
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId;";

    using var conn = new SqlConnection(_cs);
    var row = await conn.QueryFirstOrDefaultAsync<(string? AttachmentName, string? AttachmentExtension, byte[] Attachment)>(
        new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (row.Attachment is null || row.Attachment.Length == 0)
    {
      return null;
    }

    var fileName = row.AttachmentName ?? $"attachment-{attachmentId}";
    if (!string.IsNullOrWhiteSpace(row.AttachmentExtension) &&
        !fileName.EndsWith($".{row.AttachmentExtension}", StringComparison.OrdinalIgnoreCase))
    {
      fileName = $"{fileName}.{row.AttachmentExtension}";
    }

    return new TransaccionAttachmentContent
    {
      AttachmentId = attachmentId,
      FileName = fileName,
      ContentType = ResolveContentType(row.AttachmentExtension),
      Bytes = row.Attachment
    };
  }

  public async Task DeleteAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.TRANSACTION_ATTACHMENT WHERE ID = @AttachmentId;";

    using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));
  }

  public async Task<int> GetComprobanteIdByXmlAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP 1 Comprobante_ID
FROM cfdi.comprobante
WHERE XML_Attachment_ID = @AttachmentId;";

    using var conn = new SqlConnection(_cs);
    var comprobanteId = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    return comprobanteId ?? 0;
  }

  public async Task<bool> IsComprobanteLinkedToTransaccionAsync(int transaccionId, int comprobanteId, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP 1 1
FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = @TransaccionId
  AND Comprobante_ID = @ComprobanteId;";

    using var conn = new SqlConnection(_cs);
    var exists = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId, ComprobanteId = comprobanteId }, cancellationToken: ct));

    return exists.HasValue;
  }

  public async Task SetAttachmentTransaccionAsync(int attachmentId, int? transaccionId, CancellationToken ct = default)
  {
    const string sql = @"UPDATE dbo.TRANSACTION_ATTACHMENT
SET TranID = @TransaccionId
WHERE ID = @AttachmentId;";

    using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(
        new CommandDefinition(sql, new { AttachmentId = attachmentId, TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<IReadOnlyList<TransaccionComprobanteDto>> GetComprobantesAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    CAST(tc.Monto AS decimal(18, 4))                       AS PolizaMonto,
    tc.Comprobante_ID                                      AS ComprobanteId,
    CASE WHEN cd.Incluir_En_Declaracion = 1 THEN N'✔' ELSE N'X' END AS D,
    cd.Fecha,
    cd.MESES                                               AS MesGlobal,
    cd.ANIO                                                AS AnioGlobal,
    cd.EMISOR                                              AS Emisor,
    CAST(cd.SubTotal AS decimal(18, 4))                    AS SubTotal,
    CAST(cd.Descuento AS decimal(18, 4))                   AS Descuento,
    CAST(cd.SubTotal_Desc AS decimal(18, 4))               AS SubTotalDesc,
    CAST(cd.Actos_16 AS decimal(18, 4))                    AS Actos16,
    CAST(cd.Actos_0 AS decimal(18, 4))                     AS Actos0,
    CAST(cd.IVA AS decimal(18, 4))                         AS Iva,
    CAST(cd.IEPS AS decimal(18, 4))                        AS Ieps,
    CAST(cd.IVA_RETENIDO AS decimal(18, 4))                AS IvaRetenido,
    CAST(cd.ISR_RETENIDO AS decimal(18, 4))                AS IsrRetenido,
    CAST(cd.IEPS_RETENIDO AS decimal(18, 4))               AS IepsRetenido,
    CAST(cd.Total AS decimal(18, 4))                       AS Total,
    cd.FOLIO_FISCAL                                        AS FolioFiscal,
    cd.FormaPago,
    cd.TipoDeComprobante,
    cd.MetodoPago,
    cd.UsoCFDI                                             AS UsoCfdi,
    cd.FechaCancelacion,
    cd.Estatus,
    tc.Transaccion_ID                                      AS TransaccionId
FROM dbo.Transaccion_Comprobante AS tc
INNER JOIN cfdi.Comprobante_Detalle AS cd ON tc.Comprobante_ID = cd.Comprobante_Id
WHERE tc.Transaccion_ID = @TransaccionId
ORDER BY cd.Fecha DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionComprobanteDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionReservacionLinkDto>> GetReservacionLinksAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    rt.ReservationID                                     AS ReservationId,
    rt.TransaccionID                                     AS TransaccionId,
    CAST(ISNULL(rt.Amount, 0) AS decimal(18, 2))         AS Amount,
    ISNULL(c.Nombre, '(Sin cliente)')                    AS Cliente,
    r.CHECKIN                                            AS CheckIn,
    r.CHECKOUT                                           AS CheckOut,
    r.STATUS                                             AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18, 2))     AS TotalPrice,
    CAST(ISNULL(pa.PAGADO, 0) AS decimal(18, 2))         AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) - ISNULL(pa.PAGADO, 0) AS decimal(18, 2)) AS PorPagar,
    r.NOTES                                              AS Notes
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.RESERVATION r
  ON r.ID = rt.ReservationID
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
OUTER APPLY
(
  SELECT SUM(rt2.Amount) AS PAGADO
  FROM dbo.Reservation_Transacciones rt2
  WHERE rt2.ReservationID = r.ID
) pa
WHERE rt.TransaccionID = @TransaccionId
ORDER BY rt.ReservationID DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionReservacionLinkDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionReservacionSearchItemDto>> SearchReservacionesAsync(string? search, CancellationToken ct = default)
  {
    var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    var reservationId = int.TryParse(normalizedSearch, out var parsedReservationId)
        ? parsedReservationId
        : (int?)null;

    const string sql = @"SELECT TOP (100)
    lr.ID                                                 AS ReservationId,
    ISNULL(lr.Nombre, '(Sin cliente)')                    AS Cliente,
    lr.CHECKIN                                            AS CheckIn,
    lr.CHECKOUT                                           AS CheckOut,
    lr.STATUS                                             AS Status,
    CAST(ISNULL(lr.TOTAL_PRICE, 0) AS decimal(18, 2))     AS TotalPrice,
    CAST(ISNULL(lr.PAGADO, 0) AS decimal(18, 2))          AS Pagado,
    CAST(ISNULL(lr.POR_PAGAR, 0) AS decimal(18, 2))       AS PorPagar,
    lr.NOTES                                              AS Notes
FROM dbo.LISTA_DE_RESERVACIONES lr
WHERE
(
  @ReservationId IS NOT NULL
  AND lr.ID = @ReservationId
)
OR
(
  ABS(CONVERT(decimal(18, 2), ISNULL(lr.POR_PAGAR, 0))) > 2
  AND
  (
    @Search IS NULL
    OR lr.Nombre LIKE @SearchLike
    OR lr.NOTES LIKE @SearchLike
  )
)
ORDER BY
  CASE WHEN @ReservationId IS NOT NULL AND lr.ID = @ReservationId THEN 0 ELSE 1 END,
  lr.ID DESC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionReservacionSearchItemDto>(
        new CommandDefinition(
            sql,
            new
            {
              Search = normalizedSearch,
              SearchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%",
              ReservationId = reservationId
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCommandResult> UpsertReservacionLinkAsync(TransaccionReservacionLinkUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.TransaccionId <= 0 || request.ReservationId <= 0)
      return TransaccionCommandResult.Fail("Selecciona una póliza y una reservación válidas.");

    if (decimal.Abs(request.Amount) < 0.01m)
      return TransaccionCommandResult.Fail("Ingresa un monto distinto de cero.");

    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      const string existsReservationSql = @"SELECT TOP (1) 1
FROM dbo.RESERVATION
WHERE ID = @ReservationId;";

      var reservationExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsReservationSql,
              new { request.ReservationId },
              tx,
              cancellationToken: ct));

      if (!reservationExists.HasValue)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La reservación seleccionada no existe.");
      }

      const string existsTransaccionSql = @"SELECT TOP (1) 1
FROM dbo.Transacciones
WHERE ID = @TransaccionId;";

      var transaccionExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsTransaccionSql,
              new { request.TransaccionId },
              tx,
              cancellationToken: ct));

      if (!transaccionExists.HasValue)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La póliza seleccionada no existe.");
      }

      const string existsLinkSql = @"SELECT TOP (1) 1
FROM dbo.Reservation_Transacciones
WHERE ReservationID = @ReservationId
  AND TransaccionID = @TransaccionId;";

      var linkExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsLinkSql,
              new
              {
                request.ReservationId,
                request.TransaccionId
              },
              tx,
              cancellationToken: ct));

      if (linkExists.HasValue)
      {
        const string updateSql = @"UPDATE dbo.Reservation_Transacciones
SET Amount = @Amount
WHERE ReservationID = @ReservationId
  AND TransaccionID = @TransaccionId;";

        await conn.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new
                {
                  request.Amount,
                  request.ReservationId,
                  request.TransaccionId
                },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);
        return TransaccionCommandResult.Ok("Asignación de reservación actualizada.");
      }

      const string insertSql = @"INSERT INTO dbo.Reservation_Transacciones
(ReservationID, TransaccionID, Amount)
VALUES (@ReservationId, @TransaccionId, @Amount);";

      await conn.ExecuteAsync(
          new CommandDefinition(
              insertSql,
              new
              {
                request.ReservationId,
                request.TransaccionId,
                request.Amount
              },
              tx,
              cancellationToken: ct));

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok("Reservación ligada correctamente.");
    }
    catch (Exception ex)
    {
      await tx.RollbackAsync(ct);
      _logger.LogError(
          ex,
          "Error al guardar vínculo entre transacción {TransaccionId} y reservación {ReservationId}",
          request.TransaccionId,
          request.ReservationId);

      return TransaccionCommandResult.Fail("No se pudo guardar la asignación de la reservación.");
    }
  }

  public async Task<TransaccionCommandResult> DeleteReservacionLinkAsync(int transaccionId, int reservationId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.Reservation_Transacciones
WHERE TransaccionID = @TransaccionId
  AND ReservationID = @ReservationId;";

    try
    {
      using var conn = new SqlConnection(_cs);
      var affectedRows = await conn.ExecuteAsync(
          new CommandDefinition(
              sql,
              new
              {
                TransaccionId = transaccionId,
                ReservationId = reservationId
              },
              cancellationToken: ct));

      return affectedRows > 0
          ? TransaccionCommandResult.Ok("Asignación eliminada correctamente.")
          : TransaccionCommandResult.Fail("No se encontró la asignación a eliminar.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Error al eliminar vínculo entre transacción {TransaccionId} y reservación {ReservationId}",
          transaccionId,
          reservationId);

      return TransaccionCommandResult.Fail("No se pudo eliminar la asignación de la reservación.");
    }
  }

  public async Task<TransaccionCommandResult> LinkCfdiAndRelinkAttachmentAsync(
      int transaccionId,
      int comprobanteId,
      decimal monto,
      CancellationToken ct = default)
  {
    await using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      await UpsertTransaccionComprobanteAsync(conn, tx, transaccionId, comprobanteId, monto, ct);
      await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, transaccionId, ct);

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok("Transacción ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
    {
      await tx.RollbackAsync(ct);
      return TransaccionCommandResult.Fail("Ya existe un vínculo entre esta transacción y el CFDI.");
    }
    catch (Exception ex)
    {
      await tx.RollbackAsync(ct);
      _logger.LogError(
          ex,
          "Error al ligar transacción {TransaccionId} con comprobante {ComprobanteId}",
          transaccionId,
          comprobanteId);
      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
  }

  public async Task<TransaccionCommandResult> InsertTransaccionComprobanteAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default)
  {
    try
    {
      await using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

      const string sql = @"INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
VALUES (@TransaccionId, @ComprobanteId, @Monto);";

      await conn.ExecuteAsync(
          new CommandDefinition(
              sql,
              new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = monto },
              tx,
              cancellationToken: ct));

      await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, transaccionId, ct);
      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok("Transacción ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
    {
      return TransaccionCommandResult.Fail("Ya existe un vínculo entre esta transacción y el CFDI.");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error al ligar transacción {TransaccionId} con comprobante {ComprobanteId}", transaccionId, comprobanteId);
      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
  }

  public async Task ToggleComprobanteAsync(int transaccionId, int comprobanteId, bool vincular, CancellationToken ct = default)
  {
    using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      if (vincular)
      {
        const string sqlTotal = @"SELECT CAST(c.Total AS decimal(18,4))
FROM cfdi.Comprobante c
WHERE c.Comprobante_Id = @ComprobanteId;";

        var total = await conn.ExecuteScalarAsync<decimal?>(
            new CommandDefinition(sqlTotal, new { ComprobanteId = comprobanteId }, tx, cancellationToken: ct));

        if (total is null)
        {
          await tx!.RollbackAsync(ct);
          throw new InvalidOperationException("Comprobante no encontrado.");
        }

        await UpsertTransaccionComprobanteAsync(conn, tx, transaccionId, comprobanteId, total.Value, ct);
        await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, transaccionId, ct);
      }
      else
      {
        const string sqlDelete = @"DELETE FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = @TransaccionId
  AND Comprobante_ID = @ComprobanteId;";

        var deleted = await conn.ExecuteAsync(
            new CommandDefinition(sqlDelete,
                new { TransaccionId = transaccionId, ComprobanteId = comprobanteId },
                tx,
                cancellationToken: ct));

        if (deleted > 0)
        {
          var nextTransaccionId = await GetPreferredLinkedTransaccionIdAsync(conn, tx, comprobanteId, ct);
          await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, nextTransaccionId, ct);
        }
      }

      await tx!.CommitAsync(ct);
    }
    catch
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      throw;
    }
  }

  public async Task<TransaccionCommandResult> UnlinkComprobanteAsync(TransaccionComprobanteUnlinkRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      var isComplemento = string.Equals(request.Tipo, "COMP", StringComparison.OrdinalIgnoreCase);
      var updated = 0;

      if (isComplemento)
      {
        const string sqlDeleteDoctoRelacionado = @"DELETE FROM dbo.Transaccion_DoctoRelacionado
WHERE Transaccion_ID = @CurrentTransaccionId
  AND DoctoRelacionado_ID = @ComprobanteId;";

        updated = await conn.ExecuteAsync(
            new CommandDefinition(
                sqlDeleteDoctoRelacionado,
                new
                {
                  request.CurrentTransaccionId,
                  request.ComprobanteId
                },
                tx,
                cancellationToken: ct));
      }
      else
      {
        const string sqlDeleteLink = @"DELETE FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = @CurrentTransaccionId
  AND Comprobante_ID = @ComprobanteId;";

        updated = await conn.ExecuteAsync(
            new CommandDefinition(
                sqlDeleteLink,
                new
                {
                  request.CurrentTransaccionId,
                  request.ComprobanteId
                },
                tx,
                cancellationToken: ct));

        if (updated > 0)
        {
          var nextTransaccionId = await GetPreferredLinkedTransaccionIdAsync(conn, tx, request.ComprobanteId, ct);
          await ReassignXmlAttachmentAsync(conn, tx, request.ComprobanteId, nextTransaccionId, ct);
        }
      }

      if (updated == 0)
      {
        await tx!.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se encontró el vínculo de este comprobante con la póliza actual.");
      }

      await tx!.CommitAsync(ct);
      return TransaccionCommandResult.Ok("Comprobante desligado correctamente.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      _logger.LogError(
          ex,
          "Failed to unlink Comprobante {ComprobanteId} from Transaccion {TransaccionId}",
          request.ComprobanteId,
          request.CurrentTransaccionId);

      return TransaccionCommandResult.Fail("No se pudo desligar el comprobante. Inténtalo de nuevo.");
    }
  }

  public async Task<TransaccionGuardarCerrarResult> GuardarYCerrarAsync(TransaccionGuardarCerrarRequest request, CancellationToken ct = default)
  {
    using var conn = new SqlConnection(_cs);
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      const string sqlUpdate = @"UPDATE dbo.Transacciones
SET Concepto = @Concepto,
    Fecha = @Fecha,
    Cuenta = @Cuenta,
    Monto = @Monto,
    Categoria = @Categoria,
    Facturado = @Facturado,
    Memo = @Memo,
    ProyectoID = @ProyectoId,
    CompraID = @CompraId,
    ServicioID = @ServicioId,
    NominaID = @NominaId,
    Tipo_Poliza = @TipoPoliza,
    Forma_Pago = @FormaPago
WHERE ID = @TransaccionId;";

      var affected = await conn.ExecuteAsync(
          new CommandDefinition(sqlUpdate,
              new
              {
                request.TransaccionId,
                request.Concepto,
                request.Fecha,
                request.Cuenta,
                request.Monto,
                request.Categoria,
                request.Facturado,
                request.Memo,
                request.ProyectoId,
                request.CompraId,
                request.ServicioId,
                request.NominaId,
                request.TipoPoliza,
                request.FormaPago
              },
              tx,
              cancellationToken: ct));

      if (affected == 0)
      {
        await tx!.RollbackAsync(ct);
        return TransaccionGuardarCerrarResult.Fail("Transacción no encontrada.");
      }

      var totals = await LoadTotalsAsync(conn, tx, request.TransaccionId, ct);
      await tx!.CommitAsync(ct);
      return TransaccionGuardarCerrarResult.Ok(totals, "Transacción guardada correctamente.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      return TransaccionGuardarCerrarResult.Fail($"Error al guardar: {ex.Message}");
    }
  }

  public async Task<TransaccionCommandResult> ApplyCategoriaPlantillaAsync(
      int transaccionId,
      int categoriaId,
      CancellationToken ct = default)
  {
    var parameters = new Dictionary<string, object?>
    {
      ["@TransactionID"] = transaccionId,
      ["@CategoriaID"] = categoriaId
    };

    try
    {
      _logger.LogInformation(
          "Applying category template {CategoriaId} to transaction {TransactionId}",
          categoriaId,
          transaccionId);

      await _storedProcService.ExecuteAsync(
          "dbo.APLICAR_PLANTILLA_CATEGORIA",
          parameters,
          ct);

      return TransaccionCommandResult.Ok("Plantilla aplicada correctamente a la transacción seleccionada.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to apply category template {CategoriaId} to transaction {TransactionId}",
          categoriaId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo aplicar la plantilla de categoría. Revisa los datos e inténtalo nuevamente.");
    }
  }

  public async Task<TransaccionCommandResult> ProcessSatXmlAsync(
      int attachmentId,
      int transaccionId,
      CancellationToken ct = default)
  {
    try
    {
      _logger.LogInformation(
          "Processing SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      await ProcessSatXmlV2Async(conn, transaction: null, transaccionId, attachmentId, ct);

      return TransaccionCommandResult.Ok("El XML del SAT se procesó correctamente para la transacción seleccionada.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to process SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo procesar el XML del SAT. Verifica el adjunto y vuelve a intentar.");
    }
  }

  public async Task<TransaccionCommandResult> TimbrarCfdiPublicoAsync(
      TransaccionTimbrarPublicoRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.TransaccionId <= 0)
      return TransaccionCommandResult.Fail("La póliza seleccionada no es válida.");

    if (request.Monto <= 0m)
      return TransaccionCommandResult.Fail("El monto para el CFDI público debe ser mayor que cero.");

    var mes = NormalizeGlobalMonth(request.GlobalMes);
    if (mes is null)
      return TransaccionCommandResult.Fail("El mes global seleccionado no es válido.");

    if (request.GlobalAnio < 2000 || request.GlobalAnio > 2100)
      return TransaccionCommandResult.Fail("El año global seleccionado no es válido.");

    var templateComprobanteId = GetPublicoTemplateComprobanteId();
    var templatePrepared = false;
    string? facturamaCfdiId = null;

    try
    {
      string jsonPayload;
      await using (var conn = new SqlConnection(_cs))
      {
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                "dbo.Create_CFDI_Publico",
                new
                {
                  Monto = request.Monto,
                  GlobalMes = mes,
                  GlobalAnio = request.GlobalAnio.ToString("0000", CultureInfo.InvariantCulture),
                  Folio = request.TransaccionId.ToString(CultureInfo.InvariantCulture)
                },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

        templatePrepared = true;

        jsonPayload = await conn.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition(
                "dbo.GetComprobanteJson",
                new { Comprobante_ID = templateComprobanteId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct)) ?? string.Empty;
      }

      if (string.IsNullOrWhiteSpace(jsonPayload))
        return TransaccionCommandResult.Fail("No se pudo generar el JSON del CFDI público.");

      facturamaCfdiId = await _facturamaApiClient.CreateIssuedCfdiAsync(jsonPayload, ct);

      var xmlDocument = await _facturamaApiClient.DownloadIssuedDocumentAsync(
          facturamaCfdiId,
          FacturamaIssuedDocumentType.Xml,
          ct);
      var pdfDocument = await _facturamaApiClient.DownloadIssuedDocumentAsync(
          facturamaCfdiId,
          FacturamaIssuedDocumentType.Pdf,
          ct);

      await using var writeConn = new SqlConnection(_cs);
      await writeConn.OpenAsync(ct);
      await using var tx = (SqlTransaction)await writeConn.BeginTransactionAsync(ct);

      try
      {
        var xmlAttachmentId = await InsertAttachmentAsync(
            writeConn,
            tx,
            request.TransaccionId,
            facturamaCfdiId,
            xmlDocument.Extension,
            $"XML POLIZA {request.TransaccionId}",
            xmlDocument.Bytes,
            ct);

        await ProcessSatXmlV2Async(writeConn, tx, request.TransaccionId, xmlAttachmentId, ct);

        await InsertAttachmentAsync(
            writeConn,
            tx,
            request.TransaccionId,
            facturamaCfdiId,
            pdfDocument.Extension,
            $"PDF POLIZA {request.TransaccionId}",
            pdfDocument.Bytes,
            ct);

        await tx.CommitAsync(ct);
      }
      catch
      {
        await tx.RollbackAsync(ct);
        throw;
      }

      return TransaccionCommandResult.Ok("La factura al público en general se generó, timbró y procesó correctamente.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to stamp public CFDI for transaction {TransaccionId}",
          request.TransaccionId);

      if (!string.IsNullOrWhiteSpace(facturamaCfdiId))
      {
        return TransaccionCommandResult.Fail(
            $"El CFDI se timbró en Facturama ({facturamaCfdiId}), pero no se pudo completar el registro local: {ex.Message}");
      }

      return TransaccionCommandResult.Fail($"No se pudo generar el CFDI público: {ex.Message}");
    }
    finally
    {
      if (templatePrepared)
      {
        try
        {
          await ResetPublicoTemplateAsync(templateComprobanteId, ct);
        }
        catch (Exception ex)
        {
          _logger.LogWarning(
              ex,
              "Failed to reset public CFDI template comprobante {TemplateComprobanteId}",
              templateComprobanteId);
        }
      }
    }
  }

  public async Task<TransaccionCommandResult> RegenerarPolizaDesdeComprobanteEnTransaccionAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default)
  {
    using var conn = new SqlConnection(_cs);

    try
    {
      await conn.ExecuteAsync(
          new CommandDefinition(
              "[contabilidad].[Regenerar_Poliza_Desde_Comprobante_En_Transaccion]",
              new
              {
                Comprobante_Id = comprobanteId,
                Transaccion_ID = transaccionId
              },
              commandType: CommandType.StoredProcedure,
              cancellationToken: ct));

      return TransaccionCommandResult.Ok("Movimientos regenerados correctamente.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to regenerate poliza movements from comprobante {ComprobanteId} for transaccion {TransaccionId}",
          comprobanteId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudieron regenerar los movimientos desde el comprobante.");
    }
  }

  public async Task<TransaccionCommandResult> RegenerarPolizaDesdeComplementoEnTransaccionAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default)
  {
    using var conn = new SqlConnection(_cs);

    try
    {
      await conn.ExecuteAsync(
          new CommandDefinition(
              "[contabilidad].[Regenerar_Poliza_Desde_Complemento_En_Transaccion]",
              new
              {
                Comprobante_Id = comprobanteId,
                Transaccion_ID = transaccionId
              },
              commandType: CommandType.StoredProcedure,
              cancellationToken: ct));

      return TransaccionCommandResult.Ok("Movimientos regenerados correctamente.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to regenerate poliza movements from complemento {ComprobanteId} for transaccion {TransaccionId}",
          comprobanteId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudieron regenerar los movimientos desde el complemento.");
    }
  }

  public async Task DeleteMovimientoAsync(int transaccionId, int movimientoId, CancellationToken ct = default)
  {
    const string sql = @"DELETE FROM dbo.Registro_Contable
WHERE ID = @MovimientoId
  AND TransaccionID = @TransaccionId;";

    using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(
      new CommandDefinition(sql, new { MovimientoId = movimientoId, TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<TransaccionCommandResult> DeleteTransaccionAsync(int transaccionId, CancellationToken ct = default)
  {
      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
          const string checkSql = @"
              IF EXISTS (SELECT 1 FROM dbo.Actividad_Transacciones WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Registro_Contable WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Transaccion_Comprobante WHERE Transaccion_ID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Transaccion_DoctoRelacionado WHERE Transaccion_ID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Reservation_Transacciones WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.TRANSACTION_ATTACHMENT WHERE TranID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM bancos.Movimientos WHERE Transaccion_ID = @TransaccionId)
              BEGIN
                  SELECT 1;
              END
              ELSE
              BEGIN
                  SELECT 0;
              END";

          var hasRelatedRecords = await conn.ExecuteScalarAsync<bool>(
              new CommandDefinition(checkSql, new { TransaccionId = transaccionId }, tx, cancellationToken: ct));

          if (hasRelatedRecords)
          {
              await tx!.RollbackAsync(ct);
              return TransaccionCommandResult.Fail("No se puede eliminar la transacción porque tiene registros relacionados (movimientos, adjuntos, comprobantes, etc.).");
          }

          const string deleteSql = @"DELETE FROM dbo.Transacciones WHERE ID = @TransaccionId;";
          var affectedRows = await conn.ExecuteAsync(
              new CommandDefinition(deleteSql, new { TransaccionId = transaccionId }, tx, cancellationToken: ct));

          if (affectedRows == 0)
          {
              await tx!.RollbackAsync(ct);
              return TransaccionCommandResult.Fail("No se encontró la transacción a eliminar.");
          }

          await tx!.CommitAsync(ct);
          return TransaccionCommandResult.Ok("Transacción eliminada correctamente.");
      }
      catch (Exception ex)
      {
          try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
          _logger.LogError(ex, "Error al eliminar la transacción {TransaccionId}", transaccionId);
          return TransaccionCommandResult.Fail($"Ocurrió un error al eliminar la transacción: {ex.Message}");
      }
  }

  public async Task<TransaccionCreateResult> CreateTransaccionAsync(TransaccionCreateRequest request, CancellationToken ct = default)
  {
      const string sql = @"
          INSERT INTO dbo.Transacciones (RFC, Fecha, Concepto, Monto, Tipo_Poliza, Forma_Pago, Categoria, Facturado, Memo, ProyectoID, CompraID, ServicioID, NominaID, Cuenta)
          VALUES (@Rfc, @Fecha, @Concepto, @Monto, @TipoPoliza, @FormaPago, @CategoriaId, @Facturado, @Memo, @ProyectoId, @CompraId, @ServicioId, @NominaId, @Cuenta);
          SELECT CAST(SCOPE_IDENTITY() as int);";

      const string defaultCategoriaSql = @"
          SELECT TOP (1) c.ID
          FROM dbo.Categorias c
          WHERE c.GrupoCategoria = 'PLANTILLA'
            AND c.RFC = @Rfc
          ORDER BY c.Descripcion ASC;";

      try
      {
          using var conn = new SqlConnection(_cs);
          var categoriaId = request.CategoriaId;

          if (!categoriaId.HasValue)
          {
              categoriaId = await conn.ExecuteScalarAsync<int?>(
                  new CommandDefinition(defaultCategoriaSql, new { request.Rfc }, cancellationToken: ct));

              if (!categoriaId.HasValue)
              {
                  return TransaccionCreateResult.Fail("No se encontró una categoría válida para la transacción.");
              }
          }

          var newId = await conn.ExecuteScalarAsync<int>(
              new CommandDefinition(
                  sql,
                  new
                  {
                      request.Rfc,
                      request.Fecha,
                      request.Concepto,
                      request.Monto,
                      request.TipoPoliza,
                      request.FormaPago,
                      CategoriaId = categoriaId,
                      request.Facturado,
                      request.Memo,
                      request.ProyectoId,
                      request.CompraId,
                      request.ServicioId,
                      request.NominaId,
                      request.Cuenta
                  },
                  cancellationToken: ct));
          return TransaccionCreateResult.Ok(newId, "Transacción creada correctamente.");
      }
      catch (Exception ex)
      {
          _logger.LogError(ex, "Error al crear la transacción.");
          return TransaccionCreateResult.Fail($"Ocurrió un error al crear la transacción: {ex.Message}");
      }
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesListAsync(TransaccionFilter filter, CancellationToken ct = default)
  {
      var sqlBuilder = new SqlBuilder();
      var template = sqlBuilder.AddTemplate(@"
          SELECT
              t.ID AS Id,
              t.Fecha,
              t.Concepto,
              t.Monto,
              t.Tipo_Poliza AS TipoPoliza,
              t.Forma_Pago AS FormaPago
          FROM dbo.Transacciones t
          /**where**/
          /**orderby**/"
      );

      if (filter.Id.HasValue)
      {
          sqlBuilder.Where("t.ID = @Id", new { filter.Id });
      }
      if (filter.Year.HasValue)
      {
          sqlBuilder.Where("YEAR(t.Fecha) = @Year", new { filter.Year });
      }
      if (filter.Month.HasValue)
      {
          sqlBuilder.Where("MONTH(t.Fecha) = @Month", new { filter.Month });
      }
      if (!string.IsNullOrWhiteSpace(filter.Concepto))
      {
          sqlBuilder.Where("t.Concepto LIKE @Concepto", new { Concepto = $"%{filter.Concepto}%" });
      }
      if (filter.Monto.HasValue)
      {
          sqlBuilder.Where("t.Monto = @Monto", new { filter.Monto });
      }
      if (!string.IsNullOrWhiteSpace(filter.TipoPoliza))
      {
          sqlBuilder.Where("t.Tipo_Poliza LIKE @TipoPoliza", new { TipoPoliza = $"%{filter.TipoPoliza}%" });
      }
      if (!string.IsNullOrWhiteSpace(filter.FormaPago))
      {
          sqlBuilder.Where("t.Forma_Pago LIKE @FormaPago", new { FormaPago = $"%{filter.FormaPago}%" });
      }
      if (!string.IsNullOrWhiteSpace(filter.Rfc))
      {
          sqlBuilder.Where("t.RFC = @Rfc", new { filter.Rfc });
      }

      if (!string.IsNullOrWhiteSpace(filter.SortBy))
      {
          var columnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
          {
              { "Id", "t.ID" },
              { "Fecha", "t.Fecha" },
              { "Concepto", "t.Concepto" },
              { "Monto", "t.Monto" },
              { "TipoPoliza", "t.Tipo_Poliza" },
              { "FormaPago", "t.Forma_Pago" }
          };

          if (columnMap.TryGetValue(filter.SortBy, out var dbColumn))
          {
            _ = sqlBuilder.OrderBy($"{dbColumn} {(filter.SortAsc ? "ASC" : "DESC")}");
          }
          else
          {
            _ = sqlBuilder.OrderBy("t.Fecha DESC");
          }
      }
      else
      {
        _ = sqlBuilder.OrderBy("t.Fecha DESC");
      }

      using var conn = new SqlConnection(_cs);
      var rows = await conn.QueryAsync<TransaccionListItemDto>(
          new CommandDefinition(template.RawSql, template.Parameters, cancellationToken: ct)
      );
      return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByUuidAsync(string uuid, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(uuid))
      return Array.Empty<TransaccionListItemDto>();

    const string sql = @"SELECT
    T.ID                          AS Id,
    T.Fecha                       AS Fecha,
    T.Concepto                    AS Concepto,
    CAST(T.Monto AS decimal(18,4)) AS Monto,
    CAST(TC.Monto AS decimal(18,4))   AS MontoAsignado,
    T.Tipo_Poliza                 AS TipoPoliza,
    T.Forma_Pago                  AS FormaPago
FROM cfdi.TimbreFiscalDigital AS TFD
JOIN dbo.Transaccion_Comprobante AS TC
  ON TC.Comprobante_ID = TFD.Comprobante_Id
JOIN dbo.Transacciones AS T
  ON T.ID = TC.Transaccion_ID
WHERE TFD.UUID = @Uuid
ORDER BY T.Fecha;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
        new CommandDefinition(sql, new { Uuid = uuid }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByComprobanteIdAsync(int comprobanteId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    T.ID                          AS Id,
    T.Fecha                       AS Fecha,
    T.Concepto                    AS Concepto,
    CAST(T.Monto AS decimal(18,4)) AS Monto,
    CAST(TC.Monto AS decimal(18,4))   AS MontoAsignado,
    T.Tipo_Poliza                 AS TipoPoliza,
    T.Forma_Pago                  AS FormaPago
FROM dbo.Transaccion_Comprobante AS TC
JOIN dbo.Transacciones AS T
  ON T.ID = TC.Transaccion_ID
WHERE TC.Comprobante_ID = @ComprobanteId
ORDER BY T.Fecha;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
        new CommandDefinition(sql, new { ComprobanteId = comprobanteId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByDoctoRelacionadoIdAsync(int doctoRelacionadoId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    T.ID                            AS Id,
    T.Fecha                         AS Fecha,
    T.Concepto                      AS Concepto,
    CAST(T.Monto AS decimal(18,4))  AS Monto,
    CAST(TD.Monto AS decimal(18,4)) AS MontoAsignado,
    T.Tipo_Poliza                   AS TipoPoliza,
    T.Forma_Pago                    AS FormaPago
FROM cfdi.Pagos20_DoctoRelacionado AS DR
JOIN dbo.Transaccion_DoctoRelacionado AS TD
  ON TD.DoctoRelacionado_Id = DR.DoctoRelacionado_Id
JOIN dbo.Transacciones AS T
  ON T.ID = TD.Transaccion_ID
WHERE DR.DoctoRelacionado_Id = @DoctoRelacionadoId
ORDER BY T.Fecha;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
      new CommandDefinition(sql, new { DoctoRelacionadoId = doctoRelacionadoId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCommandResult> InsertTransaccionDoctoRelacionadoAsync(int transaccionId, int doctoRelacionadoId, decimal monto, CancellationToken ct = default)
  {
    const string sql = @"INSERT INTO dbo.Transaccion_DoctoRelacionado (Transaccion_ID, DoctoRelacionado_ID, Monto)
VALUES (@TransaccionId, @DoctoRelacionadoId, @Monto);";

    try
    {
      using var conn = new SqlConnection(_cs);
      await conn.ExecuteAsync(
        new CommandDefinition(sql, new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId, Monto = monto }, cancellationToken: ct));
      return TransaccionCommandResult.Ok("Transacción ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
    {
      return TransaccionCommandResult.Fail("Ya existe un vínculo entre esta transacción y el complemento.");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error al ligar transacción {TransaccionId} con docto relacionado {DoctoRelacionadoId}", transaccionId, doctoRelacionadoId);
      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
  }

  public async Task<TransaccionCommandResult> GuardarMovimientosAsync(TransaccionMovimientosUpdateRequest request, CancellationToken ct = default)
  {
      if (request is null)
          throw new ArgumentNullException(nameof(request));

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
          const string deleteSql = @"DELETE FROM dbo.Registro_Contable WHERE TransaccionID = @TransaccionId;";
          await conn.ExecuteAsync(new CommandDefinition(deleteSql, new { request.TransaccionId }, tx, cancellationToken: ct));

            if (request.Movimientos.Count != 0)
          {
              const string insertSql = @"
                  INSERT INTO dbo.Registro_Contable (TransaccionID, Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber)
                  VALUES (@TransaccionId, @Nivel1, @Nivel2, @Nivel3, @NombreCuenta, @Concepto, @Debe, @Haber);";

              var movementsToInsert = request.Movimientos.Select(m => new
              {
                  request.TransaccionId,
                  m.Nivel1,
                  m.Nivel2,
                  m.Nivel3,
                  m.NombreCuenta,
                  m.Concepto,
                  m.Debe,
                  m.Haber
              });

              await conn.ExecuteAsync(new CommandDefinition(insertSql, movementsToInsert, tx, cancellationToken: ct));
          }

          await tx!.CommitAsync(ct);
          return TransaccionCommandResult.Ok("Movimientos guardados correctamente.");
      }
      catch (Exception ex)
      {
          try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
          _logger.LogError(ex, "Error al guardar movimientos para la transacción {TransaccionId}", request.TransaccionId);
          return TransaccionCommandResult.Fail($"Ocurrió un error al guardar los movimientos: {ex.Message}");
      }
  }

  private sealed class CfdiCandidateRow
  {
    public long Comprobante_Id { get; set; }
    public DateTime Fecha { get; set; }
    public string? Tipo { get; set; }
    public string? Serie { get; set; }
    public string? Folio { get; set; }
    public string? Emisor_Rfc { get; set; }
    public string? Receptor_Rfc { get; set; }
    public string? UUID { get; set; }
    public string? FormaPago { get; set; }
    public decimal Total { get; set; }
    public int Polizas { get; set; }
    public decimal Asignado { get; set; }
    public string? MetodoPago { get; set; }
    public string? UsoCFDI { get; set; }
    public string? Conceptos { get; set; }
    public int? XML_Attachment_ID { get; set; }
  }

  private async Task<int> InsertAttachmentAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      string fileName,
      string extension,
      string description,
      byte[] content,
      CancellationToken ct)
  {
    const string sql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    return await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
            sql,
            new
            {
              TranID = transaccionId,
              Attachment = content,
              AttachmentName = fileName,
              AttachmentExtension = extension,
              AttachmentDescription = description
            },
            transaction,
            cancellationToken: ct));
  }

  private static async Task<int?> ProcessSatXmlV2Async(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      int attachmentId,
      CancellationToken ct)
  {
    return await conn.QueryFirstOrDefaultAsync<int?>(
        new CommandDefinition(
            "cfdi.PROCESAR_SAT_XML_V2",
            new { TransaccionID = transaccionId, AttachmentID = attachmentId },
            transaction,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
  }

  private static async Task UpsertTransaccionComprobanteAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      long comprobanteId,
      decimal monto,
      CancellationToken ct)
  {
    const string sql = @"IF EXISTS (
    SELECT 1
    FROM dbo.Transaccion_Comprobante
    WHERE Transaccion_ID = @TransaccionId
      AND Comprobante_ID = @ComprobanteId
)
BEGIN
    UPDATE dbo.Transaccion_Comprobante
    SET Monto = @Monto
    WHERE Transaccion_ID = @TransaccionId
      AND Comprobante_ID = @ComprobanteId;
END
ELSE
BEGIN
    INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
    VALUES (@TransaccionId, @ComprobanteId, @Monto);
END;";

    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              TransaccionId = transaccionId,
              ComprobanteId = comprobanteId,
              Monto = monto
            },
            transaction,
            cancellationToken: ct));
  }

  private static async Task<int?> GetXmlAttachmentIdAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      CancellationToken ct)
  {
    const string sql = @"SELECT XML_Attachment_ID
FROM cfdi.Comprobante
WHERE Comprobante_ID = @ComprobanteId;";

    return await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            sql,
            new { ComprobanteId = comprobanteId },
            transaction,
            cancellationToken: ct));
  }

  private static async Task<int?> GetPreferredLinkedTransaccionIdAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      CancellationToken ct)
  {
    const string sql = @"SELECT TOP (1) tc.Transaccion_ID
FROM dbo.Transaccion_Comprobante tc
INNER JOIN dbo.Transacciones t
        ON t.ID = tc.Transaccion_ID
WHERE tc.Comprobante_ID = @ComprobanteId
ORDER BY t.Fecha, t.ID;";

    return await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            sql,
            new { ComprobanteId = comprobanteId },
            transaction,
            cancellationToken: ct));
  }

  private static async Task ReassignXmlAttachmentAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      int? transaccionId,
      CancellationToken ct)
  {
    var attachmentId = await GetXmlAttachmentIdAsync(conn, transaction, comprobanteId, ct);
    if (!attachmentId.HasValue || attachmentId.Value <= 0)
    {
      return;
    }

    const string sql = @"UPDATE dbo.TRANSACTION_ATTACHMENT
SET TranID = @TransaccionId
WHERE ID = @AttachmentId;";

    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              AttachmentId = attachmentId.Value,
              TransaccionId = transaccionId
            },
            transaction,
            cancellationToken: ct));
  }

  private async Task<int> ExecuteInsertAsync(string sql, object parameters, CancellationToken ct)
  {
    using var conn = new SqlConnection(_cs);
    return await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(sql, parameters, cancellationToken: ct));
  }

  private static async Task<MovimientoTotalsDto> LoadTotalsAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      CancellationToken ct)
  {
    const string sql = @"SELECT
    CAST(ISNULL(SUM(rc.Debe), 0) AS decimal(18,4))  AS Debe,
    CAST(ISNULL(SUM(rc.Haber), 0) AS decimal(18,4)) AS Haber
FROM dbo.Registro_Contable rc
WHERE rc.TransaccionID = @TransaccionId;";

    var totals = await conn.QueryFirstOrDefaultAsync<MovimientoTotalsDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, transaction, cancellationToken: ct));

    return totals ?? new MovimientoTotalsDto();
  }

  private static string ResolveContentType(string? extension)
  {
    if (string.IsNullOrWhiteSpace(extension))
    {
      return "application/octet-stream";
    }

    return extension.ToLowerInvariant() switch
    {
      "pdf" => "application/pdf",
      "xml" => "application/xml",
      "jpg" or "jpeg" => "image/jpeg",
      "png" => "image/png",
      "txt" => "text/plain",
      _ => "application/octet-stream"
    };
  }

  private int GetPublicoTemplateComprobanteId()
  {
    var configured = _cfg["Facturama:PublicoTemplateComprobanteId"];
    return int.TryParse(configured, out var templateId) && templateId > 0
        ? templateId
        : DefaultPublicoTemplateComprobanteId;
  }

  private async Task ResetPublicoTemplateAsync(int templateComprobanteId, CancellationToken ct)
  {
    var resetMes = _cfg["Facturama:PublicoTemplateResetMes"];
    if (string.IsNullOrWhiteSpace(resetMes))
    {
      resetMes = DefaultPublicoResetMes;
    }

    var configuredResetAnio = _cfg["Facturama:PublicoTemplateResetAnio"];
    var resetAnio = int.TryParse(configuredResetAnio, out var parsedResetAnio)
        ? parsedResetAnio
        : DefaultPublicoResetAnio;

    const string sql = @"UPDATE cfdi.InformacionGlobal
SET Meses = @Meses,
    Anio = @Anio
WHERE Comprobante_ID = @ComprobanteId;";

    using var conn = new SqlConnection(_cs);
    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              ComprobanteId = templateComprobanteId,
              Meses = resetMes,
              Anio = resetAnio
            },
            cancellationToken: ct));
  }

  private static string? NormalizeGlobalMonth(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
           month >= 1 &&
           month <= 12
        ? month.ToString("00", CultureInfo.InvariantCulture)
        : null;
  }
}
