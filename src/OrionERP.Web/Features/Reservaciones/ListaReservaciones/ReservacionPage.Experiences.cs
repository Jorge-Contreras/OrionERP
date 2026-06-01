using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Reservaciones.Experiencias;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.ListaReservaciones;

public partial class ReservacionPage
{
  internal ExperienceCatalogItemDto? SelectedExperienceCatalog
    => ExperienceCatalog.FirstOrDefault(item => item.ExperienceId == ExperienceCatalogId);

  internal ExperiencePackageOptionDto? SelectedExperiencePackage
    => SelectedExperienceCatalog?.Packages.FirstOrDefault(item => item.ExperiencePackageId == ExperiencePackageId);

  internal string ExperienceDateText
    => ExperienceDate == default ? string.Empty : ExperienceDate.ToString("yyyy-MM-dd");

  internal void OpenNewExperienceModal()
  {
    StartNewExperience();
  }

  internal void EditExperience(ReservacionExperienceDto experience)
  {
    EditingExperienceId = experience.Id;
    ExperienceCatalogId = experience.ExperienceId;
    ExperiencePackageId = experience.ExperiencePackageId;
    ExperienceDate = experience.ExperienceDate.Date;
    ExperienceAdultParticipants = experience.AdultParticipants <= 0 ? 1 : experience.AdultParticipants;
    ExperienceChildParticipants = Math.Max(experience.ChildParticipants, 0);
    SelectedExperienceAddOnIds = experience.AddOns.Select(item => item.ExperienceAddOnId).ToHashSet();
    ExperienceNotes = experience.Notes;
    ShowExperienceModal = true;
  }

  internal void OnExperienceCatalogChanged(ChangeEventArgs args)
  {
    if (!int.TryParse(args.Value?.ToString(), out var id))
    {
      ExperienceCatalogId = null;
      ExperiencePackageId = null;
      SelectedExperienceAddOnIds.Clear();
      return;
    }

    ExperienceCatalogId = id;
    var experience = SelectedExperienceCatalog;
    ExperiencePackageId = experience?.Packages.FirstOrDefault(item => string.Equals(item.Code, "clasico", StringComparison.OrdinalIgnoreCase))?.ExperiencePackageId
      ?? experience?.Packages.FirstOrDefault()?.ExperiencePackageId;
    SelectedExperienceAddOnIds.Clear();
    EnsureExperienceDateDefault();
  }

  internal void OnExperiencePackageChanged(ChangeEventArgs args)
  {
    ExperiencePackageId = int.TryParse(args.Value?.ToString(), out var id) ? id : null;
  }

  internal void OnExperienceDateChanged(ChangeEventArgs args)
  {
    if (DateTime.TryParse(args.Value?.ToString(), out var parsed))
    {
      ExperienceDate = parsed.Date;
    }
  }

  internal void ToggleExperienceAddOn(int addOnId, ChangeEventArgs args)
  {
    var isSelected = args.Value is bool value && value
      || bool.TryParse(args.Value?.ToString(), out var parsed) && parsed;

    if (isSelected)
    {
      SelectedExperienceAddOnIds.Add(addOnId);
    }
    else
    {
      SelectedExperienceAddOnIds.Remove(addOnId);
    }
  }

  internal ExperiencePricingResult? GetExperiencePreview()
  {
    if (SelectedExperienceCatalog is not { } experience || SelectedExperiencePackage is not { } package)
    {
      return null;
    }

    try
    {
      var selectedAddOns = experience.AddOns
        .Where(addOn => SelectedExperienceAddOnIds.Contains(addOn.ExperienceAddOnId))
        .Select(addOn => new ExperiencePricingAddOnInput
        {
          AddOn = addOn,
          Quantity = 1
        })
        .ToArray();

      return ExperiencePricingCalculator.Calculate(new ExperiencePricingInput
      {
        ExperienceDate = DateOnly.FromDateTime(ExperienceDate.Date),
        Experience = experience,
        Package = package,
        AdultParticipants = ExperienceAdultParticipants,
        ChildParticipants = ExperienceChildParticipants,
        AddOns = selectedAddOns
      });
    }
    catch
    {
      return null;
    }
  }

  internal async Task GuardarExperienceAsync()
  {
    if (ExperienceCatalogId is null || ExperiencePackageId is null)
    {
      UiMessages.ShowWarning("Selecciona experiencia y paquete.");
      return;
    }

    if (ExperienceAdultParticipants <= 0)
    {
      UiMessages.ShowWarning("La experiencia requiere al menos un adulto.");
      return;
    }

    var requestAddOns = SelectedExperienceAddOnIds
      .Select(id => new ReservacionExperienceAddOnRequest
      {
        ExperienceAddOnId = id,
        Quantity = 1
      })
      .ToArray();

    ReservacionCommandResult result;
    if (IsEditingExperience)
    {
      result = await ExperiencesService.UpdateExperienceAsync(new ReservacionExperienceUpdateRequest
      {
        Id = EditingExperienceId!.Value,
        ReservationId = ReservationId,
        ExperienceId = ExperienceCatalogId.Value,
        ExperiencePackageId = ExperiencePackageId.Value,
        ExperienceDate = ExperienceDate.Date,
        AdultParticipants = ExperienceAdultParticipants,
        ChildParticipants = ExperienceChildParticipants,
        AddOns = requestAddOns,
        Notes = ExperienceNotes
      });
    }
    else
    {
      result = await ExperiencesService.AddExperienceAsync(new ReservacionExperienceCreateRequest
      {
        ReservationId = ReservationId,
        ExperienceId = ExperienceCatalogId.Value,
        ExperiencePackageId = ExperiencePackageId.Value,
        ExperienceDate = ExperienceDate.Date,
        AdultParticipants = ExperienceAdultParticipants,
        ChildParticipants = ExperienceChildParticipants,
        AddOns = requestAddOns,
        Notes = ExperienceNotes
      });
    }

    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      ResetExperienceEditor();
      await RefreshExperiencesAsync();
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal async Task EliminarExperienceAsync(int experienceId)
  {
    var confirm = await Js.InvokeAsync<bool>("confirm", "¿Eliminar la experiencia seleccionada?");
    if (!confirm)
    {
      return;
    }

    var result = await ExperiencesService.DeleteExperienceAsync(experienceId);
    if (result.Success)
    {
      UiMessages.ShowSuccess(result.Message);
      if (EditingExperienceId == experienceId)
      {
        ResetExperienceEditor();
      }

      await RefreshExperiencesAsync();
    }
    else
    {
      UiMessages.ShowError(result.Message);
    }
  }

  internal void CancelExperienceEdit()
  {
    ResetExperienceEditor();
  }

  internal async Task RefreshExperiencesAsync()
  {
    Experiences = await ExperiencesService.GetExperiencesAsync(ReservationId);
    RecalculateTotals();
    await InvokeAsync(StateHasChanged);
  }

  private void StartNewExperience()
  {
    EditingExperienceId = null;
    ExperienceCatalogId = ExperienceCatalog.FirstOrDefault()?.ExperienceId;
    ExperiencePackageId = SelectedExperienceCatalog?.Packages.FirstOrDefault(item => string.Equals(item.Code, "clasico", StringComparison.OrdinalIgnoreCase))?.ExperiencePackageId
      ?? SelectedExperienceCatalog?.Packages.FirstOrDefault()?.ExperiencePackageId;
    ExperienceAdultParticipants = Math.Max(1, Detail?.Suites.Select(suite => suite.Suite).Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 2);
    ExperienceChildParticipants = 0;
    ExperienceNotes = null;
    SelectedExperienceAddOnIds.Clear();
    EnsureExperienceDateDefault();
    ShowExperienceModal = true;
  }

  private void ResetExperienceEditor()
  {
    EditingExperienceId = null;
    ExperienceCatalogId = null;
    ExperiencePackageId = null;
    ExperienceDate = DateTime.Today;
    ExperienceAdultParticipants = 2;
    ExperienceChildParticipants = 0;
    ExperienceNotes = null;
    SelectedExperienceAddOnIds.Clear();
    ShowExperienceModal = false;
  }

  private void EnsureExperienceDateDefault()
  {
    var candidate = CheckIn?.Date ?? DateTime.Today;
    if (SelectedExperienceCatalog?.SeasonStart is DateOnly seasonStart && DateOnly.FromDateTime(candidate) < seasonStart)
    {
      candidate = seasonStart.ToDateTime(TimeOnly.MinValue);
    }

    ExperienceDate = candidate;
  }
}
