using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;
using OrionERP.Application.Features.Reservaciones.OpenClaw;

namespace OrionERP.Application.Features.Reservaciones.Cfdi;

public sealed class ReservationCfdiSuiteSource
{
  public int Id { get; set; }
  public DateTime Fecha { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string? RoomDescription { get; set; }
  public decimal Price { get; set; }
}

public sealed class ReservationCfdiExtraSource
{
  public int Id { get; set; }
  public string CatalogName { get; set; } = string.Empty;
  public string? Description { get; set; }
  public decimal Amount { get; set; }
  public decimal Quantity { get; set; } = 1m;
  public decimal UnitPrice { get; set; }
  public string? Notes { get; set; }
}

public static class ReservationCfdiLineFactory
{
  private const decimal IvaRate = 0.16m;
  private static readonly CultureInfo SpanishMexico = new("es-MX");

  public static IReadOnlyList<ReservationCfdiItemPreviewDto> CreateItems(
      IEnumerable<ReservationCfdiSuiteSource> suites,
      IEnumerable<ReservationCfdiExtraSource> extras,
      decimal suiteDiscountPercent = 0m)
  {
    ArgumentNullException.ThrowIfNull(suites);
    ArgumentNullException.ThrowIfNull(extras);

    var activeSuiteDiscountPercent = ReservacionTotalsCalculator.NormalizeSuiteDiscountPercent(suiteDiscountPercent);
    var items = new List<ReservationCfdiItemPreviewDto>();

    foreach (var suite in suites.Where(static item => item.Price > 0m))
    {
      items.Add(new ReservationCfdiItemPreviewDto
      {
        SourceType = "Suite",
        SourceId = suite.Id,
        Fecha = suite.Fecha,
        Description = BuildSuiteDescription(suite),
        ProductCode = "90111803",
        UnitCode = "ROM",
        Unit = "Habitacion",
        Quantity = 1m,
        UnitPrice = RoundCurrency(suite.Price),
        Subtotal = RoundCurrency(suite.Price),
        TaxObject = "02"
      });
    }

    var discountPool = 0m;

    foreach (var extra in extras)
    {
      if (extra.Amount < 0m)
      {
        discountPool += RoundCurrency(Math.Abs(extra.Amount));
        continue;
      }

      if (extra.Amount <= 0m)
      {
        continue;
      }

      var mapping = ResolveExtraMapping(extra);
      var quantity = extra.Quantity > 0m ? extra.Quantity : 1m;
      var unitPrice = extra.UnitPrice != 0m
        ? RoundCurrency(extra.UnitPrice)
        : RoundCurrency(extra.Amount / quantity);
      var subtotal = RoundCurrency(unitPrice * quantity);

      items.Add(new ReservationCfdiItemPreviewDto
      {
        SourceType = "Extra",
        SourceId = extra.Id,
        Description = mapping.Description,
        ProductCode = mapping.ProductCode,
        UnitCode = mapping.UnitCode,
        Unit = mapping.Unit,
        Quantity = quantity,
        UnitPrice = unitPrice,
        Subtotal = subtotal,
        TaxObject = "02"
      });
    }

    ApplySuiteDiscount(items, activeSuiteDiscountPercent);
    ApplyDiscountPool(items, discountPool);
    ApplyTaxes(items);

    return items;
  }

  private static string BuildSuiteDescription(ReservationCfdiSuiteSource suite)
  {
    var baseDescription = string.IsNullOrWhiteSpace(suite.RoomDescription)
      ? suite.RoomName
      : suite.RoomDescription.Trim();

    var dateLabel = suite.Fecha.ToString("dd 'DE' MMMM", SpanishMexico).ToUpper(SpanishMexico);
    return $"{baseDescription} - 1 NOCHE {dateLabel}";
  }

  private static ReservationExtraSatMapping ResolveExtraMapping(ReservationCfdiExtraSource extra)
  {
    var rawDescription = FirstNonEmpty(extra.Description, extra.Notes, extra.CatalogName);
    var normalized = OpenClawReservationNaming.NormalizeLookupKey($"{extra.CatalogName} {rawDescription}");

    if (normalized.Contains("TRANSPORTE", StringComparison.Ordinal))
    {
      return new ReservationExtraSatMapping(
          "78111802",
          "E54",
          "Viaje",
          "SERVICIO TRANSPORTE DE PASAJEROS");
    }

    if (normalized.Contains("CAMASTRO", StringComparison.Ordinal))
    {
      return new ReservationExtraSatMapping(
          "56101515",
          "E48",
          "Unidad de servicio",
          "CAMASTRO EXTRA PARA SUITE");
    }

    if (normalized.Contains("ALIMENTO", StringComparison.Ordinal) ||
        normalized.Contains("DESAYUNO", StringComparison.Ordinal) ||
        normalized.Contains("CENA", StringComparison.Ordinal) ||
        normalized.Contains("COFFEE BREAK", StringComparison.Ordinal) ||
        normalized.Contains("BEBIDA", StringComparison.Ordinal))
    {
      return new ReservationExtraSatMapping(
          "90101501",
          "E48",
          "Unidad de servicio",
          BuildExtraDescription(rawDescription));
    }

    if (normalized.Contains("CHECK IN", StringComparison.Ordinal) ||
        normalized.Contains("CHECKOUT", StringComparison.Ordinal) ||
        normalized.Contains("CHECK OUT", StringComparison.Ordinal))
    {
      return new ReservationExtraSatMapping(
          "90111803",
          "E48",
          "Unidad de servicio",
          BuildExtraDescription(rawDescription));
    }

    return new ReservationExtraSatMapping(
        "90111803",
        "E48",
        "Unidad de servicio",
        BuildExtraDescription(rawDescription));
  }

  private static string BuildExtraDescription(string description)
    => description.Trim().ToUpperInvariant();

  private static void ApplySuiteDiscount(List<ReservationCfdiItemPreviewDto> items, decimal discountPercent)
  {
    if (discountPercent <= 0m)
    {
      return;
    }

    foreach (var item in items.Where(static item => string.Equals(item.SourceType, "Suite", StringComparison.Ordinal)))
    {
      item.Discount = RoundCurrency(item.Subtotal * (discountPercent / 100m));
    }
  }

  private static void ApplyDiscountPool(List<ReservationCfdiItemPreviewDto> items, decimal discountPool)
  {
    var remainingDiscount = RoundCurrency(discountPool);
    if (remainingDiscount <= 0m || items.Count == 0)
    {
      return;
    }

    var totalDiscountableSubtotal = RoundCurrency(items.Sum(static item => item.Subtotal - item.Discount));
    if (remainingDiscount > totalDiscountableSubtotal)
    {
      throw new InvalidOperationException("El descuento de la reservacion excede el subtotal facturable.");
    }

    for (var index = 0; index < items.Count; index++)
    {
      var item = items[index];
      if (remainingDiscount <= 0m)
      {
        break;
      }

      var discountableSubtotal = RoundCurrency(item.Subtotal - item.Discount);
      if (discountableSubtotal <= 0m)
      {
        continue;
      }

      decimal itemDiscount;
      if (index == items.Count - 1)
      {
        itemDiscount = Math.Min(discountableSubtotal, remainingDiscount);
      }
      else
      {
        var proportionalShare = RoundCurrency((discountableSubtotal / totalDiscountableSubtotal) * discountPool);
        itemDiscount = Math.Min(discountableSubtotal, proportionalShare);
      }

      item.Discount = RoundCurrency(item.Discount + itemDiscount);
      remainingDiscount = RoundCurrency(remainingDiscount - itemDiscount);
    }

    if (remainingDiscount > 0m)
    {
      var lastDiscountableItem = items.LastOrDefault(static item => RoundCurrency(item.Subtotal - item.Discount) > 0m);
      if (lastDiscountableItem is null)
      {
        throw new InvalidOperationException("El descuento de la reservacion excede el subtotal facturable.");
      }

      var remainingCapacity = RoundCurrency(lastDiscountableItem.Subtotal - lastDiscountableItem.Discount);
      var extraDiscount = Math.Min(remainingCapacity, remainingDiscount);
      lastDiscountableItem.Discount = RoundCurrency(lastDiscountableItem.Discount + extraDiscount);
      remainingDiscount = RoundCurrency(remainingDiscount - extraDiscount);
      if (remainingDiscount > 0m)
      {
        throw new InvalidOperationException("El descuento de la reservacion excede el subtotal facturable.");
      }
    }

    foreach (var item in items)
    {
      if (item.Discount > item.Subtotal)
      {
        throw new InvalidOperationException("El descuento distribuido excede el subtotal de un concepto.");
      }
    }
  }

  private static void ApplyTaxes(List<ReservationCfdiItemPreviewDto> items)
  {
    foreach (var item in items)
    {
      var taxableBase = RoundCurrency(item.Subtotal - item.Discount);
      if (taxableBase < 0m)
      {
        throw new InvalidOperationException("La base gravable del concepto no puede ser negativa.");
      }

      item.Tax = RoundCurrency(taxableBase * IvaRate);
      item.Total = RoundCurrency(taxableBase + item.Tax);
    }
  }

  private static string FirstNonEmpty(params string?[] candidates)
  {
    foreach (var candidate in candidates)
    {
      if (!string.IsNullOrWhiteSpace(candidate))
      {
        return candidate.Trim();
      }
    }

    return "SERVICIO ADICIONAL";
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.ToEven);

  private sealed record ReservationExtraSatMapping(string ProductCode, string UnitCode, string Unit, string Description);
}
