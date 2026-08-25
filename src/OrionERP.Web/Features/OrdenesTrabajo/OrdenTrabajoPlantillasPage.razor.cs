using OrionERP.Application.Common;
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
  [Inject] private NavigationManager Navigation { get; set; } = default!;
  [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;

  protected List<OrdenTrabajoCategoriaDto> Categories { get; set; } = [];
  protected List<OrdenTrabajoLookupDto> Employees { get; set; } = [];
  protected List<OrdenTrabajoTemplateSummaryDto> Templates { get; set; } = [];
  protected OrdenTrabajoTemplateDetailDto? SelectedTemplate { get; set; }
  protected TemplateEditorModel Editor { get; set; } = new();
  protected OrdenTrabajoCreateRequest CreateRequest { get; set; } = new();
  protected HashSet<int> CreateHelperIds { get; set; } = [];
  protected OrdenTrabajoTemplateStepSaveRequest NewStep { get; set; } = CreateEmptyStep(1);
  protected string? SelectedCategoryCode { get; set; }
  protected string CurrentUserName { get; set; } = "OrionERP";
  protected bool IsLoading { get; set; }
  protected bool IsMutating { get; set; }
  protected bool IsCreating { get; set; }
  protected string? ErrorMessage { get; set; }
  protected bool CanCreateFromSelectedTemplate => SelectedTemplate is { Activa: true, PublishedVersionId: not null };

  private string CurrentRfc => RfcState.RequireRfc();

  protected override async Task OnInitializedAsync()
  {
    await ResolveCurrentUserAsync();
    await LoadAsync();
    CreateRequest = BuildDefaultCreateRequest();
    NewTemplate();
  }

  protected async Task LoadAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    try
    {
      Categories = (await OrdenTrabajoService.GetCategoriesAsync()).ToList();
      Employees = (await OrdenTrabajoService.GetActiveEmployeeOptionsAsync(CurrentRfc)).ToList();
      Templates = (await OrdenTrabajoService.GetTemplatesAsync(CurrentRfc, SelectedCategoryCode)).ToList();
      EnsureCreateOwner();
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
    CreateHelperIds.Clear();
    CreateRequest = BuildDefaultCreateRequest();
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
      PrepareCreateFromTemplate();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la plantilla. {ex.Message}");
    }
  }

  protected async Task ApplyCategoryFilterAsync()
    => await LoadAsync();

  protected async Task ResetCategoryFilterAsync()
  {
    SelectedCategoryCode = null;
    await LoadAsync();
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

  protected void PrepareCreateFromTemplate()
  {
    if (SelectedTemplate is null)
    {
      CreateRequest = BuildDefaultCreateRequest();
      CreateHelperIds.Clear();
      return;
    }

    CreateRequest = BuildDefaultCreateRequest();
    CreateRequest.TemplateId = SelectedTemplate.Id;
    CreateRequest.CategoriaCodigo = SelectedTemplate.CategoriaCodigo;
    CreateRequest.Titulo = SelectedTemplate.Nombre;
    CreateRequest.Descripcion = $"Orden creada desde plantilla {SelectedTemplate.Nombre}.";
  }

  protected async Task CreateFromTemplateAsync()
  {
    if (!CanCreateFromSelectedTemplate || SelectedTemplate is null)
    {
      UiMessages.ShowWarning("Selecciona una plantilla activa con version publicada.");
      return;
    }

    if (CreateRequest.OwnerEmployeeId <= 0)
    {
      UiMessages.ShowWarning("Selecciona un responsable.");
      return;
    }

    if (string.IsNullOrWhiteSpace(CreateRequest.Titulo))
    {
      UiMessages.ShowWarning("Captura el titulo de la orden.");
      return;
    }

    IsCreating = true;
    try
    {
      CreateRequest.Rfc = CurrentRfc;
      CreateRequest.TemplateId = SelectedTemplate.Id;
      CreateRequest.CategoriaCodigo = SelectedTemplate.CategoriaCodigo;
      CreateRequest.HelperEmployeeIds = CreateHelperIds.ToList();
      CreateRequest.CreatedBy = CurrentUserName;

      var result = await OrdenTrabajoService.CreateManualAsync(CreateRequest);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      if (result.EntityId.HasValue)
      {
        Navigation.NavigateTo($"/ordenes-trabajo/{result.EntityId.Value}");
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo crear la orden desde plantilla. {ex.Message}");
    }
    finally
    {
      IsCreating = false;
    }
  }

  protected void ToggleCreateHelper(int employeeId, ChangeEventArgs args)
  {
    if (args.Value is bool selected && selected)
    {
      CreateHelperIds.Add(employeeId);
      return;
    }

    if (bool.TryParse(args.Value?.ToString(), out var parsed) && parsed)
    {
      CreateHelperIds.Add(employeeId);
      return;
    }

    CreateHelperIds.Remove(employeeId);
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

  protected async Task SeedLegacyChecklistsAsync()
  {
    await MutateAsync(() => OrdenTrabajoService.SeedChecklistTemplatesFromLegacyAsync(CurrentRfc, CurrentUserName));
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

  private OrdenTrabajoCreateRequest BuildDefaultCreateRequest()
    => new()
    {
      Rfc = CurrentRfc,
      CategoriaCodigo = SelectedTemplate?.CategoriaCodigo ?? OrdenTrabajoCodes.CategoriaMantenimiento,
      Prioridad = OrdenTrabajoCodes.PrioridadNormal,
      FechaProgramada = DateTime.Today,
      FechaVencimiento = DateTime.Today,
      OwnerEmployeeId = Employees.FirstOrDefault()?.Id ?? 0
    };

  private void EnsureCreateOwner()
  {
    if ((CreateRequest.OwnerEmployeeId <= 0 || !Employees.Any(employee => employee.Id == CreateRequest.OwnerEmployeeId)) && Employees.Count > 0)
    {
      CreateRequest.OwnerEmployeeId = Employees[0].Id;
    }

    CreateHelperIds.IntersectWith(Employees.Select(employee => employee.Id));
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
