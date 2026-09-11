using System;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class BonhomiaGraphCalendarRemoteEvent
{
  public string Id { get; set; } = string.Empty;
  public string Subject { get; set; } = string.Empty;
  public string? BodyHtml { get; set; }
  public DateTime StartDate { get; set; }
  public DateTime EndDateExclusive { get; set; }
  public bool IsAllDay { get; set; }
  public string ShowAs { get; set; } = string.Empty;
  public string? SourceKey { get; set; }
}
