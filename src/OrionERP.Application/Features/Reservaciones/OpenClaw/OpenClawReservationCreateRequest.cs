using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationCreateRequest
{
  public string ClientName { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public IReadOnlyList<string> SuiteNames { get; set; } = Array.Empty<string>();
  public decimal? GeneralDiscountPercent { get; set; }
  public IReadOnlyList<OpenClawReservationExtraRequest> Extras { get; set; } = Array.Empty<OpenClawReservationExtraRequest>();
  public string? Status { get; set; }
  public bool? RequiresCfdi { get; set; }
  public bool? Taxable { get; set; }
  public string? RecommendedBy { get; set; }
  public string? ReservationNotes { get; set; }
}
