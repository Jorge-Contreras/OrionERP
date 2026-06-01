using System;
using System.Collections.Generic;
using System.Linq;

namespace OrionERP.Application.Features.Reservaciones.Experiencias;

public sealed class ExperiencePricingInput
{
  public DateOnly ExperienceDate { get; set; }
  public ExperienceCatalogItemDto Experience { get; set; } = new();
  public ExperiencePackageOptionDto Package { get; set; } = new();
  public int AdultParticipants { get; set; }
  public int ChildParticipants { get; set; }
  public IReadOnlyList<ExperiencePricingAddOnInput> AddOns { get; set; } = Array.Empty<ExperiencePricingAddOnInput>();
}

public sealed class ExperiencePricingAddOnInput
{
  public ExperienceAddOnOptionDto AddOn { get; set; } = new();
  public int Quantity { get; set; }
}

public sealed class ExperiencePricingResult
{
  public decimal PackageSubtotal { get; init; }
  public decimal UnitPrice { get; init; }
  public decimal AddOnsTotal { get; init; }
  public decimal Total { get; init; }
  public bool RequiresOperationalWarning { get; init; }
  public string TaxMode { get; init; } = ExperienceTaxModes.TaxableExclusive;
  public IReadOnlyList<ExperiencePricingAddOnResult> AddOns { get; init; } = Array.Empty<ExperiencePricingAddOnResult>();
}

public sealed class ExperiencePricingAddOnResult
{
  public ExperienceAddOnOptionDto AddOn { get; init; } = new();
  public int Quantity { get; init; }
  public decimal UnitPrice { get; init; }
  public decimal Total { get; init; }
  public string TaxMode { get; init; } = ExperienceTaxModes.TaxableExclusive;
}

public static class ExperiencePricingCalculator
{
  public static ExperiencePricingResult Calculate(ExperiencePricingInput input)
  {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(input.Experience);
    ArgumentNullException.ThrowIfNull(input.Package);

    if (input.AdultParticipants <= 0)
    {
      throw new InvalidOperationException("La experiencia requiere al menos un adulto.");
    }

    if (input.ChildParticipants < 0)
    {
      throw new InvalidOperationException("Los menores no pueden ser negativos.");
    }

    var totalParticipants = input.AdultParticipants + input.ChildParticipants;
    if (totalParticipants < input.Experience.MinimumParticipants)
    {
      throw new InvalidOperationException($"La experiencia requiere al menos {input.Experience.MinimumParticipants} participante(s).");
    }

    if (input.Experience.MaximumParticipants.HasValue && totalParticipants > input.Experience.MaximumParticipants.Value)
    {
      throw new InvalidOperationException($"La experiencia admite hasta {input.Experience.MaximumParticipants.Value} participante(s).");
    }

    if (input.Experience.SeasonStart.HasValue && input.ExperienceDate < input.Experience.SeasonStart.Value)
    {
      throw new InvalidOperationException("La fecha seleccionada esta antes de la temporada de la experiencia.");
    }

    if (input.Experience.SeasonEnd.HasValue && input.ExperienceDate > input.Experience.SeasonEnd.Value)
    {
      throw new InvalidOperationException("La fecha seleccionada esta despues de la temporada de la experiencia.");
    }

    if (input.Package.UnitPrice < 0m)
    {
      throw new InvalidOperationException("La configuracion de precio de la experiencia no es valida.");
    }

    var unitPrice = decimal.Round(input.Package.UnitPrice, 2, MidpointRounding.ToEven);
    var packageSubtotal = decimal.Round(unitPrice * totalParticipants, 2, MidpointRounding.ToEven);

    var addOns = input.AddOns
      .Where(item => item.Quantity > 0)
      .Select(item =>
      {
        var quantity = item.AddOn.AppliesPerParticipant
          ? totalParticipants
          : item.Quantity;
        var total = decimal.Round(item.AddOn.UnitPrice * quantity, 2, MidpointRounding.ToEven);
        return new ExperiencePricingAddOnResult
        {
          AddOn = item.AddOn,
          Quantity = quantity,
          UnitPrice = item.AddOn.UnitPrice,
          Total = total,
          TaxMode = string.IsNullOrWhiteSpace(item.AddOn.TaxMode) ? input.Package.TaxMode : item.AddOn.TaxMode
        };
      })
      .ToArray();

    var addOnsTotal = addOns.Sum(item => item.Total);

    return new ExperiencePricingResult
    {
      PackageSubtotal = packageSubtotal,
      UnitPrice = unitPrice,
      AddOnsTotal = addOnsTotal,
      Total = packageSubtotal + addOnsTotal,
      RequiresOperationalWarning = input.ChildParticipants > 0,
      TaxMode = string.IsNullOrWhiteSpace(input.Package.TaxMode) ? ExperienceTaxModes.TaxableExclusive : input.Package.TaxMode,
      AddOns = addOns
    };
  }
}
