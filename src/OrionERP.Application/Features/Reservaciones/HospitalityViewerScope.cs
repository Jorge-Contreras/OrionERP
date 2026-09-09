namespace OrionERP.Application.Features.Reservaciones;

/// <summary>
/// Responde quién mira, no qué empresa mira: <see cref="HospitalityScope"/> acota por empresa y
/// sede, y esto acota además por dueño cuando la sesión es de un arrendador.
/// </summary>
public interface IHospitalityViewerScope
{
  /// <summary>
  /// El proveedor al que debe acotarse la lectura, o <c>null</c> cuando la sesión no es de un
  /// arrendador y ve el conjunto completo de su empresa.
  /// </summary>
  /// <remarks>
  /// Nunca devuelve <c>null</c> por no encontrar el proveedor de un arrendador: en ese caso lanza,
  /// porque degradar a "sin filtro" mostraría el hotel entero al dueño de una sola suite.
  /// </remarks>
  Task<int?> ResolveOwnerProveedorIdAsync(CancellationToken ct = default);
}
