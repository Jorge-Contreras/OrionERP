namespace OrionERP.Application.Features.Restaurante;

public sealed record RestaurantComboOrderOptionRule(
  long SlotId,
  long OptionId,
  long ComponentProductId,
  bool IsActive = true);

public sealed record RestaurantComboOrderSlotRule(
  long Id,
  string Name,
  int MinSelections,
  int MaxSelections,
  IReadOnlyList<RestaurantComboOrderOptionRule> Options);

public sealed record RestaurantMenuSectionMembershipRule(
  long ProductId,
  long MenuSectionId,
  string MenuSectionName,
  int MenuSectionSortOrder);

public sealed record RestaurantModifierEffectSnapshotInput(
  string Name,
  string EffectKind);

public sealed record RestaurantModifierSnapshotInput(
  long ModifierOptionId,
  string GroupName,
  string OptionName,
  decimal PriceDelta,
  IReadOnlyList<RestaurantModifierEffectSnapshotInput> Effects);

public static class RestaurantComboOrderRules
{
  public static IReadOnlyList<long> ResolveSelectedComponentProductIds(
    IReadOnlyList<RestaurantComboOrderOptionRule> availableOptions,
    IReadOnlyCollection<long> selectedOptionIds)
  {
    ArgumentNullException.ThrowIfNull(availableOptions);
    ArgumentNullException.ThrowIfNull(selectedOptionIds);
    return availableOptions
      .Where(option => option.IsActive && selectedOptionIds.Contains(option.OptionId))
      .Select(option => option.ComponentProductId)
      .Distinct()
      .ToList();
  }

  public static IReadOnlyList<RestaurantComboOrderOptionRule> ValidateAndResolveSelections(
    string comboSku,
    IReadOnlyList<RestaurantComboOrderSlotRule> slots,
    IReadOnlyList<RestaurantComboSelectionCreateRequest> selections)
  {
    ArgumentNullException.ThrowIfNull(slots);
    ArgumentNullException.ThrowIfNull(selections);
    if (slots.Count == 0)
    {
      throw new InvalidOperationException($"El combo {comboSku} no tiene grupos activos configurados.");
    }
    if (selections.Count != selections.Select(selection => selection.ComboSlotOptionId).Distinct().Count())
    {
      throw new InvalidOperationException("No se puede repetir la misma opción dentro de un combo.");
    }
    if (selections.Any(selection => slots.All(slot => slot.Id != selection.ComboSlotId)))
    {
      throw new InvalidOperationException("Una selección no pertenece a los grupos del combo o al RFC seleccionado.");
    }

    var resolved = new List<RestaurantComboOrderOptionRule>();
    foreach (var slot in slots)
    {
      var slotSelections = selections.Where(selection => selection.ComboSlotId == slot.Id).ToList();
      if (slotSelections.Count < slot.MinSelections || slotSelections.Count > slot.MaxSelections)
      {
        throw new InvalidOperationException(
          $"El grupo {slot.Name} requiere entre {slot.MinSelections} y {slot.MaxSelections} opciones.");
      }
      foreach (var selection in slotSelections)
      {
        var option = slot.Options.SingleOrDefault(candidate =>
          candidate.OptionId == selection.ComboSlotOptionId && candidate.IsActive)
          ?? throw new InvalidOperationException("Una opción está inactiva o no pertenece al combo y RFC seleccionados.");
        resolved.Add(option);
      }
    }
    if (resolved.Count == 0)
    {
      throw new InvalidOperationException("Un combo debe incluir al menos un componente operativo.");
    }
    return resolved;
  }

  public static RestaurantMenuSectionMembershipRule RequireActiveMenuMembership(
    long productId,
    long? requestedMenuSectionId,
    IReadOnlyList<RestaurantMenuSectionMembershipRule> memberships)
  {
    ArgumentNullException.ThrowIfNull(memberships);
    var productSections = memberships
      .Where(item => item.ProductId == productId)
      .OrderBy(item => item.MenuSectionSortOrder)
      .ThenBy(item => item.MenuSectionId)
      .ToList();
    if (productSections.Count == 0)
    {
      throw new InvalidOperationException("El producto no pertenece al menú vigente de la sede seleccionada.");
    }
    if (!requestedMenuSectionId.HasValue)
    {
      return productSections[0];
    }
    return productSections.SingleOrDefault(item => item.MenuSectionId == requestedMenuSectionId.Value)
      ?? throw new InvalidOperationException("La sección seleccionada no contiene el producto en el menú vigente.");
  }

  public static IReadOnlyList<RestaurantOrderLineModifierDto> ExpandModifierSnapshot(
    RestaurantModifierSnapshotInput modifier)
  {
    ArgumentNullException.ThrowIfNull(modifier);
    if (modifier.Effects.Count == 0)
    {
      return
      [
        new RestaurantOrderLineModifierDto
        {
          ModifierOptionId = modifier.ModifierOptionId,
          GroupName = modifier.GroupName,
          Name = modifier.OptionName,
          PriceDelta = modifier.PriceDelta,
          EffectKind = RestaurantModifierEffectKinds.AdjustQuantity
        }
      ];
    }
    return modifier.Effects.Select((effect, index) => new RestaurantOrderLineModifierDto
    {
      ModifierOptionId = modifier.ModifierOptionId,
      GroupName = modifier.GroupName,
      Name = effect.Name,
      PriceDelta = index == 0 ? modifier.PriceDelta : 0,
      EffectKind = RestaurantModifierEffectKinds.Normalize(effect.EffectKind)
    }).ToList();
  }

  public static string FormatModifierInstruction(RestaurantOrderLineModifierDto modifier)
  {
    ArgumentNullException.ThrowIfNull(modifier);
    return modifier.EffectKind switch
    {
      RestaurantModifierEffectKinds.RemoveIngredient => $"SIN {modifier.Name}",
      RestaurantModifierEffectKinds.AddQuantity => $"AGREGAR {modifier.Name}",
      _ => modifier.Name
    };
  }
}
