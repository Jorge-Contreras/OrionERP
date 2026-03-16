using System;
using System.Globalization;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public static class OpenClawReservationLineFactory
{
  public static OpenClawReservationCreatedExtra CreateExtra(string catalogName, int quantity, decimal unitPrice, string? notes)
  {
    if (quantity <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
    }

    var linePrice = decimal.Round(unitPrice * quantity, 2, MidpointRounding.ToEven);

    return new OpenClawReservationCreatedExtra
    {
      CatalogName = catalogName,
      Quantity = quantity,
      UnitPrice = unitPrice,
      LinePrice = linePrice,
      Notes = BuildNotes(catalogName, quantity, notes)
    };
  }

  public static OpenClawReservationCreatedExtra CreateDiscount(string catalogName, decimal suiteSubtotal, decimal discountPercent)
  {
    if (discountPercent <= 0 || discountPercent > 100)
    {
      throw new ArgumentOutOfRangeException(nameof(discountPercent), "Discount percent must be greater than zero and less than or equal to 100.");
    }

    var amount = decimal.Round(suiteSubtotal * (discountPercent / 100m), 2, MidpointRounding.ToEven);

    return new OpenClawReservationCreatedExtra
    {
      CatalogName = catalogName,
      Quantity = 1,
      UnitPrice = -amount,
      LinePrice = -amount,
      Notes = $"DESCUENTO {discountPercent.ToString("0.##", CultureInfo.InvariantCulture)}%"
    };
  }

  public static string BuildNotes(string catalogName, int quantity, string? notes)
  {
    var trimmedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    var quantityLabel = quantity > 1 ? $"{catalogName} x{quantity}" : catalogName;

    if (trimmedNotes is null)
    {
      return quantityLabel;
    }

    return quantity > 1
      ? $"{quantityLabel} - {trimmedNotes}"
      : trimmedNotes;
  }
}
