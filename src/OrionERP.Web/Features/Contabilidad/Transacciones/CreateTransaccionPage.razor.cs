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
    public partial class CreateTransaccionPage : ComponentBase, IDisposable
    {
        [Inject]
        public ITransaccionService TransaccionService { get; set; } = default!;
        [Inject]
        public IUiMessageService UiMessages { get; set; } = default!;
        [Inject]
        public NavigationManager NavManager { get; set; } = default!;
        [Inject]
        public IUserRfcState RfcState { get; set; } = default!;

        protected TransaccionCreateRequest Model { get; set; } = new();
        protected EditContext EditContext { get; set; } = default!;
        protected bool IsSaving { get; set; }

        protected List<FormaPagoLookupDto> FormaPagoOptions { get; } = new();
        protected IReadOnlyList<string> TipoPolizaOptions { get; } = new[] { "INGRESO", "EGRESO", "DIARIO" };
        private string? _lastAppliedRfc;
        private bool _isDisposed;

        protected override async Task OnInitializedAsync()
        {
            Model.Fecha = DateTime.Today;
            ApplyCurrentRfc(force: true);
            EditContext = new EditContext(Model);
            RfcState.Changed += OnRfcStateChanged;

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

        private void ApplyCurrentRfc(bool force = false)
        {
            var nextRfc = NormalizeRfc(RfcState.CurrentRfc)
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(nextRfc))
            {
                return;
            }

            var currentRfc = NormalizeRfc(Model.Rfc);
            var canReplace = force
                || string.IsNullOrWhiteSpace(currentRfc)
                || string.Equals(currentRfc, _lastAppliedRfc, StringComparison.OrdinalIgnoreCase);

            if (!canReplace || string.Equals(currentRfc, nextRfc, StringComparison.OrdinalIgnoreCase))
            {
                _lastAppliedRfc = nextRfc;
                return;
            }

            Model.Rfc = nextRfc;
            _lastAppliedRfc = nextRfc;
        }

        private async void OnRfcStateChanged()
        {
            if (_isDisposed) return;

            await InvokeAsync(() =>
            {
                var before = Model.Rfc;
                ApplyCurrentRfc();

                if (!string.Equals(before, Model.Rfc, StringComparison.OrdinalIgnoreCase))
                {
                    EditContext.NotifyFieldChanged(FieldIdentifier.Create(() => Model.Rfc));
                }

                StateHasChanged();
            });
        }

        private static string? NormalizeRfc(string? rfc)
            => string.IsNullOrWhiteSpace(rfc) ? null : rfc.Trim();

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            RfcState.Changed -= OnRfcStateChanged;
        }
    }
}
