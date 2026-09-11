using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Infrastructure.Features.Reservaciones.CalendarSync;

public sealed class HospitalityCalendarSyncOptions
{
  public const string SectionName = "HospitalityIntegrations:Calendar";
  public const string LegacySectionName = "BonhomiaGraphCalendarSync";

  // Disabled until an operator binds this integration to a verified company/site.
  public bool Enabled { get; set; }
  public long CompanyId { get; set; }
  public long SiteId { get; set; }
  public string CompanyRfc { get; set; } = string.Empty;

  public string TenantId { get; set; } = string.Empty;
  public string ClientId { get; set; } = string.Empty;
  public string ClientSecret { get; set; } = string.Empty;
  public string MailboxAddress { get; set; } = string.Empty;
  public string TimeZone { get; set; } = "America/Mexico_City";
  public List<string> TargetCalendars { get; set; } = new();

  public void ApplySharedGraphCredentials(string? tenantId, string? clientId, string? clientSecret)
  {
    if (string.IsNullOrWhiteSpace(tenantId) ||
        string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
      return;
    }

    // Shared secret rotation is allowed only after the calendar integration
    // explicitly identifies the same tenant and application. Empty calendar
    // configuration must never adopt the shared mail identity implicitly.
    if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId) ||
        !string.Equals(TenantId.Trim(), tenantId.Trim(), StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(ClientId.Trim(), clientId.Trim(), StringComparison.OrdinalIgnoreCase))
    {
      return;
    }

    TenantId = tenantId.Trim();
    ClientId = clientId.Trim();
    ClientSecret = clientSecret.Trim();
  }

  public IReadOnlyList<string> GetTargetCalendars()
  {
    var values = (TargetCalendars ?? new List<string>())
      .Where(item => !string.IsNullOrWhiteSpace(item))
      .Select(item => item.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToArray();

    return values;
  }
}
