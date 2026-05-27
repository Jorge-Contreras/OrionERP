using System;
using System.Collections.Generic;
using OrionERP.Application.Features.Reservaciones.OpenClaw;

namespace OrionERP.Web.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationCreateResponse
{
  public int ReservationId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public string Status { get; set; } = string.Empty;
  public bool RequiresCfdi { get; set; }
  public bool Taxable { get; set; }
  public IReadOnlyList<string> SuiteNames { get; set; } = Array.Empty<string>();
  public IReadOnlyList<OpenClawReservationCreatedExtra> Extras { get; set; } = Array.Empty<OpenClawReservationCreatedExtra>();
  public decimal SuiteSubtotal { get; set; }
  public decimal ExtrasSubtotal { get; set; }
  public decimal TotalPrice { get; set; }
  public string PdfUrl { get; set; } = string.Empty;
}
