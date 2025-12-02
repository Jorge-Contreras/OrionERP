using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;

public sealed class TransaccionService : ITransaccionService
{
  private readonly string _cs;
  private readonly IDbStoredProcService _storedProcService;
  private readonly ILogger<TransaccionService> _logger;

  public TransaccionService(
      IConfiguration cfg,
      IDbStoredProcService storedProcService,
      ILogger<TransaccionService> logger)
  {
    _cs = cfg.GetConnectionString("OrionDb")
         ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _storedProcService = storedProcService ?? throw new ArgumentNullException(nameof(storedProcService));
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
          Conceptos = row.Conceptos
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
    using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      if (request.UseDoctoRelacionadoTable)
      {
        const string sql = @"INSERT INTO dbo.Transaccion_DoctoRelacionado (Transaccion_ID, DoctoRelacionado_Id, Monto)
VALUES (@TransaccionId, @DoctoRelacionadoId, @Monto);";

        await conn.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                  TransaccionId = request.TransaccionId,
                  DoctoRelacionadoId = request.ComprobanteId,
                  Monto = request.Monto
                },
                tx,
                cancellationToken: ct));
      }
      else
      {
        const string sql = @"INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
VALUES (@TransaccionId, @ComprobanteId, @Monto);";

        await conn.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                  TransaccionId = request.TransaccionId,
                  ComprobanteId = request.ComprobanteId,
                  Monto = request.Monto
                },
                tx,
                cancellationToken: ct));
      }

      await tx!.CommitAsync(ct);
      return TransaccionCommandResult.Ok("Transacción ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      return TransaccionCommandResult.Fail("No se pudo ligar la transacción. Revisa duplicados o restricciones.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
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

        const string sqlExists = @"SELECT Transaccion_ID
FROM dbo.Transaccion_Comprobante
WHERE Comprobante_ID = @ComprobanteId;";

        var existingTransaccion = await conn.ExecuteScalarAsync<int?>(
            new CommandDefinition(sqlExists, new { ComprobanteId = comprobanteId }, tx, cancellationToken: ct));

        if (existingTransaccion.HasValue)
        {
          const string sqlUpdate = @"UPDATE dbo.Transaccion_Comprobante
SET Transaccion_ID = @TransaccionId,
    Monto = @Monto
WHERE Comprobante_ID = @ComprobanteId;";

          await conn.ExecuteAsync(
              new CommandDefinition(sqlUpdate,
                  new
                  {
                    TransaccionId = transaccionId,
                    ComprobanteId = comprobanteId,
                    Monto = total.Value
                  },
                  tx,
                  cancellationToken: ct));
        }
        else
        {
          const string sqlInsert = @"INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
VALUES (@TransaccionId, @ComprobanteId, @Monto);";

          await conn.ExecuteAsync(
              new CommandDefinition(sqlInsert,
                  new
                  {
                    TransaccionId = transaccionId,
                    ComprobanteId = comprobanteId,
                    Monto = total.Value
                  },
                  tx,
                  cancellationToken: ct));
        }
      }
      else
      {
        const string sqlDelete = @"DELETE FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = @TransaccionId
  AND Comprobante_ID = @ComprobanteId;";

        await conn.ExecuteAsync(
            new CommandDefinition(sqlDelete,
                new { TransaccionId = transaccionId, ComprobanteId = comprobanteId },
                tx,
                cancellationToken: ct));
      }

      await tx!.CommitAsync(ct);
    }
    catch
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      throw;
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
    var parameters = new Dictionary<string, object?>
    {
      ["@AttachmentID"] = attachmentId,
      ["@TransaccionID"] = transaccionId
    };

    try
    {
      _logger.LogInformation(
          "Processing SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      await _storedProcService.ExecuteAsync(
          "dbo.PROCESAR_SAT_XML",
          parameters,
          ct);

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
              sqlBuilder.OrderBy($"{dbColumn} {(filter.SortAsc ? "ASC" : "DESC")}");
          }
          else
          {
              sqlBuilder.OrderBy("t.Fecha DESC");
          }
      }
      else
      {
          sqlBuilder.OrderBy("t.Fecha DESC");
      }

      using var conn = new SqlConnection(_cs);
      var rows = await conn.QueryAsync<TransaccionListItemDto>(
          new CommandDefinition(template.RawSql, template.Parameters, cancellationToken: ct)
      );
      return rows.AsList();
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

          if (request.Movimientos.Any())
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
}
