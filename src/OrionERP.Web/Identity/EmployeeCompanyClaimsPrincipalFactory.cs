using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

public sealed class EmployeeCompanyClaimsPrincipalFactory
  : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
  private readonly IConfiguration _configuration;

  public EmployeeCompanyClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor,
    IConfiguration configuration)
    : base(userManager, roleManager, optionsAccessor)
  {
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
        command.CommandText = """
          SELECT company.CompanyId,company.Rfc
          FROM dbo.Capital_Humano employee
          JOIN orion.Company company ON company.Rfc=NULLIF(LTRIM(RTRIM(employee.RFC)),'')
          WHERE employee.ID=@EmployeeId AND company.IsActive=1;
          """;
        command.Parameters.AddWithValue("@EmployeeId", user.EmployeeId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
          var companyId = reader.GetInt64(0);
          var employeeRfc = reader.GetString(1).Trim();
          if (!identity.HasClaim("rfc", employeeRfc)) identity.AddClaim(new Claim("rfc", employeeRfc));
          identity.AddClaim(new Claim("employee_rfc", employeeRfc));
          identity.AddClaim(new Claim("company_id", companyId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
      }
    }
    return identity;
  }
}
