using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class BonhomiaGraphCalendarSyncOptions
{
  public const string SectionName = "BonhomiaGraphCalendarSync";

  private static readonly string[] DefaultCalendars =
  {
    "BERLIN",
    "MANHATTAN",
    "SEUL",
    "PARIS",
    "MOSCU",
    "PENTHOUSE",
    "LONDON",
    "GRECIA"
  };

  public string TenantId { get; set; } = string.Empty;
  public string ClientId { get; set; } = string.Empty;
  public string ClientSecret { get; set; } = string.Empty;
  public string MailboxAddress { get; set; } = "recepcion@bonhomiasuites.com";
  public string TimeZone { get; set; } = "America/Mexico_City";
  public List<string> TargetCalendars { get; set; } = DefaultCalendars.ToList();

  public IReadOnlyList<string> GetTargetCalendars()
  {
    var values = TargetCalendars
      .Where(item => !string.IsNullOrWhiteSpace(item))
      .Select(item => item.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    return values.Length > 0 ? values : DefaultCalendars;
  }
}
