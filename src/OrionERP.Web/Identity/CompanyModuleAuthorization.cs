using Microsoft.AspNetCore.Authorization;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.Identity;

/// <summary>
/// Exige que la empresa de la sesión tenga habilitado un módulo con al menos una sede
/// utilizable. Complementa a los roles, no los sustituye: sin el módulo, ningún rol abre
/// la página, y el usuario ve que el módulo no está habilitado en vez de un error.
/// </summary>
public sealed class CompanyModuleRequirement : IAuthorizationRequirement
{
  public CompanyModuleRequirement(string moduleCode)
  {
    ModuleCode = string.IsNullOrWhiteSpace(moduleCode)
      ? throw new ArgumentException("A module code is required.", nameof(moduleCode))
      : moduleCode.Trim().ToUpperInvariant();
  }

  public string ModuleCode { get; }
}

public static class CompanyModuleAuthorizationExtensions
{
  public static AuthorizationPolicyBuilder RequireCompanyModule(
    this AuthorizationPolicyBuilder policy,
    string moduleCode)
  {
    policy.RequireCompanySession();
    policy.AddRequirements(new CompanyModuleRequirement(moduleCode));
    return policy;
  }
}

/// <summary>
/// Lee el RFC de los claims y no del circuito de Blazor, así que protege igual las páginas
/// y los endpoints HTTP (puente QZ, evidencia de merma, imágenes de productos).
/// </summary>
public sealed class CompanyModuleAuthorizationHandler(ICompanyModuleAccess modules)
  : AuthorizationHandler<CompanyModuleRequirement>
{
  protected override async Task HandleRequirementAsync(
    AuthorizationHandlerContext context,
    CompanyModuleRequirement requirement)
  {
    var rfcs = context.User.FindAll(CompanyClaimTypes.Rfc)
      .Select(claim => claim.Value.Trim().ToUpperInvariant())
      .Where(value => value.Length > 0)
      .Distinct(StringComparer.Ordinal)
      .ToArray();
    if (rfcs.Length != 1)
      return;

    var enabled = await modules.GetEnabledModulesForRfcAsync(rfcs[0]);
    if (enabled.Contains(requirement.ModuleCode))
      context.Succeed(requirement);
  }
}
