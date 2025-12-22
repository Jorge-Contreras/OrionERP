using Dapper;
using Microsoft.Data.SqlClient;           // ✅ correct provider
using Microsoft.Extensions.Configuration; // ✅ IConfiguration
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts;
using System;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
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

    // Strict UUID formats: 8-4-4-4-12 hex OR 32 hex
    private static readonly Regex _uuidRegex = new(
      @"^(?:[0-9A-Fa-f]{8}\-[0-9A-Fa-f]{4}\-[0-9A-Fa-f]{4}\-[0-9A-Fa-f]{4}\-[0-9A-Fa-f]{12}|[0-9A-Fa-f]{32})$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool IsUuidLike(string? s)
      => !string.IsNullOrWhiteSpace(s) && _uuidRegex.IsMatch(s.Trim());

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
        XmlResolver = null,
        CloseInput = true
      };

      try
      {
        using var ms = new MemoryStream(bytes, writable: false);
        using var xr = XmlReader.Create(ms, settings);

        while (xr.Read())
        {
          if (xr.NodeType == XmlNodeType.Element &&
              string.Equals(xr.LocalName, "TimbreFiscalDigital", StringComparison.OrdinalIgnoreCase))
          {
            var uuid = xr.GetAttribute("UUID");
            if (!string.IsNullOrWhiteSpace(uuid))
              return NormalizeUuid(uuid);

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
        // Swallow parse errors; caller will decide fallback behavior
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

    private static string SafeFileName(string fileName)
      => Path.GetFileName(fileName)?.Trim() ?? string.Empty;

    private static string UuidFromFileName(string fileName)
      => (Path.GetFileNameWithoutExtension(fileName) ?? string.Empty).Trim();

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
        throw new InvalidOperationException(
          $"Configured placeholder Transacciones.ID={id} not found. Create it or change SatXml:PlaceholderTransaccionId.");

      return id;
    }

    /// <summary>
    /// Resolve by UUID: returns Comprobante_Id, "best" Transaccion_Id (prefers non-placeholder), and Comprobante.XML_Attachment_ID.
    /// This is fast in your DB because you have UX_TFD_UUID on cfdi.TimbreFiscalDigital(UUID).
    /// </summary>
    private static async Task<(bool found, int comprobanteId, int? transaccionId, int? xmlAttachmentId)> TryResolveExistingLinkedAsync(
      SqlConnection conn, SqlTransaction tx, string uuid, int placeholderTranId, CancellationToken ct)
    {
      const string sql = @"
SELECT TOP (1)
    t.Comprobante_Id     AS ComprobanteId,
    tc.Transaccion_ID    AS TransaccionId,
    c.XML_Attachment_ID  AS XmlAttachmentId
FROM cfdi.TimbreFiscalDigital t
JOIN cfdi.Comprobante c
  ON c.Comprobante_Id = t.Comprobante_Id
LEFT JOIN dbo.Transaccion_Comprobante tc
  ON tc.Comprobante_ID = t.Comprobante_Id
WHERE t.UUID = @Uuid
ORDER BY
  CASE
    WHEN tc.Transaccion_ID IS NULL THEN 2
    WHEN tc.Transaccion_ID = @PlaceholderTranId THEN 1
    ELSE 0
  END,
  tc.ID DESC;";

      var row = await conn.QueryFirstOrDefaultAsync<(int ComprobanteId, int? TransaccionId, int? XmlAttachmentId)>(
        new CommandDefinition(sql, new { Uuid = uuid, PlaceholderTranId = placeholderTranId }, tx, cancellationToken: ct));

      return row.ComprobanteId == 0
        ? (false, 0, null, null)
        : (true, row.ComprobanteId, row.TransaccionId, row.XmlAttachmentId);
    }

    private static async Task<int> InsertXmlAttachmentAsync(
      SqlConnection conn, SqlTransaction tx, int tranId, string fileName, byte[] bytes, string description, CancellationToken ct)
    {
      const string insertSql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, 'xml', @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() as int);";

      return await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          insertSql,
          new
          {
            TranID = tranId,
            Attachment = bytes,
            AttachmentName = fileName,
            AttachmentDescription = description
          },
          tx,
          cancellationToken: ct));
    }

    /// <summary>
    /// Updates an existing attachment row by ID (fast PK seek). Returns true if updated; false if the row does not exist.
    /// </summary>
    private static async Task<bool> UpdateXmlAttachmentByIdAsync(
      SqlConnection conn, SqlTransaction tx, int attachmentId, int tranId, string fileName, byte[] bytes, string description, CancellationToken ct)
    {
      const string sql = @"
UPDATE dbo.TRANSACTION_ATTACHMENT
SET
  TranID = @TranID,
  Attachment = @Attachment,
  AttachmentName = @AttachmentName,
  AttachmentExtension = 'xml',
  AttachmentDescription = @AttachmentDescription
WHERE ID = @ID;";

      var rows = await conn.ExecuteAsync(
        new CommandDefinition(
          sql,
          new
          {
            ID = attachmentId,
            TranID = tranId,
            Attachment = bytes,
            AttachmentName = fileName,
            AttachmentDescription = description
          },
          tx,
          cancellationToken: ct));

      return rows > 0;
    }

    private static Task CallProcesarXmlAsync(SqlConnection conn, SqlTransaction tx, int transaccionId, int attachmentId, CancellationToken ct)
    {
      const string sp = "cfdi.PROCESAR_SAT_XML_V2";
      return conn.ExecuteAsync(
        new CommandDefinition(
          sp,
          new { TransaccionID = transaccionId, AttachmentID = attachmentId },
          commandType: CommandType.StoredProcedure,
          transaction: tx,
          cancellationToken: ct));
    }

    public async Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default)
    {
      if (xmlStream is null) throw new ArgumentNullException(nameof(xmlStream));
      if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("fileName is required.", nameof(fileName));

      // Read bytes up-front
      byte[] bytes;
      using (var ms = new MemoryStream())
      {
        await xmlStream.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
      }

      var safeName = SafeFileName(fileName);

      // Prefer UUID extracted from XML; only fallback to filename if it looks like a UUID.
      var uuidFromXml = TryExtractUuidFromXml(bytes);
      var uuidCandidate = !string.IsNullOrWhiteSpace(uuidFromXml)
        ? NormalizeUuid(uuidFromXml)
        : (IsUuidLike(UuidFromFileName(safeName)) ? NormalizeUuid(UuidFromFileName(safeName)) : string.Empty);

      var placeholderId = await EnsureInboxTransaccionAsync(ct);

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);
      using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

      try
      {
        // Branch A: UUID exists AND is already linked to a non-placeholder Transacción
        if (!string.IsNullOrWhiteSpace(uuidCandidate))
        {
          var (found, comprobanteId, transaccionId, xmlAttachmentId) =
            await TryResolveExistingLinkedAsync(conn, tx, uuidCandidate, placeholderId, ct);

          if (found && transaccionId.HasValue && transaccionId.Value != placeholderId)
          {
            var targetTranId = transaccionId.Value;

            int attachmentId;
            bool createdNew;

            // Use XML_Attachment_ID only (no name search). If missing, create a new attachment and fix the pointer.
            if (xmlAttachmentId.HasValue)
            {
              var updated = await UpdateXmlAttachmentByIdAsync(
                conn, tx, xmlAttachmentId.Value, targetTranId, safeName, bytes,
                "SAT XML upload (reprocess / reuse XML_Attachment_ID)",
                ct);

              if (updated)
              {
                attachmentId = xmlAttachmentId.Value;
                createdNew = false;
              }
              else
              {
                // Pointer is stale/broken: insert new and update Comprobante.XML_Attachment_ID
                attachmentId = await InsertXmlAttachmentAsync(
                  conn, tx, targetTranId, safeName, bytes,
                  "SAT XML upload (reprocess / XML_Attachment_ID was broken)",
                  ct);

                createdNew = true;

                await conn.ExecuteAsync(
                  new CommandDefinition(
                    "UPDATE cfdi.Comprobante SET XML_Attachment_ID = @AttachmentID WHERE Comprobante_Id = @ComprobanteID;",
                    new { AttachmentID = attachmentId, ComprobanteID = comprobanteId },
                    tx,
                    cancellationToken: ct));
              }
            }
            else
            {
              attachmentId = await InsertXmlAttachmentAsync(
                conn, tx, targetTranId, safeName, bytes,
                "SAT XML upload (reprocess / XML_Attachment_ID was NULL)",
                ct);

              createdNew = true;

              // Set pointer now so future processing is deterministic
              await conn.ExecuteAsync(
                new CommandDefinition(
                  "UPDATE cfdi.Comprobante SET XML_Attachment_ID = @AttachmentID WHERE Comprobante_Id = @ComprobanteID;",
                  new { AttachmentID = attachmentId, ComprobanteID = comprobanteId },
                  tx,
                  cancellationToken: ct));
            }

            await CallProcesarXmlAsync(conn, tx, targetTranId, attachmentId, ct);
            await tx.CommitAsync(ct);

            var msg = createdNew
              ? $"Reprocesado (TranID={targetTranId}). Se agregó el XML como adjunto (AttachmentID={attachmentId})."
              : $"Reprocesado (TranID={targetTranId}). Se reutilizó XML_Attachment_ID (AttachmentID={attachmentId}).";

            return new SatXmlProcessResult(safeName, attachmentId, true, msg);
          }
        }

        // Branch B: placeholder (no UUID, UUID not found, or not linked to a non-placeholder transacción)
        var placeholderAttachmentId = await InsertXmlAttachmentAsync(
          conn, tx, placeholderId, safeName, bytes, "SAT XML upload", ct);

        await CallProcesarXmlAsync(conn, tx, placeholderId, placeholderAttachmentId, ct);
        await tx.CommitAsync(ct);

        return new SatXmlProcessResult(safeName, placeholderAttachmentId, true, null);
      }
      catch (Exception ex)
      {
        try { await tx.RollbackAsync(ct); } catch { /* ignore */ }
        _logger.LogError(ex, "SaveAndProcessAsync failed for file {File}", fileName);
        return new SatXmlProcessResult(fileName, 0, false, $"Error: {ex.Message}");
      }
    }
  }
}
