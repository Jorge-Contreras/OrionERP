using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationCreateResult
{
  public int ReservationId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public string Status { get; set; } = string.Empty;
  public bool Taxable { get; set; }
  public IReadOnlyList<string> SuiteNames { get; set; } = Array.Empty<string>();
  public IReadOnlyList<OpenClawReservationCreatedExtra> Extras { get; set; } = Array.Empty<OpenClawReservationCreatedExtra>();
  public decimal SuiteSubtotal { get; set; }
  public decimal ExtrasSubtotal { get; set; }
  public decimal TotalPrice { get; set; }
}

public sealed class OpenClawReservationCreatedExtra
{
  public string CatalogName { get; set; } = string.Empty;
  public int Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal LinePrice { get; set; }
  public string? Notes { get; set; }
}
