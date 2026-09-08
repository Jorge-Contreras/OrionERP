namespace OrionERP.Application.Common;

/// <summary>
/// Traduce la clave legacy de empresa —el RFC con el que operan las tablas
/// heredadas— al <c>CompanyId</c> técnico de <c>orion.Company</c>, que es su clave
/// primaria. El vínculo es exacto: no se infiere desde <c>TaxRfc</c>, que es el RFC
/// fiscal y una cosa distinta, ni por semejanza de nombre.
/// </summary>
public interface ICompanyIdentityResolver
{
  /// <summary>Devuelve <c>null</c> si la empresa no existe o está inactiva.</summary>
  Task<long?> ResolveCompanyIdAsync(string legacyRfc, CancellationToken ct = default);
}
