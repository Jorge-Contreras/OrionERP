namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantOnlineCartPricingResult
{
  public IReadOnlyList<RestaurantOnlineCartPricedLine> Lines { get; init; }
    = Array.Empty<RestaurantOnlineCartPricedLine>();
  public decimal MerchandiseTotal { get; init; }
}

public sealed class RestaurantOnlineCartPricedLine
{
  public string LineKey { get; init; } = string.Empty;
  public RestaurantProductDto Product { get; init; } = new();
  public RestaurantOnlineQuoteLineDto QuoteLine { get; init; } = new();
  public RestaurantOrderLineCreateRequest OrderLine { get; init; } = new();
}

public static class RestaurantOnlineCartCalculator
{
  private const int MaximumLines = 50;
  private const int MaximumModifiersPerSelection = 100;
  private const int MaximumComboSelectionsPerLine = 100;

  public static RestaurantOnlineCartPricingResult Calculate(
    RestaurantPosCatalogDto catalog,
    RestaurantOnlineQuoteRequest request)
  {
    ArgumentNullException.ThrowIfNull(catalog);
    ArgumentNullException.ThrowIfNull(request);
    var lines = ValidateRequestShape(request);

    var priced = new List<RestaurantOnlineCartPricedLine>(lines.Count);
    for (var index = 0; index < lines.Count; index++)
    {
      var requested = lines[index];
      if (requested.Quantity <= 0 || requested.Quantity != decimal.Truncate(requested.Quantity) || requested.Quantity > 99)
        throw new InvalidOperationException("La cantidad de cada producto debe ser un entero entre 1 y 99.");
      if (requested.Notes?.Length > 500)
        throw new InvalidOperationException("La nota de un producto no puede exceder 500 caracteres.");

      var section = catalog.Sections.SingleOrDefault(item => item.Id == requested.MenuSectionId)
        ?? throw new InvalidOperationException("La sección seleccionada ya no está disponible.");
      var product = section.Products.SingleOrDefault(item => item.Id == requested.ProductId)
        ?? throw new InvalidOperationException("El producto no pertenece al menú vigente.");
      if (!product.IsActive || product.IsSoldOut)
        throw new InvalidOperationException($"{product.Name} no está disponible en este momento.");
      if (!product.CanOrderOnline)
        throw new InvalidOperationException($"{product.Name} está visible en el menú, pero no está habilitado para pedidos en línea.");

      var modifierNames = new List<string>();
      var comboNames = new List<string>();
      decimal unitPrice;
      if (string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase))
      {
        if (requested.ModifierOptionIds.Count > 0)
          throw new InvalidOperationException("Los modificadores de un combo deben pertenecer a su componente.");
        var selections = requested.ComboSelections.Select(selection => new RestaurantComboSelectionCreateRequest
        {
          ComboSlotId = selection.ComboSlotId,
          ComboSlotOptionId = selection.ComboSlotOptionId,
          ModifierOptionIds = selection.ModifierOptionIds.ToList(),
          Notes = NormalizeNote(selection.Notes)
        }).ToList();
        var slots = product.ComboSlots.Where(slot => slot.IsActive).Select(slot => new RestaurantComboOrderSlotRule(
          slot.Id,
          slot.Name,
          slot.MinSelections,
          slot.MaxSelections,
          slot.Options.Select(option => new RestaurantComboOrderOptionRule(
            slot.Id,
            option.Id,
            option.ComponentProductId,
            option.IsActive && !option.IsSoldOut && option.ComponentProduct?.CanOrderOnline == true)).ToArray())).ToArray();
        _ = RestaurantComboOrderRules.ValidateAndResolveSelections(product.Sku, slots, selections);

        var priceSelections = new List<RestaurantComboPriceSelection>();
        foreach (var selection in requested.ComboSelections)
        {
          if (selection.Notes?.Length > 500)
            throw new InvalidOperationException("La nota de un componente no puede exceder 500 caracteres.");
          var slot = product.ComboSlots.Single(item => item.Id == selection.ComboSlotId);
          var option = slot.Options.Single(item => item.Id == selection.ComboSlotOptionId);
          if (!option.IsActive || option.IsSoldOut)
            throw new InvalidOperationException($"{option.ComponentProductName} ya no está disponible.");
          if (option.ComponentProduct?.CanOrderOnline != true)
            throw new InvalidOperationException($"{option.ComponentProductName} no está habilitado para pedidos en línea.");
          var componentModifiers = ValidateModifiers(option.ComponentModifierGroups, selection.ModifierOptionIds);
          priceSelections.Add(new RestaurantComboPriceSelection(
            option.PriceDelta,
            option.Quantity,
            componentModifiers.Select(item => item.PriceDelta).ToArray()));
          comboNames.Add($"{slot.Name}: {option.ComponentProductName}");
          modifierNames.AddRange(componentModifiers.Select(item => $"{option.ComponentProductName}: {item.Name}"));
        }
        unitPrice = RestaurantComboPricingRules.CalculateUnitPrice(product.Price, priceSelections);
      }
      else
      {
        if (requested.ComboSelections.Count > 0)
          throw new InvalidOperationException("Las selecciones de combo no corresponden a este producto.");
        var modifiers = ValidateModifiers(product.ModifierGroups, requested.ModifierOptionIds);
        modifierNames.AddRange(modifiers.Select(item => item.Name));
        unitPrice = decimal.Round(product.Price + modifiers.Sum(item => item.PriceDelta), 2, MidpointRounding.AwayFromZero);
      }

      var lineTotal = decimal.Round(unitPrice * requested.Quantity, 2, MidpointRounding.AwayFromZero);
      var lineKey = $"web-{index + 1}";
      priced.Add(new RestaurantOnlineCartPricedLine
      {
        LineKey = lineKey,
        Product = product,
        QuoteLine = new RestaurantOnlineQuoteLineDto
        {
          Index = index,
          ProductId = product.Id,
          MenuSectionId = section.Id,
          ProductName = product.Name,
          Quantity = requested.Quantity,
          UnitPrice = unitPrice,
          Total = lineTotal,
          Notes = NormalizeNote(requested.Notes),
          Modifiers = modifierNames,
          ComboSelections = comboNames
        },
        OrderLine = new RestaurantOrderLineCreateRequest
        {
          ProductId = product.Id,
          MenuSectionId = section.Id,
          Quantity = requested.Quantity,
          Notes = NormalizeNote(requested.Notes),
          ModifierOptionIds = requested.ModifierOptionIds.ToList(),
          ComboSelections = requested.ComboSelections.Select(selection => new RestaurantComboSelectionCreateRequest
          {
            ComboSlotId = selection.ComboSlotId,
            ComboSlotOptionId = selection.ComboSlotOptionId,
            ModifierOptionIds = selection.ModifierOptionIds.ToList(),
            Notes = NormalizeNote(selection.Notes)
          }).ToList()
        }
      });
    }

    return new RestaurantOnlineCartPricingResult
    {
      Lines = priced,
      MerchandiseTotal = decimal.Round(priced.Sum(item => item.QuoteLine.Total), 2, MidpointRounding.AwayFromZero)
    };
  }

  private static IReadOnlyList<SelectedModifier> ValidateModifiers(
    IReadOnlyList<RestaurantModifierGroupDto> groups,
    IReadOnlyList<long> selectedIds)
  {
    if (selectedIds.Count > MaximumModifiersPerSelection)
      throw new InvalidOperationException("Hay demasiados modificadores en una partida.");
    if (selectedIds.Count != selectedIds.Distinct().Count())
      throw new InvalidOperationException("No se puede repetir el mismo modificador.");

    var allOptions = groups.SelectMany(group => group.Options).ToDictionary(option => option.Id);
    if (selectedIds.Any(id => !allOptions.ContainsKey(id)))
      throw new InvalidOperationException("Un modificador no pertenece al producto seleccionado.");

    var result = new List<SelectedModifier>();
    foreach (var group in groups)
    {
      var optionIds = group.Options.Select(option => option.Id).ToHashSet();
      var selectedForGroup = selectedIds.Where(optionIds.Contains).ToList();
      if (selectedForGroup.Count < group.MinSelections || selectedForGroup.Count > group.MaxSelections)
        throw new InvalidOperationException(
          $"{group.Name} requiere entre {group.MinSelections} y {group.MaxSelections} selecciones.");
      result.AddRange(selectedForGroup.Select(id =>
      {
        var option = allOptions[id];
        return new SelectedModifier(option.Name, option.PriceDelta);
      }));
    }
    return result;
  }

  private static IReadOnlyList<RestaurantOnlineCartLineRequest> ValidateRequestShape(
    RestaurantOnlineQuoteRequest request)
  {
    var lines = request.Lines
      ?? throw new InvalidOperationException("Agrega al menos un producto a la orden.");
    if (lines.Count == 0)
      throw new InvalidOperationException("Agrega al menos un producto a la orden.");
    if (lines.Count > MaximumLines)
      throw new InvalidOperationException("La orden no puede contener más de 50 partidas.");

    foreach (var line in lines)
    {
      if (line is null)
        throw new InvalidOperationException("Una partida del pedido no es válida.");
      if (line.ModifierOptionIds is null)
        throw new InvalidOperationException("Los modificadores de una partida no son válidos.");
      if (line.ModifierOptionIds.Count > MaximumModifiersPerSelection)
        throw new InvalidOperationException("Hay demasiados modificadores en una partida.");
      if (line.ComboSelections is null)
        throw new InvalidOperationException("Las selecciones de combo no son válidas.");
      if (line.ComboSelections.Count > MaximumComboSelectionsPerLine)
        throw new InvalidOperationException("Hay demasiadas selecciones de combo en una partida.");

      foreach (var selection in line.ComboSelections)
      {
        if (selection is null || selection.ModifierOptionIds is null)
          throw new InvalidOperationException("Una selección de combo no es válida.");
        if (selection.ModifierOptionIds.Count > MaximumModifiersPerSelection)
          throw new InvalidOperationException("Hay demasiados modificadores en un componente de combo.");
      }
    }

    return lines;
  }

  private static string? NormalizeNote(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed record SelectedModifier(string Name, decimal PriceDelta);
}
