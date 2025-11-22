using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.Services;

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

        protected TransaccionCreateRequest Model { get; set; } = new();
        protected EditContext EditContext { get; set; } = default!;
        protected bool IsSaving { get; set; }

        protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = new();
        protected List<LookupInt32Dto> CategoriaOptions { get; } = new();
        protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };

        protected override async Task OnInitializedAsync()
        {
            Model.Fecha = DateTime.Today;
            EditContext = new EditContext(Model);

            var formasPago = await TransaccionService.GetFormasPagoAsync();
            FormaPagoOptions.AddRange(formasPago);

            // Note: This will load all categories. You may want to filter by RFC.
            // For now, we'll load them all as a placeholder.
            var categorias = await TransaccionService.GetCategoriasAsync("");
            CategoriaOptions.AddRange(categorias);
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
