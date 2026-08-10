namespace OrionERP.Application.Features.Restaurante;

public readonly record struct RestaurantCustomItemSnapshot(
  string Name,
  decimal UnitPrice,
  decimal Gross);

public static class RestaurantCustomItemRules
{
  public const string SkuSnapshot = "CUSTOM";
  public const int MaximumNameLength = 180;

  public static RestaurantCustomItemSnapshot CreateSnapshot(RestaurantOrderLineCreateRequest line)
  {
    ArgumentNullException.ThrowIfNull(line);
    if (!line.IsCustom)
    {
      throw new InvalidOperationException("La partida no está marcada como cargo personalizado.");
    }
    if (line.ProductId.HasValue)
    {
      throw new InvalidOperationException("Un cargo personalizado no puede referenciar un producto del catálogo.");
    }
    if (line.MenuSectionId.HasValue)
    {
      throw new InvalidOperationException("Un cargo personalizado usa su propia sección de cocina.");
    }
    var name = line.CustomName?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
      throw new InvalidOperationException("El cargo personalizado requiere una descripción.");
    }
    if (name.Length > MaximumNameLength)
    {
      throw new InvalidOperationException($"La descripción del cargo personalizado no puede exceder {MaximumNameLength} caracteres.");
    }
    if (!line.CustomUnitPrice.HasValue || line.CustomUnitPrice.Value <= 0)
    {
      throw new InvalidOperationException("El cargo personalizado requiere un precio mayor que cero.");
    }
    if (line.Quantity <= 0)
    {
      throw new InvalidOperationException("La cantidad del cargo personalizado debe ser mayor que cero.");
    }
    if (line.ModifierOptionIds.Count > 0)
    {
      throw new InvalidOperationException("Un cargo personalizado no puede usar modificadores de catálogo.");
    }

    var unitPrice = decimal.Round(line.CustomUnitPrice.Value, 2, MidpointRounding.AwayFromZero);
    var gross = decimal.Round(unitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);
    return new RestaurantCustomItemSnapshot(name, unitPrice, gross);
  }

  public static void ValidateCatalogLine(RestaurantOrderLineCreateRequest line)
  {
    ArgumentNullException.ThrowIfNull(line);
    if (line.IsCustom)
    {
      throw new InvalidOperationException("La partida personalizada debe validarse como cargo personalizado.");
    }
    if (!line.ProductId.HasValue || line.ProductId.Value <= 0)
    {
      throw new InvalidOperationException("Cada partida de catálogo requiere un producto válido.");
    }
    if (line.MenuSectionId is <= 0)
    {
      throw new InvalidOperationException("La sección de menú seleccionada no es válida.");
    }
    if (!string.IsNullOrWhiteSpace(line.CustomName) || line.CustomUnitPrice.HasValue)
    {
      throw new InvalidOperationException("El nombre o precio personalizado no puede sobrescribir un producto del catálogo.");
    }
  }
}
