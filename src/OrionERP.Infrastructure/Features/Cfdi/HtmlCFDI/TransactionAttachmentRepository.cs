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
  private readonly ICurrentRfcAccessor _rfcAccessor;

  public TransactionAttachmentRepository(SqlConnectionFactory connectionFactory, ICurrentRfcAccessor rfcAccessor)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _rfcAccessor = rfcAccessor ?? throw new ArgumentNullException(nameof(rfcAccessor));
  }

  public async Task<TransactionAttachment?> GetAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    var currentRfc = _rfcAccessor.CurrentRfc;
    if (string.IsNullOrWhiteSpace(currentRfc))
      return null;

    const string sql = @"SELECT TOP (1)
    ta.ID                  AS Id,
    ta.TranID              AS TranId,
    ta.AttachmentName      AS AttachmentName,
    ta.AttachmentExtension AS AttachmentExtension,
    ta.Attachment          AS Content
FROM dbo.TRANSACTION_ATTACHMENT ta
INNER JOIN dbo.Transacciones t ON t.ID = ta.TranID
WHERE ta.ID = @AttachmentId
  AND t.RFC = @Rfc;";

    using var conn = _connectionFactory.Create();
    var command = new CommandDefinition(sql, new { AttachmentId = attachmentId, Rfc = currentRfc }, cancellationToken: ct);
    return await conn.QueryFirstOrDefaultAsync<TransactionAttachment>(command);
  }
}
