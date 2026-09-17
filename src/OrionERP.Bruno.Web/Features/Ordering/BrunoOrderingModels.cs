using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Bruno.Web.Features.Ordering;

public sealed class BrunoCartLine
{
  public string Key { get; set; } = Guid.NewGuid().ToString("N");
  public long ProductId { get; set; }
  public long MenuSectionId { get; set; }
  public string ProductName { get; set; } = string.Empty;
  public decimal Quantity { get; set; } = 1;
  public decimal DisplayUnitPrice { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  public List<string> ModifierNames { get; set; } = [];
  public List<BrunoCartComboSelection> ComboSelections { get; set; } = [];
  public string? Notes { get; set; }

  public decimal DisplayTotal => DisplayUnitPrice * Quantity;

  public RestaurantOnlineCartLineRequest ToRequest() => new()
  {
    ProductId = ProductId,
    MenuSectionId = MenuSectionId,
    Quantity = Quantity,
    Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
    ModifierOptionIds = [.. ModifierOptionIds.Distinct()],
    ComboSelections = ComboSelections.Select(selection => selection.ToRequest()).ToList()
  };
}

public sealed class BrunoCartComboSelection
{
  public long ComboSlotId { get; set; }
  public long ComboSlotOptionId { get; set; }
  public string SlotName { get; set; } = string.Empty;
  public string OptionName { get; set; } = string.Empty;
  public decimal PriceDelta { get; set; }
  public List<long> ModifierOptionIds { get; set; } = [];
  public List<string> ModifierNames { get; set; } = [];
  public string? Notes { get; set; }

  public RestaurantOnlineComboSelectionRequest ToRequest() => new()
  {
    ComboSlotId = ComboSlotId,
    ComboSlotOptionId = ComboSlotOptionId,
    ModifierOptionIds = [.. ModifierOptionIds.Distinct()],
    Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim()
  };
}

public sealed class BrunoClipCardOptions
{
  /// <summary>Clave pública del SDK. No autoriza cobros por sí sola.</summary>
  public string ApiKey { get; set; } = string.Empty;
  public string Currency { get; set; } = "MXN";
  public string Locale { get; set; } = "es";
  public string Theme { get; set; } = "light";
  public string QuoteToken { get; set; } = string.Empty;
  public string QuoteFingerprint { get; set; } = string.Empty;
  public Guid ClientAttemptId { get; set; }
  public string CustomerName { get; set; } = string.Empty;
  public string CustomerEmail { get; set; } = string.Empty;
  public string CustomerPhone { get; set; } = string.Empty;
  public bool TermsAccepted { get; set; }
  public string TermsVersion { get; set; } = string.Empty;
  public string PrivacyVersion { get; set; } = string.Empty;
}

public sealed class BrunoPendingCheckout
{
  public Guid ClientAttemptId { get; set; }
  public string TrackingToken { get; set; } = string.Empty;
  public DateTimeOffset SavedAtUtc { get; set; }
}
