namespace OrionERP.Application.Features.Logistica.PhysicalCounts;

/// <summary>
/// Cómo se nombra una sesión de conteo en pantalla. Un conteo por ubicación se llama por su ubicación;
/// uno por material no tiene una sola ubicación, así que se nombra por el material y cuántas paradas
/// implica. Vive aquí porque lo usan tanto la pantalla de conteos como el tablero de inicio.
/// </summary>
public static class PhysicalCountScopeLabel
{
  public const string UnnamedLocation = "Ubicación sin nombre";
  public const string EmptyMaterialScope = "Conteo por material";

  public static string Format(
    string? scopeType,
    string? locationName,
    string? primaryMaterialLabel,
    int materialCount,
    int locationCount)
  {
    if (!PhysicalCountSessionScopeTypes.IsMaterialScope(scopeType))
    {
      return string.IsNullOrWhiteSpace(locationName) ? UnnamedLocation : locationName.Trim();
    }

    var subject = materialCount > 1
      ? $"{materialCount} materiales"
      : string.IsNullOrWhiteSpace(primaryMaterialLabel)
        ? EmptyMaterialScope
        : primaryMaterialLabel.Trim();

    var stops = FormatLocationCount(locationCount);
    return stops is null ? subject : $"{subject} — {stops}";
  }

  public static string Format(PhysicalCountSessionSummaryDto session)
    => Format(session.ScopeType, session.LocationName, session.PrimaryMaterialLabel, session.MaterialCount, session.LocationCount);

  public static string Format(PhysicalCountSessionDetailDto session)
    => Format(session.ScopeType, session.LocationName, session.PrimaryMaterialLabel, session.MaterialCount, session.LocationCount);

  public static string Format(PhysicalCountPendingRecountDto session)
    => Format(session.ScopeType, session.LocationName, session.PrimaryMaterialLabel, session.MaterialCount, session.LocationCount);

  /// <summary>"6 ubicaciones", "1 ubicación", o nada cuando todavía no hay renglones que contar.</summary>
  public static string? FormatLocationCount(int locationCount)
    => locationCount switch
    {
      <= 0 => null,
      1 => "1 ubicación",
      _ => $"{locationCount} ubicaciones"
    };
}
