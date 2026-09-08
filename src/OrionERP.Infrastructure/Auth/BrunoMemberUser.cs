using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth;

public sealed class BrunoMemberUser : IdentityUser
{
  /// <summary>
  /// Immutable binding to the database-verified public restaurant instance.
  /// orion.PublicSite in turn fixes the company, site and module.
  /// </summary>
  public long PublicSiteId { get; set; }
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime? ClosedAt { get; set; }
}
