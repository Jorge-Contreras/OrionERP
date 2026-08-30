using OrionERP.Application.Features.Logistica.Materials;

namespace OrionERP.Web.Features.Restaurante;

/// <summary>
/// Reglas del selector "Producto o preparación terminada" de <c>/restaurante/recetas</c>.
/// Ningún material se excluye del selector: los que ya están clasificados para llevar receta
/// se ordenan primero y el resto queda disponible con un aviso, porque de otro modo un
/// semielaborado nuevo no podría crear su primera receta.
/// </summary>
public static class RestaurantRecipeProductRules
{
  public const string ReadyGroup = "Productos y subproductos";
  public const string OtherGroup = "Otros materiales";

  /// <summary>Modo de un material que se compra hecho; su receta no produce ni descuenta.</summary>
  private const string PurchasedMode = "StockItem";

  private static readonly string[] RecipeCapableProductTypes = ["FinishedGood", "SemiFinished"];

  /// <summary>
  /// Un material puede llevar receta si ya está clasificado como producto terminado o
  /// semielaborado, o si de hecho ya tiene versiones capturadas.
  /// </summary>
  public static bool IsRecipeCapable(MaterialListItemDto? material, bool hasRecipes)
    => material is not null
      && (hasRecipes
        || RecipeCapableProductTypes.Contains(material.ProductType, StringComparer.OrdinalIgnoreCase));

  /// <summary>
  /// Agrupa las opciones del selector dejando primero las que ya pueden llevar receta.
  /// El orden dentro de cada grupo es el que trae <paramref name="options"/>.
  /// </summary>
  public static IReadOnlyList<RestaurantMaterialOption> BuildProductOptions(
    IEnumerable<RestaurantMaterialOption> options,
    IEnumerable<MaterialListItemDto> materials,
    IEnumerable<int> materialIdsWithRecipes)
  {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(materials);
    ArgumentNullException.ThrowIfNull(materialIdsWithRecipes);

    var materialsById = materials
      .GroupBy(material => material.Id)
      .ToDictionary(group => group.Key, group => group.First());
    var withRecipes = materialIdsWithRecipes.ToHashSet();

    return options
      .Select(option => (
        Option: option,
        Ready: materialsById.TryGetValue(option.Id, out var material)
          && IsRecipeCapable(material, withRecipes.Contains(option.Id))))
      .OrderBy(entry => entry.Ready ? 0 : 1)
      .Select(entry => entry.Option with { Group = entry.Ready ? ReadyGroup : OtherGroup })
      .ToList();
  }

  /// <summary>
  /// Explica qué pasará con la receta cuando el material elegido está clasificado como insumo
  /// comprado. Devuelve <c>null</c> cuando la clasificación ya es la adecuada.
  /// </summary>
  public static string? ClassificationNotice(MaterialListItemDto? material)
  {
    if (material is null
      || !string.Equals(material.FulfillmentMode, PurchasedMode, StringComparison.OrdinalIgnoreCase))
    {
      return null;
    }

    return material.BaseUnitPrice.HasValue
      ? "La receta se guardará, pero no podrás planear producción con ella y el costo de los platillos que lo usen seguirá tomándose de su precio de compra. Reclasifícalo como subproducto por lote para que la receta cuente."
      : "La receta se guardará, pero no podrás planear producción con ella. Reclasifícalo como subproducto por lote para poder producir lotes.";
  }
}
