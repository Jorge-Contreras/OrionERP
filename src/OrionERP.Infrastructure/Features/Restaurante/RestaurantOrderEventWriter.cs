using System.Data.Common;
using Dapper;

namespace OrionERP.Infrastructure.Features.Restaurante;

internal static class RestaurantOrderEventWriter
{
  public static Task AddAsync(
    DbConnection connection,
    DbTransaction? transaction,
    string rfc,
    int siteId,
    Guid orderId,
    string eventType,
    string category,
    string title,
    string? description,
    string? actor,
    CancellationToken ct,
    string? sourceKey = null)
    => connection.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO restaurante.OrderEvent
        (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey)
      VALUES
        (@Rfc,@SiteId,@OrderId,@EventType,@Category,@Title,@Description,@Actor,@SourceKey);
      """,
      new
      {
        Rfc = rfc,
        SiteId = siteId,
        OrderId = orderId,
        EventType = Limit(eventType, 80),
        Category = Limit(category, 30),
        Title = Limit(title, 180),
        Description = LimitNullable(description, 1200),
        Actor = LimitNullable(actor, 256),
        SourceKey = LimitNullable(sourceKey, 180)
      },
      transaction,
      cancellationToken: ct));

  private static string Limit(string value, int maximumLength)
  {
    var normalized = value.Trim();
    return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
  }

  private static string? LimitNullable(string? value, int maximumLength)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var normalized = value.Trim();
    return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
  }
}
