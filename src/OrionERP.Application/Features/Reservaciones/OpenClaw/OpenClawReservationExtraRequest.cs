namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationExtraRequest
{
  public string CatalogName { get; set; } = string.Empty;
  public int Quantity { get; set; }
  public string? Notes { get; set; }
}
