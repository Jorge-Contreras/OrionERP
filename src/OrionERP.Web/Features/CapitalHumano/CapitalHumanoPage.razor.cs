using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using OrionERP.Application.Features.CapitalHumano;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.CapitalHumano;

public partial class CapitalHumanoPage : ComponentBase, IDisposable
{
  private const int PhotoMaxPixels = 640;
  private const int PageSize = 50;
  private const int QueryTake = PageSize + 1;

  [Inject] private ICapitalHumanoService CapitalHumanoService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private IUserRfcState RfcState { get; set; } = default!;
  [Inject] private IJSRuntime JS { get; set; } = default!;

  protected CapitalHumanoFilter Filter { get; set; } = new();
  protected CapitalHumanoCatalogDto Catalog { get; set; } = new();
  protected List<CapitalHumanoListItemDto> Employees { get; set; } = [];
  protected CapitalHumanoSaveRequest Editor { get; set; } = CreateNewEditor();
  protected Dictionary<int, string> EmployeePhotoDataUrls { get; set; } = [];
  protected List<CapitalHumanoAttachmentDto> Attachments { get; set; } = [];
  protected int? SelectedEmployeeId { get; set; }
  protected string? PhotoPreviewDataUrl { get; set; }
  protected string? SelectedPhotoFileName { get; set; }
  protected string AttachmentDescription { get; set; } = string.Empty;
  protected string? PendingAttachmentFileName { get; set; }
  protected string AttachmentEditFileName { get; set; } = string.Empty;
  protected string AttachmentEditDescription { get; set; } = string.Empty;
  protected string? ReplacementAttachmentFileName { get; set; }
  protected CapitalHumanoDetailDto? SelectedDetail { get; set; }
  protected bool HasExecutedSearch { get; set; }
  protected bool HasMoreEmployees { get; set; }
  protected bool IsBusy { get; set; }
  protected bool IsLoadingMore { get; set; }
  protected bool IsSaving { get; set; }
  protected bool IsDeactivating { get; set; }
  protected bool IsAttachmentsExpanded { get; set; } = true;
  protected bool IsUploadingAttachment { get; set; }
  protected bool IsUpdatingAttachment { get; set; }
  protected string? LoadError { get; set; }
  protected bool IsListBusy => IsBusy || IsLoadingMore;
  protected bool CanManageAttachments => Editor.Id.HasValue && !IsSaving && !IsDeactivating;
  protected bool CanSaveAttachmentEdit => EditingAttachmentId.HasValue
    && CanManageAttachments
    && !IsUpdatingAttachment
    && !string.IsNullOrWhiteSpace(AttachmentEditFileName);
  protected bool CanDeactivate => Editor.Id.HasValue
    && !IsSaving
    && !IsDeactivating
    && !string.Equals(Editor.Status, "INACTIVO", StringComparison.OrdinalIgnoreCase);

  private IBrowserFile? _pendingAttachment;
  private IBrowserFile? _pendingAttachmentReplacement;
  protected int AttachmentInputKey { get; set; }
  protected int ReplacementAttachmentInputKey { get; set; }
  protected int? EditingAttachmentId { get; set; }
  private int? _attachmentDownloadingId;
  private int? _attachmentDeletingId;

  protected string CurrentRfc => RfcState.CurrentRfc ?? RfcState.AllowedRfcs.FirstOrDefault() ?? "OHM191112Q26";

  protected bool HasPhotoOnly
  {
    get => Filter.HasPhoto ?? false;
    set => Filter.HasPhoto = value ? true : null;
  }

  protected string DisplayName
  {
    get
    {
      var parts = new[] { Editor.Nombre, Editor.ApellidoPaterno, Editor.ApellidoMaterno }
        .Where(part => !string.IsNullOrWhiteSpace(part));
      var fullName = string.Join(" ", parts);
      return string.IsNullOrWhiteSpace(fullName) ? "Nuevo colaborador" : fullName;
    }
  }

  protected int? DerivedAge
  {
    get
    {
      if (!Editor.Fecha_Nacimiento.HasValue)
      {
        return null;
      }

      var today = DateTime.Today;
      var age = today.Year - Editor.Fecha_Nacimiento.Value.Year;
      if (Editor.Fecha_Nacimiento.Value.Date > today.AddYears(-age))
      {
        age--;
      }

      return age < 0 ? null : age;
    }
  }

  protected override async Task OnInitializedAsync()
  {
    RfcState.Changed += OnRfcStateChanged;
    EnsureCurrentRfc();
    await LoadCatalogAsync();
    NuevoEmpleado();
  }

  protected async Task BuscarAsync()
  {
    EnsureCurrentRfc();
    HasExecutedSearch = true;
    IsBusy = true;
    LoadError = null;
    Employees = [];
    EmployeePhotoDataUrls = [];
    HasMoreEmployees = false;

    try
    {
      var page = await GetEmployeePageAsync(0);
      Employees = page.Items;
      HasMoreEmployees = page.HasMore;
      await LoadPhotosAsync(page.Items, append: false);
    }
    catch (Exception ex)
    {
      LoadError = ex.Message;
      EmployeePhotoDataUrls = [];
      UiMessages.ShowError($"No se pudo cargar Capital Humano. {ex.Message}");
    }
    finally
    {
      IsBusy = false;
      StateHasChanged();
    }
  }

  protected async Task CargarMasAsync()
  {
    if (IsListBusy || !HasMoreEmployees)
    {
      return;
    }

    IsLoadingMore = true;
    try
    {
      var page = await GetEmployeePageAsync(Employees.Count);
      Employees.AddRange(page.Items);
      HasMoreEmployees = page.HasMore;
      await LoadPhotosAsync(page.Items, append: true);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudieron cargar mas empleados. {ex.Message}");
    }
    finally
    {
      IsLoadingMore = false;
      StateHasChanged();
    }
  }

  protected void NuevoEmpleado()
  {
    EnsureCurrentRfc();
    SelectedEmployeeId = null;
    SelectedDetail = null;
    PhotoPreviewDataUrl = null;
    SelectedPhotoFileName = null;
    Editor = CreateNewEditor();
    Editor.Rfc = CurrentRfc;
    ClearAttachmentState();
  }

  protected async Task SeleccionarEmpleadoAsync(int employeeId)
  {
    EnsureCurrentRfc();
    try
    {
      var detail = await CapitalHumanoService.GetEmployeeAsync(employeeId, CurrentRfc);
      if (detail is null)
      {
        UiMessages.ShowWarning("El empleado seleccionado ya no existe para el RFC actual.");
        return;
      }

      SelectedEmployeeId = detail.Id;
      SelectedDetail = detail;
      Editor = MapToEditor(detail);
      await LoadPhotoPreviewAsync(detail.Id);
      await RefreshAttachmentsAsync(detail.Id);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el empleado. {ex.Message}");
    }
  }

  protected async Task GuardarAsync()
  {
    EnsureCurrentRfc();
    Editor.Rfc = CurrentRfc;
    NormalizeEditorForSave();

    IsSaving = true;
    try
    {
      var result = await CapitalHumanoService.SaveEmployeeAsync(Editor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await LoadCatalogAsync();

      if (HasExecutedSearch)
      {
        await BuscarAsync();
      }

      if (result.EntityId.HasValue)
      {
        await SeleccionarEmpleadoAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el empleado. {ex.Message}");
    }
    finally
    {
      IsSaving = false;
    }
  }

  protected async Task DeactivateAsync()
  {
    if (!Editor.Id.HasValue || IsDeactivating)
    {
      return;
    }

    var confirmed = await JS.InvokeAsync<bool>(
      "confirm",
      $"Se desactivara a {DisplayName} y se conservara su historial. ¿Deseas continuar?");
    if (!confirmed)
    {
      return;
    }

    IsDeactivating = true;
    try
    {
      var result = await CapitalHumanoService.DeactivateEmployeeAsync(Editor.Id.Value, CurrentRfc);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await BuscarAsync();
      await SeleccionarEmpleadoAsync(Editor.Id.Value);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo desactivar el empleado. {ex.Message}");
    }
    finally
    {
      IsDeactivating = false;
    }
  }

  protected async Task OnPhotoSelectedAsync(InputFileChangeEventArgs args)
  {
    var file = args.File;
    if (file is null)
    {
      return;
    }

    try
    {
      var resizedFile = await file.RequestImageFileAsync("image/jpeg", PhotoMaxPixels, PhotoMaxPixels);
      await using var stream = resizedFile.OpenReadStream(long.MaxValue);
      using var ms = new MemoryStream();
      await stream.CopyToAsync(ms);
      Editor.FotografiaBytes = ms.ToArray();
      SelectedPhotoFileName = file.Name;
      PhotoPreviewDataUrl = BuildDataUrl("image/jpeg", Editor.FotografiaBytes);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar la fotografia. {ex.Message}");
    }
  }

  protected void ToggleAttachments()
  {
    IsAttachmentsExpanded = !IsAttachmentsExpanded;
  }

  protected async Task OnAttachmentSelectedAsync(InputFileChangeEventArgs args)
  {
    _pendingAttachment = args.FileCount > 0 ? args.File : null;
    PendingAttachmentFileName = _pendingAttachment?.Name;
    await InvokeAsync(StateHasChanged);
  }

  protected async Task CargarAttachmentAsync()
  {
    if (!Editor.Id.HasValue)
    {
      UiMessages.ShowWarning("Guarda el colaborador antes de cargar archivos.");
      return;
    }

    if (_pendingAttachment is null)
    {
      UiMessages.ShowWarning("Selecciona un archivo.");
      return;
    }

    if (_pendingAttachment.Size > CapitalHumanoAttachmentCreateRequest.MaxFileSizeBytes)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      return;
    }

    IsUploadingAttachment = true;
    try
    {
      var content = await ReadAttachmentFileAsync(_pendingAttachment);
      await CapitalHumanoService.AddEmployeeAttachmentAsync(new CapitalHumanoAttachmentCreateRequest
      {
        EmployeeId = Editor.Id.Value,
        Rfc = CurrentRfc,
        FileName = _pendingAttachment.Name,
        Extension = Path.GetExtension(_pendingAttachment.Name).TrimStart('.'),
        Description = AttachmentDescription,
        Content = content
      });

      AttachmentDescription = string.Empty;
      _pendingAttachment = null;
      PendingAttachmentFileName = null;
      AttachmentInputKey++;
      await RefreshAttachmentsAsync(Editor.Id.Value);
      UiMessages.ShowSuccess("Archivo agregado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el archivo. {ex.Message}");
    }
    finally
    {
      IsUploadingAttachment = false;
    }
  }

  protected void EditarAttachment(CapitalHumanoAttachmentDto attachment)
  {
    EditingAttachmentId = attachment.Id;
    AttachmentEditFileName = attachment.AttachmentName;
    AttachmentEditDescription = attachment.AttachmentDescription;
    ReplacementAttachmentFileName = null;
    _pendingAttachmentReplacement = null;
    ReplacementAttachmentInputKey++;
  }

  protected void CancelarEdicionAttachment()
  {
    EditingAttachmentId = null;
    AttachmentEditFileName = string.Empty;
    AttachmentEditDescription = string.Empty;
    ReplacementAttachmentFileName = null;
    _pendingAttachmentReplacement = null;
    ReplacementAttachmentInputKey++;
  }

  protected async Task OnAttachmentReplacementSelectedAsync(InputFileChangeEventArgs args)
  {
    _pendingAttachmentReplacement = args.FileCount > 0 ? args.File : null;
    ReplacementAttachmentFileName = _pendingAttachmentReplacement?.Name;
    if (_pendingAttachmentReplacement is not null)
    {
      AttachmentEditFileName = _pendingAttachmentReplacement.Name;
    }

    await InvokeAsync(StateHasChanged);
  }

  protected async Task GuardarAttachmentEditAsync(CapitalHumanoAttachmentDto attachment)
  {
    if (!Editor.Id.HasValue || EditingAttachmentId != attachment.Id)
    {
      return;
    }

    if (string.IsNullOrWhiteSpace(AttachmentEditFileName))
    {
      UiMessages.ShowWarning("Ingresa el nombre del archivo.");
      return;
    }

    if (_pendingAttachmentReplacement is not null &&
        _pendingAttachmentReplacement.Size > CapitalHumanoAttachmentCreateRequest.MaxFileSizeBytes)
    {
      UiMessages.ShowError("El archivo excede el tamaño máximo permitido (5 MB).");
      return;
    }

    IsUpdatingAttachment = true;
    try
    {
      byte[]? replacementContent = null;
      var extension = Path.GetExtension(AttachmentEditFileName).TrimStart('.');
      if (_pendingAttachmentReplacement is not null)
      {
        replacementContent = await ReadAttachmentFileAsync(_pendingAttachmentReplacement);
        extension = Path.GetExtension(_pendingAttachmentReplacement.Name).TrimStart('.');
      }

      if (string.IsNullOrWhiteSpace(extension))
      {
        extension = attachment.AttachmentExtension;
      }

      await CapitalHumanoService.UpdateEmployeeAttachmentAsync(new CapitalHumanoAttachmentUpdateRequest
      {
        AttachmentId = attachment.Id,
        EmployeeId = Editor.Id.Value,
        Rfc = CurrentRfc,
        FileName = AttachmentEditFileName,
        Extension = extension,
        Description = AttachmentEditDescription,
        Content = replacementContent
      });

      CancelarEdicionAttachment();
      await RefreshAttachmentsAsync(Editor.Id.Value);
      UiMessages.ShowSuccess("Archivo actualizado.");
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo actualizar el archivo. {ex.Message}");
    }
    finally
    {
      IsUpdatingAttachment = false;
    }
  }

  protected async Task DescargarAttachmentAsync(CapitalHumanoAttachmentDto attachment)
  {
    _attachmentDownloadingId = attachment.Id;
    try
    {
      var content = await CapitalHumanoService.GetEmployeeAttachmentContentAsync(attachment.Id, CurrentRfc);
      if (content is null || content.Bytes.Length == 0)
      {
        UiMessages.ShowError("No se encontro el contenido del archivo.");
        return;
      }

      var dataUrl = $"data:{content.ContentType};base64,{Convert.ToBase64String(content.Bytes)}";
      await JS.InvokeVoidAsync("triggerFileDownload", content.FileName, dataUrl);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo descargar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDownloadingId = null;
    }
  }

  protected async Task EliminarAttachmentAsync(CapitalHumanoAttachmentDto attachment)
  {
    var confirmed = await JS.InvokeAsync<bool>("confirm", "¿Eliminar el archivo seleccionado?");
    if (!confirmed)
    {
      return;
    }

    _attachmentDeletingId = attachment.Id;
    try
    {
      var result = await CapitalHumanoService.DeleteEmployeeAttachmentAsync(attachment.Id, CurrentRfc);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      if (Editor.Id.HasValue)
      {
        await RefreshAttachmentsAsync(Editor.Id.Value);
      }

      if (EditingAttachmentId == attachment.Id)
      {
        CancelarEdicionAttachment();
      }

      UiMessages.ShowSuccess(result.Message);
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo eliminar el archivo. {ex.Message}");
    }
    finally
    {
      _attachmentDeletingId = null;
    }
  }

  protected string GetEmployeeRowClass(CapitalHumanoListItemDto item)
    => item.Id == SelectedEmployeeId ? "table-primary" : string.Empty;

  protected string FormatAuthLink(CapitalHumanoListItemDto item)
  {
    if (!item.HasAuthUser)
    {
      return "Sin usuario";
    }

    var name = string.IsNullOrWhiteSpace(item.AuthUserName) ? item.AuthEmail : item.AuthUserName;
    return item.AuthUserCount <= 1
      ? name ?? "Usuario ligado"
      : $"{item.AuthUserCount} usuarios ligados";
  }

  protected string? GetEmployeePhotoDataUrl(int employeeId)
    => EmployeePhotoDataUrls.TryGetValue(employeeId, out var dataUrl) ? dataUrl : null;

  protected static string FormatDate(DateTime? value)
    => value.HasValue ? value.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "-";

  protected static string FormatStatusBadge(string? status)
  {
    var normalized = status?.Trim().ToUpperInvariant();
    return normalized == "ACTIVO" ? "text-bg-success" : "text-bg-secondary";
  }

  private async Task LoadCatalogAsync()
  {
    EnsureCurrentRfc();
    Catalog = await CapitalHumanoService.GetCatalogAsync(CurrentRfc);
  }

  private async Task LoadPhotoPreviewAsync(int employeeId)
  {
    var photo = await CapitalHumanoService.GetPhotoAsync(employeeId, CurrentRfc);
    PhotoPreviewDataUrl = photo is null ? null : BuildDataUrl(photo.ContentType, photo.Bytes);
    SelectedPhotoFileName = photo is null ? null : photo.FileName;
    Editor.FotografiaBytes = null;
  }

  private async Task<(List<CapitalHumanoListItemDto> Items, bool HasMore)> GetEmployeePageAsync(int skip)
  {
    var rows = (await CapitalHumanoService.GetEmployeesAsync(CreateQueryFilter(skip, QueryTake))).ToList();
    var hasMore = rows.Count > PageSize;
    if (hasMore)
    {
      rows = rows.Take(PageSize).ToList();
    }

    return (rows, hasMore);
  }

  private CapitalHumanoFilter CreateQueryFilter(int skip, int take)
    => new()
    {
      Rfc = CurrentRfc,
      SearchText = Filter.SearchText,
      Status = Filter.Status,
      Puesto = Filter.Puesto,
      HasPhoto = Filter.HasPhoto,
      Skip = skip,
      Take = take
    };

  private async Task LoadPhotosAsync(IEnumerable<CapitalHumanoListItemDto> employees, bool append)
  {
    if (!append)
    {
      EmployeePhotoDataUrls = [];
    }

    var ids = employees
      .Where(employee => employee.HasPhoto)
      .Select(employee => employee.Id)
      .Distinct()
      .Where(id => !append || !EmployeePhotoDataUrls.ContainsKey(id))
      .ToArray();

    if (ids.Length == 0)
    {
      return;
    }

    try
    {
      var photos = await CapitalHumanoService.GetThumbnailsAsync(CurrentRfc, ids);
      foreach (var photo in photos)
      {
        EmployeePhotoDataUrls[photo.Id] = BuildDataUrl(photo.ContentType, photo.Bytes);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowWarning($"No se pudieron cargar algunas fotografias. {ex.Message}");
    }
  }

  protected bool IsAttachmentDownloading(CapitalHumanoAttachmentDto attachment)
    => _attachmentDownloadingId == attachment.Id;

  protected bool IsAttachmentDeleting(CapitalHumanoAttachmentDto attachment)
    => _attachmentDeletingId == attachment.Id;

  protected bool IsEditingAttachment(CapitalHumanoAttachmentDto attachment)
    => EditingAttachmentId == attachment.Id;

  protected static string FormatFileSize(long bytes)
  {
    if (bytes < 1024)
    {
      return $"{bytes} B";
    }

    var kiloBytes = bytes / 1024d;
    if (kiloBytes < 1024)
    {
      return $"{kiloBytes:0.#} KB";
    }

    var megaBytes = kiloBytes / 1024d;
    return $"{megaBytes:0.##} MB";
  }

  private async Task RefreshAttachmentsAsync(int employeeId)
  {
    Attachments = (await CapitalHumanoService.GetEmployeeAttachmentsAsync(employeeId, CurrentRfc)).ToList();
  }

  private void ClearAttachmentState()
  {
    Attachments = [];
    AttachmentDescription = string.Empty;
    PendingAttachmentFileName = null;
    _pendingAttachment = null;
    CancelarEdicionAttachment();
    AttachmentInputKey++;
  }

  private static async Task<byte[]> ReadAttachmentFileAsync(IBrowserFile file)
  {
    await using var stream = file.OpenReadStream(CapitalHumanoAttachmentCreateRequest.MaxFileSizeBytes);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);
    return ms.ToArray();
  }

  private void NormalizeEditorForSave()
  {
    Editor.RFC_Capital_Humano = ToUpperTrim(Editor.RFC_Capital_Humano);
    Editor.CURP = ToUpperTrim(Editor.CURP);
    Editor.Tipo_Sangre = ToUpperTrim(Editor.Tipo_Sangre);
    Editor.Sexo = ToUpperTrim(Editor.Sexo);
    Editor.Status = ToUpperTrim(Editor.Status) ?? "ACTIVO";
  }

  private void EnsureCurrentRfc()
  {
    if (string.IsNullOrWhiteSpace(RfcState.CurrentRfc))
    {
      RfcState.ResetToDefault();
    }

    Filter.Rfc = CurrentRfc;
  }

  private async void OnRfcStateChanged()
  {
    await InvokeAsync(async () =>
    {
      EnsureCurrentRfc();
      await LoadCatalogAsync();
      NuevoEmpleado();
      HasExecutedSearch = false;
      Employees = [];
      EmployeePhotoDataUrls = [];
      StateHasChanged();
    });
  }

  private static CapitalHumanoSaveRequest MapToEditor(CapitalHumanoDetailDto detail)
    => new()
    {
      Id = detail.Id,
      Rfc = detail.Rfc,
      Nombre = detail.Nombre,
      ApellidoPaterno = detail.ApellidoPaterno,
      ApellidoMaterno = detail.ApellidoMaterno,
      NombreCorto = detail.NombreCorto,
      Status = detail.Status,
      CURP = detail.CURP,
      Fecha_Nacimiento = detail.Fecha_Nacimiento,
      RFC_Capital_Humano = detail.RFC_Capital_Humano,
      Seguro_Social = detail.Seguro_Social,
      Calle = detail.Calle,
      Colonia = detail.Colonia,
      Comunidad = detail.Comunidad,
      Ciudad = detail.Ciudad,
      Estado = detail.Estado,
      Tipo_Sangre = detail.Tipo_Sangre,
      Telefono = detail.Telefono,
      Numero_Emergencia = detail.Numero_Emergencia,
      Sueldo_Mensual = detail.Sueldo_Mensual,
      Puesto = detail.Puesto,
      Sexo = detail.Sexo,
      Dependientes = detail.Dependientes,
      Beneficiarios = detail.Beneficiarios,
      Fecha_Alta = detail.Fecha_Alta,
      Fecha_Baja = detail.Fecha_Baja,
      Nacionalidad = detail.Nacionalidad,
      Tipo_Contrato = detail.Tipo_Contrato,
      Sede_Contratada = detail.Sede_Contratada,
      Jornada = detail.Jornada,
      Lactancia = detail.Lactancia,
      Horario_Alimentos = detail.Horario_Alimentos,
      Esquema_Pagos = detail.Esquema_Pagos,
      Tipo_Capital_Humano = detail.Tipo_Capital_Humano,
      Nivel_Maximo_Estudios = detail.Nivel_Maximo_Estudios,
      Descanso_Semanal = detail.Descanso_Semanal
    };

  private static CapitalHumanoSaveRequest CreateNewEditor()
    => new()
    {
      Status = "ACTIVO",
      Fecha_Alta = DateTime.Today,
      Nacionalidad = "MEXICANA"
    };

  private static string? ToUpperTrim(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType.Trim();
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }

  public void Dispose()
  {
    RfcState.Changed -= OnRfcStateChanged;
  }
}
