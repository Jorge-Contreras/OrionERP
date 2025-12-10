using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Infrastructure.Features.Cfdi.DescargaMasiva.Dapper;

namespace OrionERP.Infrastructure.Features.Cfdi.HtmlCFDI;

public sealed class TransactionAttachmentRepository : ITransactionAttachmentRepository
{
  private readonly SqlConnectionFactory _connectionFactory;

  public TransactionAttachmentRepository(SqlConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<TransactionAttachment?> GetAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    const string sql = @"SELECT TOP (1)
    ta.ID                  AS Id,
    ta.TranID              AS TranId,
    ta.AttachmentName      AS AttachmentName,
    ta.AttachmentExtension AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    ta.Attachment          AS Content
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId;";

    using var conn = _connectionFactory.Create();
    var command = new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct);
    return await conn.QueryFirstOrDefaultAsync<TransactionAttachment>(command);
  }
}
