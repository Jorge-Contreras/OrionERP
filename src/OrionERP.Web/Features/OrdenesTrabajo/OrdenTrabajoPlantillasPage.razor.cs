using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.OrdenesTrabajo;

public partial class OrdenTrabajoPlantillasPage : ComponentBase
{
  [Inject] private IOrdenTrabajoService OrdenTrabajoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;

  protected List<OrdenTrabajoCategoriaDto> Categories { get; set; } = [];
  protected List<OrdenTrabajoTemplateSummaryDto> Templates { get; set; } = [];
  protected OrdenTrabajoTemplateDetailDto? SelectedTemplate { get; set; }
  protected TemplateEditorModel Editor { get; set; } = new();
  protected OrdenTrabajoTemplateStepSaveRequest NewStep { get; set; } = CreateEmptyStep(1);
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsLoading { get; set; }
  protected bool IsMutating { get; set; }
  protected string? ErrorMessage { get; set; }

  private string CurrentRfc => RfcState.CurrentRfc ?? RfcState.AllowedRfcs.FirstOrDefault() ?? "OHM191112Q26";

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    await LoadAsync();
    NewTemplate();
  }

  protected async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    try
    {
      Categories = (await OrdenTrabajoService.GetCategoriesAsync()).ToList();
      Templates = (await OrdenTrabajoService.GetTemplatesAsync(CurrentRfc)).ToList();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsLoading = false;
    }
  }

  protected void NewTemplate()
  {
    SelectedTemplate = null;
    Editor = new TemplateEditorModel
    {
      Rfc = CurrentRfc,
      CategoriaCodigo = OrdenTrabajoCodes.CategoriaLimpieza,
      Activa = true,
      Steps = []
    };
    NewStep = CreateEmptyStep(1);
  }

  protected async Task SelectTemplateAsync(int id)
  {
    try
    {
      SelectedTemplate = await OrdenTrabajoService.GetTemplateDetailAsync(id);
      if (SelectedTemplate is null)
      {
        return;
      }

      Editor = new TemplateEditorModel
      {
        TemplateId = SelectedTemplate.Id,
        Nombre = SelectedTemplate.Nombre,
        CategoriaCodigo = SelectedTemplate.CategoriaCodigo,
        Rfc = SelectedTemplate.Rfc,
        Activa = SelectedTemplate.Activa,
        Steps = SelectedTemplate.DraftSteps
          .Select(step => new OrdenTrabajoTemplateStepSaveRequest
          {
            Secuencia = step.Secuencia,
            Titulo = step.Titulo,
            Descripcion = step.Descripcion,
            PoliticaFoto = step.PoliticaFoto,
            RequiereNotasEnIncidencia = step.RequiereNotasEnIncidencia,
            RequiereNotasEnNoAplica = step.RequiereNotasEnNoAplica,
            ProcedimientoId = step.ProcedimientoId
          })
          .ToList()
      };
      NewStep = CreateEmptyStep(Editor.Steps.Count + 1);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la plantilla. {ex.Message}");
    }
  }

  protected void AddStep()
  {
    if (string.IsNullOrWhiteSpace(NewStep.Titulo) || string.IsNullOrWhiteSpace(NewStep.Descripcion))
    {
      UiMessages.ShowWarning("Captura titulo y descripcion del paso.");
      return;
    }

    Editor.Steps.Add(NewStep);
    NewStep = CreateEmptyStep(Editor.Steps.Count + 1);
  }

  protected void RemoveStep(OrdenTrabajoTemplateStepSaveRequest step)
    => Editor.Steps.Remove(step);

  protected async Task SaveDraftAsync()
  {
    if (string.IsNullOrWhiteSpace(Editor.Nombre))
    {
      UiMessages.ShowWarning("Captura el nombre de la plantilla.");
      return;
    }

    if (Editor.Steps.Count == 0)
    {
      UiMessages.ShowWarning("La plantilla debe tener al menos un paso.");
      return;
    }

    IsMutating = true;
    try
    {
      var result = await OrdenTrabajoService.SaveTemplateDraftAsync(new OrdenTrabajoTemplateSaveRequest
      {
        TemplateId = Editor.TemplateId,
        Nombre = Editor.Nombre,
        CategoriaCodigo = Editor.CategoriaCodigo,
        Rfc = CurrentRfc,
        Activa = Editor.Activa,
        Steps = Editor.Steps.OrderBy(step => step.Secuencia).ToList(),
        SavedBy = CurrentUserName
      });
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadAsync();
      if (result.EntityId.HasValue)
      {
        await SelectTemplateAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la plantilla. {ex.Message}");
    }
    finally
    {
      IsMutating = false;
    }
  }

  protected async Task PublishAsync()
  {
    if (!Editor.TemplateId.HasValue)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.PublishTemplateAsync(Editor.TemplateId.Value, CurrentUserName));
    await LoadAsync();
    await SelectTemplateAsync(Editor.TemplateId.Value);
  }

  protected async Task MapRoomAsync(int roomId)
  {
    if (!Editor.TemplateId.HasValue)
    {
      return;
    }

    await MutateAsync(() => OrdenTrabajoService.MapRoomTemplateAsync(roomId, Editor.TemplateId.Value, CurrentUserName));
    await SelectTemplateAsync(Editor.TemplateId.Value);
  }

  protected async Task SeedLegacyAsync()
  {
    await MutateAsync(() => OrdenTrabajoService.SeedCleaningTemplatesFromLegacyAsync(CurrentRfc, CurrentUserName));
    await LoadAsync();
  }

  protected string GetTemplateListClass(OrdenTrabajoTemplateSummaryDto template)
    => SelectedTemplate?.Id == template.Id
      ? "list-group-item list-group-item-action active"
      : "list-group-item list-group-item-action";

  private async Task MutateAsync(Func<Task<OrdenTrabajoCommandResult>> action)
  {
    IsMutating = true;
    try
    {
      var result = await action();
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError(ex.Message);
    }
    finally
    {
      IsMutating = false;
    }
  }

  private async Task ResolveCurrentUserAsync()
  {
    var authState = await AuthenticationStateProvider.GetAuthenticationStateAsync();
    CurrentUserName = authState.User.Identity?.Name?.Trim() switch
    {
      { Length: > 0 } name => name,
      _ => "OrionERP"
    };
  }

  private static OrdenTrabajoTemplateStepSaveRequest CreateEmptyStep(int order)
    => new()
    {
      Secuencia = order,
      PoliticaFoto = OrdenTrabajoCodes.FotoNoPermitida,
      RequiereNotasEnIncidencia = true,
      RequiereNotasEnNoAplica = true
    };

  protected sealed class TemplateEditorModel
  {
    public int? TemplateId { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string CategoriaCodigo { get; set; } = OrdenTrabajoCodes.CategoriaLimpieza;
    public string Rfc { get; set; } = string.Empty;
    public bool Activa { get; set; } = true;
    public List<OrdenTrabajoTemplateStepSaveRequest> Steps { get; set; } = [];
  }
}
