namespace OrionERP.Application.Features.Logistica.Shared;

public sealed class LogisticsCommandResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public int? EntityId { get; set; }

  public static LogisticsCommandResult Ok(string message, int? entityId = null)
    => new()
    {
      Success = true,
      Message = message,
      EntityId = entityId
    };

  public static LogisticsCommandResult Fail(string message, int? entityId = null)
    => new()
    {
      Success = false,
      Message = message,
      EntityId = entityId
    };
}
