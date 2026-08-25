using OrionERP.Application.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Contabilidad.Transacciones
{
    public partial class CreateTransaccionPage : ComponentBase
    {
        [Inject]
        public ITransaccionService TransaccionService { get; set; } = default!;
        [Inject]
        public IUiMessageService UiMessages { get; set; } = default!;
        [Inject]
        public NavigationManager NavManager { get; set; } = default!;
        [Inject]
        public ICurrentCompanyContext RfcState { get; set; } = default!;

        protected TransaccionCreateRequest Model { get; set; } = new();
        protected EditContext EditContext { get; set; } = default!;
        protected bool IsSaving { get; set; }

        protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = new();
        protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };
        protected override async Task OnInitializedAsync()
        {
            Model.Fecha = DateTime.Today;
            Model.Rfc = RfcState.RequireRfc();
            EditContext = new EditContext(Model);
            var formasPago = await TransaccionService.GetFormasPagoAsync();
            FormaPagoOptions.AddRange(formasPago);
        }

        protected async Task HandleValidSubmit()
        {
            IsSaving = true;
            try
            {
                var result = await TransaccionService.CreateTransaccionAsync(Model);
                if (result.Success)
                {
                    UiMessages.ShowSuccess(result.Message ?? "Transacción creada con éxito.");
                    NavManager.NavigateTo($"/contabilidad/transacciones/{result.NewTransaccionId}");
                }
                else
                {
                    UiMessages.ShowError(result.Message ?? "Error al crear la transacción.");
                }
            }
            catch (Exception ex)
            {
                UiMessages.ShowError($"Error: {ex.Message}");
            }
            finally
            {
                IsSaving = false;
            }
        }

    }
}
