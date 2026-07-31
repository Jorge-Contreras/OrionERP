using Microsoft.AspNetCore.Identity;

namespace OrionERP.Infrastructure.Auth;

public sealed class BrunoMemberUser : IdentityUser
{
  public string FirstName { get; set; } = string.Empty;
  public string LastName { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime? ClosedAt { get; set; }
}
