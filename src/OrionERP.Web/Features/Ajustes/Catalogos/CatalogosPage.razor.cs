using OrionERP.Application.Common;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using OrionERP.Application.Features.Ajustes.Catalogos;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Ajustes.Catalogos;

/// <summary>
/// One editor for every flat reference catalog, plus a dedicated chart-of-accounts
/// manager.
///
/// The tabs are driven by <see cref="ICatalogoService.GetDescriptors"/> rather
/// than hard-coded here, so adding a catalog is a change to the service and not
/// to this page. Data is fetched when a tab is first opened, and tenant-scoped
/// tenant-scoped tabs use the company fixed into the authenticated session.
/// </summary>
public partial class CatalogosPage : ComponentBase
{
  private const string CuentasTab = "cuentas";

  [Inject] private ICatalogoService CatalogoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IJSRuntime JsRuntime { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private NavigationManager Navigation { get; set; } = default!;

  private IReadOnlyList<CatalogoDescriptorDto> Descriptors { get; set; } = Array.Empty<CatalogoDescriptorDto>();
  private List<CatalogoItemDto> Items { get; } = [];
  private List<CuentaContableNodeDto> Cuentas { get; } = [];

  private CatalogoItemEditorModel ItemEditor { get; set; } = new();
  private CuentaEditorModel CuentaEditor { get; set; } = new();

  private string tab = string.Empty;
  private string? selectedItemId;
  private int? selectedCuentaId;
  private string? searchText;
  private string? cuentaSearchText;
  private bool includeInactive;
  private bool isLoading;
  private bool isSaving;
  private bool isNewItem = true;
  private bool isNewCuenta = true;

  private CatalogoDescriptorDto? ActiveDescriptor
    => Descriptors.FirstOrDefault(descriptor => TabKey(descriptor.Key) == tab);

  private CatalogoItemDto? SelectedItem
    => Items.FirstOrDefault(item => item.Id == selectedItemId);

  protected override async Task OnInitializedAsync()
  {
    Descriptors = CatalogoService.GetDescriptors();
    // A deep link should land on the tab it names, so a colleague can be sent
    // straight to the catalog under discussion.
    var uri = Navigation.ToAbsoluteUri(Navigation.Uri);
    tab = QueryHelpers.ParseQuery(uri.Query).TryGetValue("tab", out var requested)
          && IsKnownTab(requested.ToString())
      ? requested.ToString()
      : TabKey(Descriptors[0].Key);

    NewItem();
    NewCuenta();
    await LoadActiveTabAsync();
  }

  private async Task SelectTabAsync(string value)
  {
    if (tab == value)
    {
      return;
    }

    tab = value;
    searchText = null;
    cuentaSearchText = null;
    includeInactive = false;
    NewItem();
    NewCuenta();

    Navigation.NavigateTo(
      Navigation.GetUriWithQueryParameter("tab", value),
      forceLoad: false,
      replace: true);

    await LoadActiveTabAsync();
  }

  private Task LoadActiveTabAsync()
    => tab == CuentasTab ? LoadCuentasAsync() : LoadItemsAsync();

  private async Task LoadItemsAsync()
  {
    if (ActiveDescriptor is not { } descriptor)
    {
      return;
    }

    isLoading = true;
    try
    {
      var results = await CatalogoService.GetItemsAsync(
        descriptor.Key,
        descriptor.EsPorRfc ? CurrentRfc : null,
        searchText,
        includeInactive);

      Items.Clear();
      Items.AddRange(results);

      if (selectedItemId is not null && Items.All(item => item.Id != selectedItemId))
      {
        NewItem();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el catálogo: {ex.Message}");
    }
    finally
    {
      isLoading = false;
      StateHasChanged();
    }
  }

  private Task OnCatalogSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? LoadItemsAsync() : Task.CompletedTask;

  private async Task LoadCuentasAsync()
  {
    isLoading = true;
    try
    {
      var results = await CatalogoService.GetCuentasAsync(CurrentRfc, cuentaSearchText);
      Cuentas.Clear();
      Cuentas.AddRange(results);

      if (selectedCuentaId is not null && Cuentas.All(cuenta => cuenta.Id != selectedCuentaId))
      {
        NewCuenta();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el catálogo de cuentas: {ex.Message}");
    }
    finally
    {
      isLoading = false;
      StateHasChanged();
    }
  }

  private Task OnCuentaSearchKeyUpAsync(KeyboardEventArgs args)
    => args.Key == "Enter" ? LoadCuentasAsync() : Task.CompletedTask;

  private void NewItem()
  {
    selectedItemId = null;
    isNewItem = true;
    ItemEditor = new CatalogoItemEditorModel { Activo = true };
  }

  private void SelectItem(CatalogoItemDto item)
  {
    selectedItemId = item.Id;
    isNewItem = false;
    ItemEditor = new CatalogoItemEditorModel
    {
      Codigo = item.Codigo,
      Nombre = item.Nombre,
      Orden = item.Orden ?? 0,
      Activo = item.Activo
    };
  }

  private async Task SaveItemAsync()
  {
    if (ActiveDescriptor is not { } descriptor)
    {
      return;
    }

    isSaving = true;
    try
    {
      var result = await CatalogoService.SaveItemAsync(new CatalogoSaveRequest
      {
        Key = descriptor.Key,
        Id = selectedItemId,
        Codigo = ItemEditor.Codigo,
        Nombre = ItemEditor.Nombre,
        Orden = descriptor.TieneOrden ? ItemEditor.Orden : null,
        Activo = ItemEditor.Activo,
        Rfc = descriptor.EsPorRfc ? CurrentRfc : null
      });

      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      var keepSelected = descriptor.CodigoEsLlave ? ItemEditor.Codigo?.Trim() : result.EntityId?.ToString();
      await LoadItemsAsync();

      var reselected = Items.FirstOrDefault(item => item.Id == keepSelected);
      if (reselected is not null)
      {
        SelectItem(reselected);
      }
      else
      {
        NewItem();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar: {ex.Message}");
    }
    finally
    {
      isSaving = false;
      StateHasChanged();
    }
  }

  private async Task DeleteItemAsync(CatalogoItemDto item)
  {
    if (ActiveDescriptor is not { } descriptor)
    {
      return;
    }

    // Say what will happen, and to what. A generic "are you sure" teaches the
    // user to click through it.
    var label = descriptor.TieneCodigo ? $"{item.Codigo} - {item.Nombre}" : item.Nombre;
    var question = descriptor.TieneActivo
      ? $"Se desactivará \"{label}\". Dejará de aparecer en los selectores, sin afectar los registros que ya la usan. ¿Continuar?"
      : $"Se eliminará \"{label}\". Esta acción no se puede deshacer. ¿Continuar?";

    if (!await ConfirmAsync(question))
    {
      return;
    }

    isSaving = true;
    try
    {
      var result = await CatalogoService.DeleteItemAsync(
        descriptor.Key,
        item.Id,
        descriptor.EsPorRfc ? CurrentRfc : null);

      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      NewItem();
      await LoadItemsAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar: {ex.Message}");
    }
    finally
    {
      isSaving = false;
      StateHasChanged();
    }
  }

  private void NewCuenta()
  {
    selectedCuentaId = null;
    isNewCuenta = true;
    CuentaEditor = new CuentaEditorModel { Nivel2 = "00", Nivel3 = "00" };
  }

  private void SelectCuenta(CuentaContableNodeDto cuenta)
  {
    selectedCuentaId = cuenta.Id;
    isNewCuenta = false;
    CuentaEditor = new CuentaEditorModel
    {
      Nivel1 = cuenta.Nivel1,
      Nivel2 = cuenta.Nivel2,
      Nivel3 = cuenta.Nivel3,
      Descripcion = cuenta.Descripcion
    };
  }

  private async Task SaveCuentaAsync()
  {
    isSaving = true;
    try
    {
      var result = await CatalogoService.SaveCuentaAsync(new CuentaContableSaveRequest
      {
        Id = selectedCuentaId,
        Rfc = CurrentRfc,
        Nivel1 = CuentaEditor.Nivel1,
        Nivel2 = CuentaEditor.Nivel2,
        Nivel3 = CuentaEditor.Nivel3,
        Descripcion = CuentaEditor.Descripcion
      });

      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      var keepId = result.EntityId;
      await LoadCuentasAsync();

      var reselected = Cuentas.FirstOrDefault(cuenta => cuenta.Id == keepId);
      if (reselected is not null)
      {
        SelectCuenta(reselected);
      }
      else
      {
        NewCuenta();
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar la cuenta: {ex.Message}");
    }
    finally
    {
      isSaving = false;
      StateHasChanged();
    }
  }

  private async Task DeleteCuentaAsync(int id)
  {
    var cuenta = Cuentas.FirstOrDefault(item => item.Id == id);
    if (cuenta is null)
    {
      return;
    }

    if (!await ConfirmAsync(
          $"Se eliminará la cuenta {cuenta.Clave} \"{cuenta.Descripcion}\". Esta acción no se puede deshacer. ¿Continuar?"))
    {
      return;
    }

    isSaving = true;
    try
    {
      var result = await CatalogoService.DeleteCuentaAsync(CurrentRfc, id);
      if (!result.Success)
      {
        UiMessages.ShowWarning(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      NewCuenta();
      await LoadCuentasAsync();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar la cuenta: {ex.Message}");
    }
    finally
    {
      isSaving = false;
      StateHasChanged();
    }
  }

  /// <summary>Rows are clickable, so they must also be operable from the keyboard.</summary>
  private static void OnRowKeyDown(KeyboardEventArgs args, Action select)
  {
    if (args.Key is "Enter" or " ")
    {
      select();
    }
  }

  private async Task<bool> ConfirmAsync(string message)
    => await JsRuntime.InvokeAsync<bool>("confirm", message);

  private static string TabKey(CatalogoKey key) => key.ToString().ToLowerInvariant();

  private bool IsKnownTab(string value)
    => value == CuentasTab || Descriptors.Any(descriptor => TabKey(descriptor.Key) == value);

  private string CurrentRfc => RfcState.RequireRfc();

  private sealed class CatalogoItemEditorModel
  {
    [StringLength(100, ErrorMessage = "El código no puede exceder 100 caracteres.")]
    public string? Codigo { get; set; }

    [Required(ErrorMessage = "Captura un valor.")]
    [StringLength(400, ErrorMessage = "El texto no puede exceder 400 caracteres.")]
    public string Nombre { get; set; } = string.Empty;

    [Range(0, 9999, ErrorMessage = "El orden debe estar entre 0 y 9999.")]
    public int Orden { get; set; }

    public bool Activo { get; set; } = true;
  }

  private sealed class CuentaEditorModel
  {
    [Required(ErrorMessage = "Nivel 1 es obligatorio.")]
    [StringLength(20, ErrorMessage = "Nivel 1 no puede exceder 20 caracteres.")]
    public string Nivel1 { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nivel 2 es obligatorio.")]
    [StringLength(2, MinimumLength = 1, ErrorMessage = "Nivel 2 usa dos caracteres.")]
    public string Nivel2 { get; set; } = "00";

    [Required(ErrorMessage = "Nivel 3 es obligatorio.")]
    [StringLength(2, MinimumLength = 1, ErrorMessage = "Nivel 3 usa dos caracteres.")]
    public string Nivel3 { get; set; } = "00";

    [Required(ErrorMessage = "La descripción es obligatoria.")]
    [StringLength(400, ErrorMessage = "La descripción no puede exceder 400 caracteres.")]
    public string Descripcion { get; set; } = string.Empty;
  }
}
