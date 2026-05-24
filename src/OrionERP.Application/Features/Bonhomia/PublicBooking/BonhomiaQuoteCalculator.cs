using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public static class BonhomiaQuoteCalculator
{
  public static BonhomiaQuoteDto BuildQuote(
    BonhomiaQuoteRequest request,
    BonhomiaRoomAvailabilityDto room,
    IReadOnlyList<BonhomiaExtraOptionDto> extras,
    DateTimeOffset expiresAtUtc,
    string currency,
    int maxStayNights)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(room);
    ArgumentNullException.ThrowIfNull(extras);

    var checkIn = request.CheckIn;
    var checkOut = request.CheckOut;
    var nights = checkOut.DayNumber - checkIn.DayNumber;
    if (nights <= 0)
    {
      throw new BonhomiaPublicBookingException("invalid_dates", "La salida debe ser posterior a la llegada.");
    }

    if (nights > maxStayNights)
    {
      throw new BonhomiaPublicBookingException("stay_too_long", $"La estancia maxima en linea es de {maxStayNights} noches.");
    }

    if (request.Guests <= 0)
    {
      throw new BonhomiaPublicBookingException("invalid_guests", "Indica al menos un huesped.");
    }

    if (request.Guests > room.Capacity)
    {
      throw new BonhomiaPublicBookingException(
        "capacity_exceeded",
        $"{room.RoomName} admite hasta {room.Capacity} huespedes.");
    }

    var daysByDate = room.Days.ToDictionary(day => day.Date);
    var suiteLines = new List<decimal>();
    var roomCalendarIds = new List<int>();

    for (var day = checkIn; day < checkOut; day = day.AddDays(1))
    {
      if (!daysByDate.TryGetValue(day, out var availability) || !availability.IsAvailable)
      {
        throw new BonhomiaPublicBookingException(
          "not_available",
          $"{room.RoomName} ya no esta disponible para todas las noches seleccionadas.");
      }

      suiteLines.Add(availability.Price > 0m ? availability.Price : room.BasePrice);
    }

    var selectedExtras = NormalizeSelectedExtras(request.Extras, extras);
    var extraLines = selectedExtras
      .Select(item => item.Option.UnitPrice * item.Quantity)
      .ToArray();

    var totals = ReservacionTotalsCalculator.Calculate(
      checkIn.ToDateTime(TimeOnly.MinValue),
      checkOut.ToDateTime(TimeOnly.MinValue),
      taxable: true,
      suiteLines,
      extraLines,
      totalPagado: 0m);

    var lines = new List<BonhomiaQuoteLineDto>
    {
      new()
      {
        Type = "suite",
        Description = $"{room.RoomName} x {nights} noche{(nights == 1 ? string.Empty : "s")}",
        Quantity = nights,
        UnitPrice = suiteLines.Count == 0 ? 0m : decimal.Round(suiteLines.Average(), 2, MidpointRounding.ToEven),
        Total = totals.TotalSuites
      }
    };

    lines.AddRange(selectedExtras.Select(item => new BonhomiaQuoteLineDto
    {
      Type = "extra",
      Description = item.Option.Name,
      Quantity = item.Quantity,
      UnitPrice = item.Option.UnitPrice,
      Total = item.Option.UnitPrice * item.Quantity
    }));

    var quoteRequest = new BonhomiaQuoteRequest
    {
      RoomName = room.RoomName,
      CheckIn = checkIn,
      CheckOut = checkOut,
      Guests = request.Guests,
      Extras = selectedExtras.Select(item => new BonhomiaSelectedExtraRequest
      {
        Code = item.Option.Code,
        Quantity = item.Quantity
      }).ToArray()
    };

    var quote = new BonhomiaQuoteDto
    {
      QuoteId = Guid.NewGuid(),
      Request = quoteRequest,
      RoomName = room.RoomName,
      RoomImage = room.Image,
      Nights = nights,
      Guests = request.Guests,
      CheckIn = checkIn,
      CheckOut = checkOut,
      SuiteSubtotal = totals.TotalSuites,
      ExtrasSubtotal = totals.TotalExtras,
      SubTotal = totals.SubTotal,
      Tax = totals.Tax,
      Ish = totals.Ish,
      Total = totals.TotalReservacion,
      Currency = string.IsNullOrWhiteSpace(currency) ? "MXN" : currency.Trim().ToUpperInvariant(),
      ExpiresAtUtc = expiresAtUtc,
      Lines = lines,
      RoomCalendarIds = roomCalendarIds
    };

    quote.Fingerprint = CreateFingerprint(quote);
    return quote;
  }

  public static string CreateFingerprint(BonhomiaQuoteDto quote)
  {
    ArgumentNullException.ThrowIfNull(quote);

    var builder = new StringBuilder();
    builder.Append(quote.RoomName.Trim().ToUpperInvariant()).Append('|')
      .Append(quote.CheckIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.CheckOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Guests.ToString(CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Total.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Currency.Trim().ToUpperInvariant());

    foreach (var line in quote.Lines.OrderBy(line => line.Type).ThenBy(line => line.Description, StringComparer.OrdinalIgnoreCase))
    {
      builder.Append('|')
        .Append(line.Type.Trim().ToUpperInvariant()).Append(':')
        .Append(line.Description.Trim().ToUpperInvariant()).Append(':')
        .Append(line.Quantity.ToString(CultureInfo.InvariantCulture)).Append(':')
        .Append(line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(line.Total.ToString("0.00", CultureInfo.InvariantCulture));
    }

    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    return Convert.ToHexString(bytes);
  }

  private static IReadOnlyList<(BonhomiaExtraOptionDto Option, int Quantity)> NormalizeSelectedExtras(
    IReadOnlyList<BonhomiaSelectedExtraRequest>? selectedExtras,
    IReadOnlyList<BonhomiaExtraOptionDto> extras)
  {
    if (selectedExtras is null || selectedExtras.Count == 0)
    {
      return Array.Empty<(BonhomiaExtraOptionDto, int)>();
    }

    var optionsByCode = extras.ToDictionary(extra => extra.Code, StringComparer.OrdinalIgnoreCase);
    var normalized = new List<(BonhomiaExtraOptionDto Option, int Quantity)>();

    foreach (var selected in selectedExtras)
    {
      if (selected.Quantity <= 0)
      {
        continue;
      }

      if (!optionsByCode.TryGetValue(selected.Code, out var option))
      {
        throw new BonhomiaPublicBookingException("unknown_extra", "Uno de los extras seleccionados ya no esta disponible.");
      }

      if (selected.Quantity > option.MaxQuantity)
      {
        throw new BonhomiaPublicBookingException(
          "extra_quantity_exceeded",
          $"{option.Name} permite hasta {option.MaxQuantity} unidad{(option.MaxQuantity == 1 ? string.Empty : "es")}.");
      }

      var existingIndex = normalized.FindIndex(item => string.Equals(item.Option.Code, option.Code, StringComparison.OrdinalIgnoreCase));
      if (existingIndex >= 0)
      {
        var mergedQuantity = normalized[existingIndex].Quantity + selected.Quantity;
        if (mergedQuantity > option.MaxQuantity)
        {
          throw new BonhomiaPublicBookingException("extra_quantity_exceeded", $"{option.Name} excede el maximo permitido.");
        }

        normalized[existingIndex] = (option, mergedQuantity);
      }
      else
      {
        normalized.Add((option, selected.Quantity));
      }
    }

    return normalized;
  }
}
