using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public static class BonhomiaQuoteCalculator
{
  public static BonhomiaQuoteDto BuildQuote(
    BonhomiaQuoteRequest request,
    BonhomiaRoomAvailabilityDto room,
    IReadOnlyList<BonhomiaExtraOptionDto> extras,
    IReadOnlyList<ExperienceCatalogItemDto> experiences,
    DateTimeOffset expiresAtUtc,
    string currency,
    int maxStayNights)
  {
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(room);
    ArgumentNullException.ThrowIfNull(extras);
    ArgumentNullException.ThrowIfNull(experiences);

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
    var selectedExperiences = NormalizeSelectedExperiences(request.Experiences, experiences, room.Capacity, checkIn, checkOut);
    var experienceChargeLines = selectedExperiences
      .SelectMany(item => BuildExperienceChargeLines(item.Pricing))
      .ToArray();

    var totals = ReservacionTotalsCalculator.Calculate(
      checkIn.ToDateTime(TimeOnly.MinValue),
      checkOut.ToDateTime(TimeOnly.MinValue),
      suiteLines,
      extraLines,
      experienceChargeLines,
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

    foreach (var selectedExperience in selectedExperiences)
    {
      lines.Add(new BonhomiaQuoteLineDto
      {
        Type = "experience",
        Description = $"{selectedExperience.Experience.Name} - {selectedExperience.Package.Name} ({selectedExperience.Request.ExperienceDate:dd MMM yyyy})",
        Quantity = selectedExperience.Request.AdultParticipants + selectedExperience.Request.ChildParticipants,
        UnitPrice = selectedExperience.Pricing.UnitPrice,
        Total = selectedExperience.Pricing.PackageSubtotal
      });

      lines.AddRange(selectedExperience.Pricing.AddOns.Select(addOn => new BonhomiaQuoteLineDto
      {
        Type = "experience-addon",
        Description = $"{selectedExperience.Experience.Name} - {addOn.AddOn.Name}",
        Quantity = addOn.Quantity,
        UnitPrice = addOn.UnitPrice,
        Total = addOn.Total
      }));
    }

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
      }).ToArray(),
      Experiences = selectedExperiences.Select(item => new BonhomiaSelectedExperienceRequest
      {
        Code = item.Experience.Code,
        PackageCode = item.Package.Code,
        ExperienceDate = item.Request.ExperienceDate,
        AdultParticipants = item.Request.AdultParticipants,
        ChildParticipants = item.Request.ChildParticipants,
        AddOns = item.Request.AddOns
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
      ExperiencesSubtotal = totals.TotalExperiences,
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

  private static IReadOnlyList<SelectedExperience> NormalizeSelectedExperiences(
    IReadOnlyList<BonhomiaSelectedExperienceRequest>? selectedExperiences,
    IReadOnlyList<ExperienceCatalogItemDto> experiences,
    int participantLimit,
    DateOnly checkIn,
    DateOnly checkOut)
  {
    if (selectedExperiences is null || selectedExperiences.Count == 0)
    {
      return Array.Empty<SelectedExperience>();
    }

    var optionsByCode = experiences.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
    var normalized = new List<SelectedExperience>();

    foreach (var selected in selectedExperiences)
    {
      if (selected.AdultParticipants <= 0 && selected.ChildParticipants <= 0)
      {
        continue;
      }

      if (!optionsByCode.TryGetValue(selected.Code, out var experience))
      {
        throw new BonhomiaPublicBookingException("unknown_experience", "Una de las experiencias seleccionadas ya no esta disponible.");
      }

      var package = experience.Packages.FirstOrDefault(item => string.Equals(item.Code, selected.PackageCode, StringComparison.OrdinalIgnoreCase));
      if (package is null)
      {
        throw new BonhomiaPublicBookingException("unknown_experience_package", $"{experience.Name} ya no tiene disponible el paquete seleccionado.");
      }

      if (selected.ExperienceDate < checkIn || selected.ExperienceDate >= checkOut)
      {
        throw new BonhomiaPublicBookingException(
          "experience_date_outside_stay",
          $"{experience.Name} debe programarse dentro de las noches de la estancia.");
      }

      if (selected.AdultParticipants <= 0)
      {
        throw new BonhomiaPublicBookingException(
          "experience_adult_required",
          $"{experience.Name} requiere al menos un adulto.");
      }

      if (selected.ChildParticipants < 0)
      {
        throw new BonhomiaPublicBookingException(
          "invalid_experience_children",
          "La cantidad de menores en la experiencia no puede ser negativa.");
      }

      var totalParticipants = selected.AdultParticipants + selected.ChildParticipants;
      if (totalParticipants > participantLimit)
      {
        throw new BonhomiaPublicBookingException(
          "experience_participants_exceed_guests",
          $"{experience.Name} admite hasta {participantLimit} participante(s) segun la capacidad de la suite.");
      }

      var addOnsByCode = experience.AddOns.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
      var selectedAddOns = new List<ExperiencePricingAddOnInput>();
      var normalizedAddOnRequests = new List<BonhomiaSelectedExperienceAddOnRequest>();

      foreach (var addOnRequest in selected.AddOns.Where(item => item.Quantity > 0))
      {
        if (!addOnsByCode.TryGetValue(addOnRequest.Code, out var addOn))
        {
          throw new BonhomiaPublicBookingException("unknown_experience_addon", $"Un adicional de {experience.Name} ya no esta disponible.");
        }

        selectedAddOns.Add(new ExperiencePricingAddOnInput
        {
          AddOn = addOn,
          Quantity = addOnRequest.Quantity
        });
        normalizedAddOnRequests.Add(new BonhomiaSelectedExperienceAddOnRequest
        {
          Code = addOn.Code,
          Quantity = addOnRequest.Quantity
        });
      }

      var normalizedRequest = new BonhomiaSelectedExperienceRequest
      {
        Code = experience.Code,
        PackageCode = package.Code,
        ExperienceDate = selected.ExperienceDate,
        AdultParticipants = selected.AdultParticipants,
        ChildParticipants = selected.ChildParticipants,
        AddOns = normalizedAddOnRequests
      };

      var pricing = ExperiencePricingCalculator.Calculate(new ExperiencePricingInput
      {
        ExperienceDate = selected.ExperienceDate,
        Experience = experience,
        Package = package,
        AdultParticipants = normalizedRequest.AdultParticipants,
        ChildParticipants = normalizedRequest.ChildParticipants,
        AddOns = selectedAddOns
      });

      normalized.Add(new SelectedExperience(experience, package, normalizedRequest, pricing));
    }

    return normalized;
  }

  private static IEnumerable<ReservationChargeLine> BuildExperienceChargeLines(ExperiencePricingResult pricing)
  {
    yield return new ReservationChargeLine(pricing.PackageSubtotal, MapTaxMode(pricing.TaxMode));
    foreach (var addOn in pricing.AddOns)
    {
      yield return new ReservationChargeLine(addOn.Total, MapTaxMode(addOn.TaxMode));
    }
  }

  private static ReservationChargeTaxMode MapTaxMode(string? value)
    => value switch
    {
      ExperienceTaxModes.TaxIncluded => ReservationChargeTaxMode.TaxIncluded,
      ExperienceTaxModes.NonTaxable => ReservationChargeTaxMode.NonTaxable,
      _ => ReservationChargeTaxMode.TaxableExclusive
    };

  private sealed record SelectedExperience(
    ExperienceCatalogItemDto Experience,
    ExperiencePackageOptionDto Package,
    BonhomiaSelectedExperienceRequest Request,
    ExperiencePricingResult Pricing);
}
