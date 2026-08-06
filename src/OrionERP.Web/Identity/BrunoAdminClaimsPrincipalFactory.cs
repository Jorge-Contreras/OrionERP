using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public sealed class BrunoAdminClaimsPrincipalFactory
  : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
  private readonly UserManager<ApplicationUser> _userManager;
  private readonly IConfiguration _configuration;

  public BrunoAdminClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor,
    IConfiguration configuration)
    : base(userManager, roleManager, optionsAccessor)
  {
    _userManager = userManager;
    _configuration = configuration;
  }

  protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
  {
    var identity = await base.GenerateClaimsAsync(user);
    if (user.EmployeeId.HasValue)
    {
      identity.AddClaim(new Claim("employee_id", user.EmployeeId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
      var connectionString = _configuration.GetConnectionString("OrionDb");
      if (!string.IsNullOrWhiteSpace(connectionString))
      {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULLIF(LTRIM(RTRIM(RFC)),'') FROM dbo.Capital_Humano WHERE ID=@EmployeeId;";
        command.Parameters.AddWithValue("@EmployeeId", user.EmployeeId.Value);
        var employeeRfc = Convert.ToString(await command.ExecuteScalarAsync())?.Trim();
        if (!string.IsNullOrWhiteSpace(employeeRfc))
        {
          if (!identity.HasClaim("rfc", employeeRfc)) identity.AddClaim(new Claim("rfc", employeeRfc));
          identity.AddClaim(new Claim("employee_rfc", employeeRfc));
        }
      }
    }
    var canAdministerBruno =
      await _userManager.IsInRoleAsync(user, "Administrador") ||
      await _userManager.IsInRoleAsync(user, "RestauranteAdmin");

    if (canAdministerBruno &&
        !identity.HasClaim("rfc", BrunoRestaurantConstants.Rfc))
    {
      identity.AddClaim(new Claim("rfc", BrunoRestaurantConstants.Rfc));
    }

    return identity;
  }
}
