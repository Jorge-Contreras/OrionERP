namespace OrionERP.Application.Features.Logistica.Materials;

/// <summary>
/// Orden canónico de los materiales en cualquier lista, combo, selector o reporte: descripción
/// ascendente, con el código como desempate. Está centralizado porque el catálogo se muestra en
/// Logística, Compras, Conteos y Restaurante, y cada módulo había elegido su propio criterio;
/// quien lo consulta busca por nombre, no por código.
/// </summary>
public static class MaterialSortOrder
{
  /// <summary>Nombre de la columna que manda en el orden, tal como se escribe en SQL.</summary>
  public const string SqlDescriptionColumn = "[Description]";

  /// <summary>
  /// Claves de ordenamiento para pegar en un <c>ORDER BY</c>, con el alias de la tabla de
  /// materiales. El alias siempre es literal en el código, nunca entrada del usuario.
  /// </summary>
  public static string SqlKeys(string alias)
    => $"{alias}.{SqlDescriptionColumn}, {alias}.MaterialCode";

  /// <summary>
  /// Comparador para ordenar en memoria. Es el mismo que ya usaban los combos de Restaurante, de
  /// modo que una lista armada en C# y una traída de SQL se vean igual.
  /// </summary>
  public static StringComparer Comparer => StringComparer.CurrentCultureIgnoreCase;

  /// <summary>Llave compuesta para los lugares que ordenan por una sola cadena.</summary>
  public static string Key(string? description, string? code)
    => $"{description}|{code}";
}
