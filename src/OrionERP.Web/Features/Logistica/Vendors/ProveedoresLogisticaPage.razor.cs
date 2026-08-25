using OrionERP.Application.Common;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Web.Services;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Vendors;

public partial class ProveedoresLogisticaPage : ComponentBase
{
  [Inject] private IBusinessPartnerService BusinessPartnerService { get; set; } = default!;
  [Inject] private IUiMessageService UiMessages { get; set; } = default!;
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;

  protected BusinessPartnerFilter Filter { get; set; } = new() { VendorOnly = true };
  protected BusinessPartnerCatalogDto Catalog { get; set; } = new();
  protected List<BusinessPartnerListItemDto> Partners { get; set; } = [];
  protected BusinessPartnerUpsertRequest Editor { get; set; } = CreateNewEditor(string.Empty);
  protected VendorProfileUpsertRequest VendorProfile { get; set; } = CreateVendorProfile();
  protected HashSet<string> SelectedRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase) { "Vendor" };
  protected int? SelectedPartnerId { get; set; }
  protected bool IsBusy { get; set; }
  protected bool IsSaving { get; set; }

  protected override async Task OnInitializedAsync()
  {
    Editor = CreateNewEditor(CurrentRfc);
    Catalog = await BusinessPartnerService.GetCatalogAsync(CurrentRfc);
    await BuscarAsync();
  }

  protected async Task BuscarAsync()
  {
    IsBusy = true;
    try
    {
      Filter.OwnerRfc = CurrentRfc;
      Partners = (await BusinessPartnerService.GetPartnersAsync(Filter)).ToList();
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el maestro de socios. {ex.Message}");
    }
    finally
    {
      IsBusy = false;
      StateHasChanged();
    }
  }

  protected void NuevoRegistro()
  {
    SelectedPartnerId = null;
    Editor = CreateNewEditor(CurrentRfc);
    VendorProfile = CreateVendorProfile();
    SelectedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendor" };
  }

  protected async Task SeleccionarAsync(int businessPartnerId)
  {
    try
    {
      var detail = await BusinessPartnerService.GetPartnerAsync(CurrentRfc, businessPartnerId);
      if (detail is null)
      {
        UiMessages.ShowWarning("El socio seleccionado ya no existe.");
        return;
      }

      SelectedPartnerId = detail.Id;
      Editor = new BusinessPartnerUpsertRequest
      {
        OwnerRfc = CurrentRfc,
        Id = detail.Id,
        LegacyProveedorId = detail.LegacyProveedorId,
        DisplayName = detail.DisplayName,
        Rfc = detail.Rfc,
        Email = detail.Email,
        Phone = detail.Phone,
        Street = detail.Street,
        Neighborhood = detail.Neighborhood,
        City = detail.City,
        State = detail.State,
        PostalCode = detail.PostalCode,
        BusinessLine = detail.BusinessLine,
        Notes = detail.Notes,
        IsActive = detail.IsActive
      };

      VendorProfile = new VendorProfileUpsertRequest
      {
        PaymentTerms = detail.VendorProfile?.PaymentTerms,
        DefaultLeadTimeDays = detail.VendorProfile?.DefaultLeadTimeDays,
        IsApproved = detail.VendorProfile?.IsApproved ?? true,
        Notes = detail.VendorProfile?.Notes
      };

      SelectedRoles = new HashSet<string>(detail.Roles, StringComparer.OrdinalIgnoreCase);
      if (SelectedRoles.Count == 0)
      {
        SelectedRoles.Add("Vendor");
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo cargar el socio seleccionado. {ex.Message}");
    }
  }

  protected void ToggleRole(string roleCode, bool isChecked)
  {
    if (isChecked)
    {
      SelectedRoles.Add(roleCode);
      return;
    }

    SelectedRoles.Remove(roleCode);
  }

  protected async Task GuardarAsync()
  {
    IsSaving = true;
    try
    {
      Editor.Roles = SelectedRoles.ToArray();
      Editor.OwnerRfc = CurrentRfc;
      Editor.VendorProfile = VendorProfile;

      var result = await BusinessPartnerService.SavePartnerAsync(Editor);
      if (!result.Success)
      {
        UiMessages.ShowError(result.Message);
        return;
      }

      UiMessages.ShowSuccess(result.Message);
      await BuscarAsync();

      if (result.EntityId.HasValue)
      {
        await SeleccionarAsync(result.EntityId.Value);
      }
    }
    catch (Exception ex)
    {
      UiMessages.ShowError($"No se pudo guardar el socio. {ex.Message}");
    }
    finally
    {
      IsSaving = false;
    }
  }

  private static BusinessPartnerUpsertRequest CreateNewEditor(string ownerRfc)
    => new()
    {
      OwnerRfc = ownerRfc,
      IsActive = true,
      DisplayName = string.Empty
    };

  private static VendorProfileUpsertRequest CreateVendorProfile()
    => new()
    {
      IsApproved = true
    };

  private string CurrentRfc => RfcState.RequireRfc();
}
