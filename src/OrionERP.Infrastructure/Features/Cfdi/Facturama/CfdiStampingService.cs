using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Cfdi.Facturama;

namespace OrionERP.Infrastructure.Features.Cfdi.Facturama;

public interface ICfdiStampingService
{
  Task<CfdiStampResult> StampIssuedCfdiAsync(CfdiStampRequest request, CancellationToken ct = default);
}

public sealed class CfdiStampRequest
{
  public int TransaccionId { get; set; }
  public string AttachmentLabel { get; set; } = string.Empty;
  public FacturamaIssuedCfdiRequest Payload { get; set; } = new();
}

public sealed class CfdiStampResult
{
  public string FacturamaCfdiId { get; set; } = string.Empty;
  public int? ComprobanteId { get; set; }
}

public sealed class CfdiStampingException : Exception
{
  public CfdiStampingException(string message, string? facturamaCfdiId, Exception innerException)
      : base(message, innerException)
  {
    FacturamaCfdiId = facturamaCfdiId;
  }

  public string? FacturamaCfdiId { get; }
}

public sealed class CfdiStampingService : ICfdiStampingService
{
  private readonly string _connectionString;
  private readonly IFacturamaApiClient _facturamaApiClient;
  private readonly ILogger<CfdiStampingService> _logger;

  public CfdiStampingService(
      IConfiguration configuration,
      IFacturamaApiClient facturamaApiClient,
      ILogger<CfdiStampingService> logger)
  {
    ArgumentNullException.ThrowIfNull(configuration);
    _connectionString = configuration.GetConnectionString("OrionDb")
        ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _facturamaApiClient = facturamaApiClient ?? throw new ArgumentNullException(nameof(facturamaApiClient));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public async Task<CfdiStampResult> StampIssuedCfdiAsync(CfdiStampRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);

    if (request.TransaccionId <= 0)
      throw new ArgumentOutOfRangeException(nameof(request), "TransaccionId must be greater than zero.");

    if (request.Payload is null)
      throw new ArgumentException("Facturama payload is required.", nameof(request));

    var attachmentLabel = string.IsNullOrWhiteSpace(request.AttachmentLabel)
      ? $"POLIZA {request.TransaccionId}"
      : request.AttachmentLabel.Trim();

    string? facturamaCfdiId = null;

    try
    {
      facturamaCfdiId = await _facturamaApiClient.CreateIssuedCfdiAsync(request.Payload, ct);

      var xmlDocument = await _facturamaApiClient.DownloadIssuedDocumentAsync(
          facturamaCfdiId,
          FacturamaIssuedDocumentType.Xml,
          ct);
      var pdfDocument = await _facturamaApiClient.DownloadIssuedDocumentAsync(
          facturamaCfdiId,
          FacturamaIssuedDocumentType.Pdf,
          ct);

      await using var writeConn = new SqlConnection(_connectionString);
      await writeConn.OpenAsync(ct);
      await using var tx = (SqlTransaction)await writeConn.BeginTransactionAsync(ct);

      try
      {
        var xmlAttachmentId = await InsertAttachmentAsync(
            writeConn,
            tx,
            request.TransaccionId,
            $"XML {attachmentLabel}",
            xmlDocument.Extension,
            $"XML {attachmentLabel}",
            xmlDocument.Bytes,
            ct);

        var comprobanteId = await ProcessSatXmlV2Async(writeConn, tx, request.TransaccionId, xmlAttachmentId, ct);

        await InsertAttachmentAsync(
            writeConn,
            tx,
            request.TransaccionId,
            $"PDF {attachmentLabel}",
            pdfDocument.Extension,
            $"PDF {attachmentLabel}",
            pdfDocument.Bytes,
            ct);

        await MarkTransactionAsFacturadoAsync(writeConn, tx, request.TransaccionId, ct);
        await tx.CommitAsync(ct);

        return new CfdiStampResult
        {
          FacturamaCfdiId = facturamaCfdiId,
          ComprobanteId = comprobanteId
        };
      }
      catch
      {
        await tx.RollbackAsync(ct);
        throw;
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to stamp/process CFDI for transaction {TransaccionId}",
          request.TransaccionId);

      throw new CfdiStampingException(
          "The CFDI could not be fully stamped and persisted locally.",
          facturamaCfdiId,
          ex);
    }
  }

  private static async Task<int> InsertAttachmentAsync(
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

  private static async Task MarkTransactionAsFacturadoAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      CancellationToken ct)
  {
    const string sql = """
UPDATE dbo.Transacciones
SET Facturado = 1
WHERE ID = @TransaccionId;
""";

    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new { TransaccionId = transaccionId },
            transaction,
            cancellationToken: ct));
  }
}
