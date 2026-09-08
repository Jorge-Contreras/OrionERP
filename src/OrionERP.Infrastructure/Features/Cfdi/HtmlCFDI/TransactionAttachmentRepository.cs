using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

namespace OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;

public sealed class TransactionAttachmentRepository : ITransactionAttachmentRepository
{
  private readonly SqlConnectionFactory _connectionFactory;
  private readonly ICurrentCompanyContext _companyContext;

  public TransactionAttachmentRepository(SqlConnectionFactory connectionFactory, ICurrentCompanyContext companyContext)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _companyContext = companyContext ?? throw new ArgumentNullException(nameof(companyContext));
  }

  public async Task<TransactionAttachment?> GetAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    var rfc = _companyContext.RequireRfc();
    const string sql = @"SELECT TOP (1)
    ta.ID                  AS Id,
    ta.TranID              AS TranId,
    ta.AttachmentName      AS AttachmentName,
    ta.AttachmentExtension AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    ta.Attachment          AS Content
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId
  AND
  (
    EXISTS (SELECT 1 FROM dbo.Transacciones t WHERE t.ID = ta.TranID AND t.RFC = @Rfc)
    OR (ta.TranID IS NULL AND EXISTS
    (
      SELECT 1 FROM cfdi.Comprobante c
      WHERE c.XML_Attachment_ID = ta.ID
        AND
        (
          EXISTS (SELECT 1 FROM cfdi.Emisor e WHERE e.Comprobante_ID = c.Comprobante_Id AND e.Rfc = @Rfc)
          OR EXISTS (SELECT 1 FROM cfdi.Receptor r WHERE r.Comprobante_ID = c.Comprobante_Id AND r.Rfc = @Rfc)
        )
    ))
  );";

    using var conn = _connectionFactory.Create();
    var command = new CommandDefinition(sql, new { AttachmentId = attachmentId, Rfc = rfc }, cancellationToken: ct);
    return await conn.QueryFirstOrDefaultAsync<TransactionAttachment>(command);
  }
}
