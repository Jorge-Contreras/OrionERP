namespace OrionERP.Application.Features.Logistica.Materials;

/// <summary>
/// Un rol de producción es la forma en que el usuario elige, en una sola decisión, el par
/// <c>(ProductType, FulfillmentMode)</c> de un material. <c>FulfillmentMode</c> sigue siendo la
/// columna que gobierna el comportamiento —venta, producción, costeo, guardas— y
/// <c>ProductType</c> es la etiqueta que la acompaña; el rol impide que se contradigan.
/// </summary>
public sealed record MaterialProductionRoleOption(
  string Key,
  string Label,
  string Description,
  string ProductType,
  string FulfillmentMode,
  bool RequiresRecipe,
  bool KeepsOwnStock,
  bool IsProducible);

public static class MaterialProductionRoles
{
  public const string PurchasedInput = "PurchasedInput";
  public const string Resale = "Resale";
  public const string BatchSubProduct = "BatchSubProduct";
  public const string OnDemandSubRecipe = "OnDemandSubRecipe";
  public const string OnDemandFinishedGood = "OnDemandFinishedGood";
  public const string BatchFinishedGood = "BatchFinishedGood";

  /// <summary>Rol de un material cuyo par actual no corresponde a ninguna combinación válida.</summary>
  public const string Unclassified = "Unclassified";

  public static IReadOnlyList<MaterialProductionRoleOption> All { get; } =
  [
    new(PurchasedInput,
      "Insumo comprado",
      "Se compra hecho y se descuenta del inventario. No lleva receta.",
      "RawMaterial", "StockItem",
      RequiresRecipe: false, KeepsOwnStock: true, IsProducible: false),

    new(Resale,
      "Artículo de reventa",
      "Se compra y se vende sin transformarlo, como refrescos o cervezas.",
      "Resale", "StockItem",
      RequiresRecipe: false, KeepsOwnStock: true, IsProducible: false),

    new(BatchSubProduct,
      "Subproducto por tanda",
      "Semielaborado que se produce en tanda y vive en inventario con su propia unidad. Las recetas que lo usan descuentan ese inventario.",
      "SemiFinished", "MakeToStock",
      RequiresRecipe: true, KeepsOwnStock: true, IsProducible: true),

    new(OnDemandSubRecipe,
      "Subreceta al momento",
      "Preparación que no guarda inventario propio: cada venta del platillo que la usa se descuenta hasta materia prima.",
      "SemiFinished", "MakeToOrder",
      RequiresRecipe: true, KeepsOwnStock: false, IsProducible: false),

    new(OnDemandFinishedGood,
      "Producto terminado al momento",
      "Se prepara cuando se ordena. Su receta se explota en la venta.",
      "FinishedGood", "MakeToOrder",
      RequiresRecipe: true, KeepsOwnStock: false, IsProducible: false),

    new(BatchFinishedGood,
      "Producto terminado por tanda",
      "Se produce en tanda y se vende del inventario resultante, como pan o postres.",
      "FinishedGood", "MakeToStock",
      RequiresRecipe: true, KeepsOwnStock: true, IsProducible: true)
  ];

  public static MaterialProductionRoleOption? Find(string? key)
    => string.IsNullOrWhiteSpace(key)
      ? null
      : All.FirstOrDefault(role => string.Equals(role.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));

  /// <summary>
  /// Traduce el par almacenado al rol correspondiente. Devuelve <see cref="Unclassified"/> cuando
  /// la combinación no es una de las válidas, para que la UI lo muestre sin reescribir los datos.
  /// </summary>
  public static string Resolve(string? productType, string? fulfillmentMode)
    => All.FirstOrDefault(role =>
        string.Equals(role.ProductType, productType?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(role.FulfillmentMode, fulfillmentMode?.Trim(), StringComparison.OrdinalIgnoreCase))
      ?.Key
      ?? Unclassified;

  public static string LabelFor(string? key)
    => Find(key)?.Label ?? "Sin clasificar";

  /// <summary>
  /// Un material sin tocar conserva el par por omisión del esquema. Sólo en ese caso otras
  /// pantallas pueden fijarle un rol sin pisar una decisión del usuario.
  /// </summary>
  public static bool IsSchemaDefault(string? productType, string? fulfillmentMode)
    => string.Equals(productType?.Trim(), "RawMaterial", StringComparison.OrdinalIgnoreCase)
      && string.Equals(fulfillmentMode?.Trim(), "StockItem", StringComparison.OrdinalIgnoreCase);
}
