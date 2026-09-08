using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantComboOrderRulesTests
{
  [Fact]
  public void ResolveSelectedComponentProductIds_DoesNotLoadUnselectedAlternatives()
  {
    RestaurantComboOrderOptionRule[] options =
    [
      new(1, 10, 100),
      new(1, 11, 101),
      new(1, 12, 102)
    ];

    var productIds = RestaurantComboOrderRules.ResolveSelectedComponentProductIds(options, [11L]);

    Assert.Equal(new long[] { 101L }, productIds);
  }

  [Fact]
  public void ValidateAndResolveSelections_RejectsComboWithOnlyEmptyOptionalSlots()
  {
    RestaurantComboOrderSlotRule[] slots =
    [
      new(1, "Extras", 0, 2, [new(1, 10, 100)])
    ];

    var error = Assert.Throws<InvalidOperationException>(() =>
      RestaurantComboOrderRules.ValidateAndResolveSelections("COMBO-1", slots, []));

    Assert.Contains("al menos un componente operativo", error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ValidateAndResolveSelections_RejectsInactiveSelectedOption()
  {
    RestaurantComboOrderSlotRule[] slots =
    [
      new(1, "Bebida", 1, 1, [new(1, 10, 100, IsActive: false)])
    ];
    RestaurantComboSelectionCreateRequest[] selections =
    [
      new() { ComboSlotId = 1, ComboSlotOptionId = 10 }
    ];

    Assert.Throws<InvalidOperationException>(() =>
      RestaurantComboOrderRules.ValidateAndResolveSelections("COMBO-1", slots, selections));
  }

  [Fact]
  public void RequireActiveMenuMembership_RejectsOmittedOrManipulatedSectionOutsideCurrentMenu()
  {
    var emptyError = Assert.Throws<InvalidOperationException>(() =>
      RestaurantComboOrderRules.RequireActiveMenuMembership(50, null, []));
    Assert.Contains("menú vigente", emptyError.Message, StringComparison.OrdinalIgnoreCase);

    RestaurantMenuSectionMembershipRule[] memberships =
    [
      new(50, 7, "Comida", 1)
    ];
    Assert.Throws<InvalidOperationException>(() =>
      RestaurantComboOrderRules.RequireActiveMenuMembership(50, 99, memberships));
    Assert.Equal(7, RestaurantComboOrderRules.RequireActiveMenuMembership(50, null, memberships).MenuSectionId);
  }

  [Fact]
  public void ExpandModifierSnapshot_PreservesEachSubstitutionEffectWithoutDuplicatingPrice()
  {
    var modifier = new RestaurantModifierSnapshotInput(
      88,
      "Cambios",
      "Sustituir proteína",
      15m,
      [
        new("Pollo", RestaurantModifierEffectKinds.RemoveIngredient),
        new("Carne", RestaurantModifierEffectKinds.AddQuantity)
      ]);

    var snapshots = RestaurantComboOrderRules.ExpandModifierSnapshot(modifier);

    Assert.Collection(snapshots,
      removed =>
      {
        Assert.Equal("Pollo", removed.Name);
        Assert.Equal(RestaurantModifierEffectKinds.RemoveIngredient, removed.EffectKind);
        Assert.Equal(15m, removed.PriceDelta);
      },
      added =>
      {
        Assert.Equal("Carne", added.Name);
        Assert.Equal(RestaurantModifierEffectKinds.AddQuantity, added.EffectKind);
        Assert.Equal(0m, added.PriceDelta);
      });
    Assert.Equal("SIN Pollo", RestaurantComboOrderRules.FormatModifierInstruction(snapshots[0]));
    Assert.Equal("AGREGAR Carne", RestaurantComboOrderRules.FormatModifierInstruction(snapshots[1]));
  }

  [Fact]
  public void FormatModifierInstruction_AnnouncesRepeatedOptionsButNeverRepeatsARemoval()
  {
    var modifier = new RestaurantModifierSnapshotInput(
      44,
      "Extras",
      "Pollo extra",
      35m,
      [
        new RestaurantModifierEffectSnapshotInput("Cebolla", RestaurantModifierEffectKinds.RemoveIngredient),
        new RestaurantModifierEffectSnapshotInput("Pollo", RestaurantModifierEffectKinds.AddQuantity)
      ],
      Quantity: 3);

    var snapshots = RestaurantComboOrderRules.ExpandModifierSnapshot(modifier);

    Assert.All(snapshots, snapshot => Assert.Equal(3, snapshot.Quantity));
    Assert.Equal("SIN Cebolla", RestaurantComboOrderRules.FormatModifierInstruction(snapshots[0]));
    Assert.Equal("3× AGREGAR Pollo", RestaurantComboOrderRules.FormatModifierInstruction(snapshots[1]));
  }
}
