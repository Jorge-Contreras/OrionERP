using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;

public sealed class TransaccionService : ITransaccionService
{
  private readonly string _cs;

  public TransaccionService(IConfiguration cfg)
  {
    _cs = cfg.GetConnectionString("OrionDb")
         ?? throw new InvalidOperationException("Missing connection string: OrionDb");
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
    t.ReservacionID     AS ReservacionId,
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

  public async Task<IReadOnlyList<TransaccionMovimientoDto>> GetMovimientosAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    rc.ID                 AS Id,
    rc.TransaccionID     AS TransaccionId,
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

  public async Task<IReadOnlyList<LookupInt32Dto>> SearchActividadesAsync(string rfc, string? term, int maxResults = 50, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (@MaxResults)
    a.ID          AS Id,
    a.Descripcion AS Description
FROM dbo.Actividad a
WHERE a.RFC = @Rfc
  AND (
    @Term IS NULL
    OR CAST(a.ID AS nvarchar(20)) LIKE @TermLike
    OR a.Descripcion LIKE @TermLike
  )
ORDER BY
  CASE WHEN @Term IS NOT NULL AND CAST(a.ID AS nvarchar(20)) = @Term THEN 0 ELSE 1 END,
  a.Descripcion ASC;";

    var parameters = new
    {
      Rfc = rfc,
      Term = term,
      TermLike = term is null ? null : $"%{term}%",
      MaxResults = maxResults
    };

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, parameters, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> SearchComprasAsync(string rfc, string? term, int maxResults = 50, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (@MaxResults)
    c.ID          AS Id,
    c.Descripcion AS Description
FROM dbo.Compra c
WHERE c.RFC = @Rfc
  AND (
    @Term IS NULL
    OR CAST(c.ID AS nvarchar(20)) LIKE @TermLike
    OR c.Descripcion LIKE @TermLike
  )
ORDER BY
  CASE WHEN @Term IS NOT NULL AND CAST(c.ID AS nvarchar(20)) = @Term THEN 0 ELSE 1 END,
  c.Descripcion ASC;";

    var parameters = new
    {
      Rfc = rfc,
      Term = term,
      TermLike = term is null ? null : $"%{term}%",
      MaxResults = maxResults
    };

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, parameters, cancellationToken: ct));
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

  public async Task<IReadOnlyList<LookupStringDto>> GetReservacionesAsync(string rfc, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    r.ID     AS Id,
    r.Nombre AS Description
FROM dbo.LISTA_DE_RESERVACIONES r
WHERE r.RFC = @Rfc
ORDER BY r.Nombre ASC;";

    using var conn = new SqlConnection(_cs);
    var rows = await conn.QueryAsync<LookupStringDto>(
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

  public async Task<IReadOnlyList<TransaccionComprobanteDto>> GetComprobantesAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql = @"SELECT
    c.Comprobante_Id                       AS ComprobanteId,
    c.Serie                                AS Serie,
    c.Folio                                AS Folio,
    c.Fecha                                AS Fecha,
    CAST(c.Total AS decimal(18,4))         AS Total,
    CAST(CASE WHEN tc.Transaccion_ID IS NULL THEN 0 ELSE 1 END AS bit) AS Vinculado
FROM dbo.Transaccion_Comprobante tc
INNER JOIN cfdi.Comprobante c ON c.Comprobante_Id = tc.Comprobante_ID
WHERE tc.Transaccion_ID = @TransaccionId
ORDER BY c.Fecha DESC;";

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
    ReservacionID = @ReservacionId,
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
                request.ReservacionId,
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
