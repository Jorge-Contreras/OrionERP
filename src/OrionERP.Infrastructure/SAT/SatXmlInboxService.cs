using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;           // ✅ correct provider
using Microsoft.Extensions.Configuration; // ✅ IConfiguration
using Microsoft.Extensions.Logging;
using OrionERP.Application.SAT;       // ✅ ILogger

namespace OrionERP.Infrastructure.SAT
{
  public sealed class SatXmlInboxService : ISatXmlInboxService
  {
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

    public async Task<SatXmlProcessResult> SaveAndProcessAsync(Stream xmlStream, string fileName, CancellationToken ct = default)
    {
      var placeholderId = await EnsureInboxTransaccionAsync(ct);

      // Read bytes
      byte[] bytes;
      using (var ms = new MemoryStream())
      {
        await xmlStream.CopyToAsync(ms, ct);
        bytes = ms.ToArray();
      }

      var ext = Path.GetExtension(fileName);
      if (string.IsNullOrWhiteSpace(ext)) ext = ".xml";

      const string insertAttach = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() as int);";

      using var conn = new SqlConnection(_cs);
      await conn.OpenAsync(ct);

      var attachmentId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
              insertAttach,
              new
              {
                TranID = placeholderId,               // <-- always 5505 (from config)
                Attachment = bytes,
                AttachmentName = fileName,
                AttachmentExtension = ext.TrimStart('.'),
                AttachmentDescription = "SAT XML upload"
              },
              commandType: CommandType.Text,
              cancellationToken: ct
          )
      );

      // Call stored procedure
      const string sp = "dbo.PROCESAR_SAT_XML";
      try
      {
        await conn.ExecuteAsync(
            new CommandDefinition(
                sp,
                new { TransaccionID = placeholderId, AttachmentID = attachmentId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct
            )
        );

        return new SatXmlProcessResult(fileName, attachmentId, true, null);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "SP PROCESAR_SAT_XML failed for attachment {AttachmentId}", attachmentId);
        return new SatXmlProcessResult(fileName, attachmentId, false, $"SP error: {ex.Message}");
      }
    }
  }

}
