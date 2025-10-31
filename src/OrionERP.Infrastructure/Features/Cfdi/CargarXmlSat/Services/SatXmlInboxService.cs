using Dapper;
using Microsoft.Data.SqlClient;           // ✅ correct provider
using Microsoft.Extensions.Configuration; // ✅ IConfiguration
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace OrionERP.Infrastructure.Features.Cfdi.CargarXmlSat.Services
{

  public sealed class SatXmlInboxService : ISatXmlInboxService
  {

    // XML Helpers for UUID extraction and normalization


// … inside SatXmlInboxService class …

private static string NormalizeUuid(string? s)
    => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim().ToUpperInvariant();

  /// <summary>
  /// Stream-reads XML and returns the UUID from any element whose LocalName == "TimbreFiscalDigital".
  /// Ignores namespaces; safe settings (no DTD, no external fetch).
  /// Returns null if not found or on XML errors.
  /// </summary>
  private static string? TryExtractUuidFromXml(byte[] bytes)
  {
    if (bytes is null || bytes.Length == 0) return null;

    var settings = new XmlReaderSettings
    {
      IgnoreComments = true,
      IgnoreProcessingInstructions = true,
      IgnoreWhitespace = true,
      DtdProcessing = DtdProcessing.Prohibit,
      XmlResolver = null
    };

    try
    {
      using var ms = new MemoryStream(bytes);
      using var xr = XmlReader.Create(ms, settings);

      while (xr.Read())
      {
        if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "TimbreFiscalDigital")
        {
          // Prefer exact "UUID"
          var uuid = xr.GetAttribute("UUID");
          if (!string.IsNullOrWhiteSpace(uuid))
            return NormalizeUuid(uuid);

          // Fallback: search attributes case-insensitively (handles "Uuid"/"uuid" etc.)
          if (xr.HasAttributes)
          {
            xr.MoveToFirstAttribute();
            do
            {
              if (string.Equals(xr.LocalName, "UUID", StringComparison.OrdinalIgnoreCase))
                return NormalizeUuid(xr.Value);
            } while (xr.MoveToNextAttribute());
          }
        }
      }
    }
    catch
    {
      // swallow parse errors and fall back to filename
    }
    return null;
  }







  private readonly string _cs;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SatXmlInboxService> _logger;

    public SatXmlInboxService(IConfiguration cfg, ILogger<SatXmlInboxService> logger)
    {
      _cfg = cfg;
      _logger = logger;
      _cs = cfg.GetConnectionString("OrionDb")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb");
    }

   



  //Helpers
  private static string UuidFromFileName(string fileName)
    {
      // Access used filename (without extension) as UUID token
      var baseName = Path.GetFileNameWithoutExtension(fileName);
      return baseName?.Trim() ?? string.Empty;
    }

    private static string AttachmentLikePattern(string fileNameNoExt)
    {
      // Access did: LIKE '%<filename without ext>%'
      return $"%{fileNameNoExt}%";
    }



    // Now this just returns the configured placeholder ID (defaults to 5505).
    public async Task<int> EnsureInboxTransaccionAsync(CancellationToken ct = default)
    {
      var idStr = _cfg["SatXml:PlaceholderTransaccionId"];
      if (!int.TryParse(idStr, out var id)) id = 5505;

      // Optional safety: verify it exists (kept light; you said it already exists)
      const string sql = "SELECT 1 FROM dbo.Transacciones WHERE ID = @ID;";
      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { ID = id }, cancellationToken: ct));
      if (!exists.HasValue)
        throw new InvalidOperationException($"Configured placeholder Transacciones.ID={id} not found. Create it or change SatXml:PlaceholderTransaccionId.");

      return id;
    }

    //Saves and Process the XML file
    public async Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default)
    {
      // Read file bytes up-front (needed for either branch)
      byte[] bytes;
      using (var ms = new MemoryStream())
      {
        await xmlStream.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
      }

      var uuidFromXml = TryExtractUuidFromXml(bytes);
      var uuidCandidate = !string.IsNullOrWhiteSpace(uuidFromXml)
          ? uuidFromXml
          : NormalizeUuid(UuidFromFileName(fileName));

      var placeholderId = await EnsureInboxTransaccionAsync(ct); // still 5505 (config)

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
        // --- pre-check: does UUID already exist & linked? ---
        if (!string.IsNullOrWhiteSpace(uuidCandidate))
        {
          var (found, comprobanteId, transaccionId) =
              await TryResolveExistingLinkedAsync(conn, tx, uuidCandidate, ct);

          if (found && transaccionId.HasValue)
          {
            // Already linked to a transaction: DO NOT reprocess; ensure attachment exists there.
            var inserted = await EnsureXmlAttachmentOnTranAsync(conn, tx, transaccionId.Value, fileName, bytes, ct);

            await tx!.CommitAsync(ct);
            return new SatXmlProcessResult(
                fileName,
                AttachmentId: 0,
                Success: true,
                Message: inserted
                    ? $"Ya conciliado (TranID={transaccionId}). Se agregó el XML como adjunto."
                    : $"Ya conciliado (TranID={transaccionId}). El adjunto XML ya existía.");
          }
        }

        // --- fallback: process into inbox (5505) like Step 3 ---
        // optional cleanup like Access
        await CleanupDuplicateAttachmentsByNameAsync(conn, tx, fileName, ct);

        const string insertAttach = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() as int);";

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".xml";

        var attachmentId = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                insertAttach,
                new
                {
                  TranID = placeholderId,
                  Attachment = bytes,
                  AttachmentName = fileName,
                  AttachmentExtension = ext.TrimStart('.'),
                  AttachmentDescription = "SAT XML upload"
                },
                tx, cancellationToken: ct)
        );

        // Call SP to parse into SAT tables
        const string sp = "dbo.PROCESAR_SAT_XML_V2";
        await conn.ExecuteAsync(
            new CommandDefinition(
                sp,
                new { TransaccionID = placeholderId, AttachmentID = attachmentId },
                commandType: CommandType.StoredProcedure,
                transaction: tx,
                cancellationToken: ct)
        );

        await tx!.CommitAsync(ct);
        return new SatXmlProcessResult(fileName, attachmentId, true, null);
      }
      catch (Exception ex)
      {
        try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
        _logger.LogError(ex, "SaveAndProcessAsync failed for file {File}", fileName);
        return new SatXmlProcessResult(fileName, 0, false, $"Error: {ex.Message}");
      }
    }


    //Resolver to check if UUID already exists
    private async Task<(bool found, int comprobanteId, int? transaccionId)> TryResolveExistingLinkedAsync(
  SqlConnection conn, SqlTransaction? tx, string uuid, CancellationToken ct)
    {
      const string sql = @"
SELECT TOP (1)
    t.Comprobante_ID      AS ComprobanteId,
    tc.Transaccion_ID     AS TransaccionId
FROM dbo.TimbreFiscalDigital t
LEFT JOIN dbo.Transaccion_Comprobante tc
    ON tc.Comprobante_ID = t.Comprobante_ID
WHERE UPPER(t.UUID) = UPPER(@Uuid);";

      var row = await conn.QueryFirstOrDefaultAsync<(int ComprobanteId, int? TransaccionId)>(
          new CommandDefinition(sql, new { Uuid = uuid }, tx, cancellationToken: ct)
      );

      return row.ComprobanteId == 0
          ? (false, 0, null)
          : (true, row.ComprobanteId, row.TransaccionId);
    }
    //helper to ensure the XML attachment exists on a specific Transacción
    private async Task<bool> EnsureXmlAttachmentOnTranAsync(
        SqlConnection conn, SqlTransaction? tx, int tranId, string fileName, byte[] bytes, CancellationToken ct)
    {
      var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
      const string existsSql = @"
SELECT TOP(1) 1
FROM dbo.TRANSACTION_ATTACHMENT
WHERE TranID = @TranID
  AND AttachmentExtension = 'xml'
  AND AttachmentName LIKE @LikeName;";

      var exists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(existsSql,
              new { TranID = tranId, LikeName = AttachmentLikePattern(fileNameNoExt) },
              tx, cancellationToken: ct));

      if (exists.HasValue) return false; // already present; nothing to do

      const string insertAttach = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, 'xml', @AttachmentDescription);";

      await conn.ExecuteAsync(
          new CommandDefinition(insertAttach,
              new
              {
                TranID = tranId,
                Attachment = bytes,
                AttachmentName = fileName,
                AttachmentDescription = "SAT XML upload (linked existing)"
              },
              tx, cancellationToken: ct));

      return true; // we inserted it
    }

    //CLEAN DUPLICATES ON ATTACHMENTS
    private async Task<int> CleanupDuplicateAttachmentsByNameAsync(
    SqlConnection conn, SqlTransaction? tx, string fileName, CancellationToken ct)
    {
      var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);
      const string delSql = @"
DELETE FROM dbo.TRANSACTION_ATTACHMENT
WHERE AttachmentExtension = 'xml'
  AND AttachmentName LIKE @LikeName;";

      return await conn.ExecuteAsync(
          new CommandDefinition(delSql,
              new { LikeName = AttachmentLikePattern(fileNameNoExt) }, tx, cancellationToken: ct));
    }



  }

}
