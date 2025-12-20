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
    // ===== XML Helpers for UUID extraction and normalization =====
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

            // Fallback: attributes case-insensitively ("Uuid"/"uuid", etc.)
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

    // ===== Small helpers =====
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

    /// <summary>
    /// Returns the configured placeholder ID (defaults to 5505), validating it exists.
    /// </summary>
    public async Task<int> EnsureInboxTransaccionAsync(CancellationToken ct = default)
    {
      var idStr = _cfg["SatXml:PlaceholderTransaccionId"];
      if (!int.TryParse(idStr, out var id)) id = 5505;

      const string sql = "SELECT 1 FROM dbo.Transacciones WHERE ID = @ID;";
      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      var exists = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(sql, new { ID = id }, cancellationToken: ct));

      if (!exists.HasValue)
        throw new InvalidOperationException($"Configured placeholder Transacciones.ID={id} not found. Create it or change SatXml:PlaceholderTransaccionId.");

      return id;
    }

    /// <summary>
    /// Saves and processes the XML file:
    /// - If UUID is already linked to a Transacción, ensures/fetches an XML attachment under that Transacción,
    ///   then calls dbo.PROCESAR_SAT_XML_V2 with (TransaccionID=that, AttachmentID=found/new).
    /// - Otherwise, inserts under the placeholder Transacción and processes there.
    /// </summary>
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

      var placeholderId = await EnsureInboxTransaccionAsync(ct); // e.g., 5505 (from config)

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
        // --- Branch A: UUID already exists & linked to a Transacción ---
        if (!string.IsNullOrWhiteSpace(uuidCandidate))
        {
          var (found, _comprobanteId, transaccionId) =
              await TryResolveExistingLinkedAsync(conn, tx, uuidCandidate, ct);

          if (found && transaccionId.HasValue)
          {
            // Ensure/fetch the attachment for the already-linked Transacción
            var (attachmentId, createdNew) = await EnsureXmlAttachmentOnTranAsync(
              conn, tx, transaccionId.Value, fileName, bytes, ct);

            // Re-process using the existing Transacción and the located/created AttachmentID
            const string spReprocess = "cfdi.PROCESAR_SAT_XML_V2";
            await conn.ExecuteAsync(
              new CommandDefinition(
                spReprocess,
                new { TransaccionID = transaccionId.Value, AttachmentID = attachmentId },
                commandType: CommandType.StoredProcedure,
                transaction: tx,
                cancellationToken: ct));

            await tx!.CommitAsync(ct);
            var msg = createdNew
              ? $"Reprocesado (TranID={transaccionId}). Se agregó el XML como adjunto (AttachmentID={attachmentId})."
              : $"Reprocesado (TranID={transaccionId}). Se reutilizó el adjunto existente (AttachmentID={attachmentId}).";

            return new SatXmlProcessResult(fileName, attachmentId, true, msg);
          }
        }

      

        const string insertAttach = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() as int);";

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".xml";

        var placeholderAttachmentId = await conn.ExecuteScalarAsync<int>(
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
                tx, cancellationToken: ct));

        // Parse/process into SAT tables
        const string sp = "cfdi.PROCESAR_SAT_XML_V2";
        await conn.ExecuteAsync(
            new CommandDefinition(
                sp,
                new { TransaccionID = placeholderId, AttachmentID = placeholderAttachmentId },
                commandType: CommandType.StoredProcedure,
                transaction: tx,
                cancellationToken: ct));

        await tx!.CommitAsync(ct);
        return new SatXmlProcessResult(fileName, placeholderAttachmentId, true, null);
      }
      catch (Exception ex)
      {
        try { await tx!.RollbackAsync(ct); } catch { /* ignore */ }
        _logger.LogError(ex, "SaveAndProcessAsync failed for file {File}", fileName);
        return new SatXmlProcessResult(fileName, 0, false, $"Error: {ex.Message}");
      }
    }

    /// <summary>
    /// Check if UUID already exists and if it is linked to a Transacción.
    /// </summary>
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
          new CommandDefinition(sql, new { Uuid = uuid }, tx, cancellationToken: ct));

      return row.ComprobanteId == 0
          ? (false, 0, null)
          : (true, row.ComprobanteId, row.TransaccionId);
    }

    /// <summary>
    /// Ensures an XML attachment exists on the given Transacción.
    /// If an exact-name (case-insensitive) match exists (or a LIKE '%nameNoExt%'), returns its ID.
    /// Otherwise inserts a new attachment and returns the new ID.
    /// </summary>
    private async Task<(int AttachmentId, bool CreatedNew)> EnsureXmlAttachmentOnTranAsync(
      SqlConnection conn, SqlTransaction? tx, int tranId, string fileName, byte[] bytes, CancellationToken ct)
    {
      var fileNameNoExt = Path.GetFileNameWithoutExtension(fileName);

      const string findSql = @"
SELECT TOP(1) ID
FROM dbo.TRANSACTION_ATTACHMENT
WHERE TranID = @TranID
  AND AttachmentExtension = 'xml'
  AND (LOWER(AttachmentName) = LOWER(@AttachmentName) OR AttachmentName LIKE @LikeName)
ORDER BY CASE WHEN LOWER(AttachmentName) = LOWER(@AttachmentName) THEN 0 ELSE 1 END, ID DESC;";

      var existingId = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
          findSql,
          new { TranID = tranId, AttachmentName = fileName, LikeName = AttachmentLikePattern(fileNameNoExt) },
          tx, cancellationToken: ct));

      if (existingId.HasValue)
      {
        return (existingId.Value, false);
      }

      const string insertSql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, 'xml', @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() as int);";

      var newId = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          insertSql,
          new
          {
            TranID = tranId,
            Attachment = bytes,
            AttachmentName = fileName,
            AttachmentDescription = "SAT XML upload (linked existing)"
          },
          tx, cancellationToken: ct));

      return (newId, true);
    }
  
  }
}
