using OrionERP.Application.Common;

namespace OrionERP.UnitTests.Common;

/// <summary>
/// Para pruebas que sólo ejercitan el RFC de la sesión. Si alguna llega a pedir la
/// identidad técnica, falla en vez de inventar una empresa.
/// </summary>
internal sealed class StubCompanyIdentityResolver(long? companyId = null) : ICompanyIdentityResolver
{
  public Task<long?> ResolveCompanyIdAsync(string legacyRfc, CancellationToken ct = default)
    => companyId is null
      ? throw new InvalidOperationException("Esta prueba no debería resolver la identidad técnica de la empresa.")
      : Task.FromResult(companyId);
}
