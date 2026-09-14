namespace OrionERP.Application.Features.Platform;

/// <summary>
/// Qué módulos puede usar la empresa de la sesión, para decidir qué muestra la interfaz.
/// No autoriza nada por sí mismo: los servicios de cada módulo siguen revalidando su
/// alcance y fallan cerrados. Nunca lanza por falta de datos; ante un error responde sólo
/// con el núcleo contable, de modo que la interfaz esconde en vez de invitar a un error.
/// </summary>
public interface ICompanyModuleAccess
{
  /// <summary>Módulos utilizables por la empresa de la sesión, siempre con el núcleo contable.</summary>
  Task<IReadOnlySet<string>> GetEnabledModulesAsync(CancellationToken ct = default);

  /// <summary>
  /// Lo mismo para un RFC explícito. Lo usa la autorización de páginas, que lee el RFC de
  /// los claims porque también corre fuera del circuito de Blazor.
  /// </summary>
  Task<IReadOnlySet<string>> GetEnabledModulesForRfcAsync(string rfc, CancellationToken ct = default);

  Task<bool> IsEnabledAsync(string moduleCode, CancellationToken ct = default);
}
