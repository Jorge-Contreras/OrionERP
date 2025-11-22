using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Contabilidad.Transacciones
{
    public partial class TransaccionesListPage : ComponentBase, IDisposable
    {
        [Inject]
        public ITransaccionService TransaccionService { get; set; } = default!;
        [Inject]
        public IUserRfcState RfcState { get; set; } = default!;

        protected bool IsLoading { get; set; }
        protected List<TransaccionListItemDto> Transacciones { get; set; } = new();
        protected TransaccionFilter Filter { get; set; } = new();
        private bool _isDisposed;

        protected override async Task OnInitializedAsync()
        {
            RfcState.Changed += OnRfcStateChanged;
            await LoadTransacciones();
        }

        protected async Task Search()
        {
            await LoadTransacciones();
        }

        protected async Task ClearFilters()
        {
            Filter = new TransaccionFilter();
            await LoadTransacciones();
        }

        private async Task LoadTransacciones()
        {
            IsLoading = true;
            StateHasChanged();

            Filter.Rfc = RfcState.CurrentRfc;
            var result = await TransaccionService.GetTransaccionesListAsync(Filter);
            Transacciones = result.ToList();

            IsLoading = false;
            StateHasChanged();
        }

        private async void OnRfcStateChanged()
        {
            if (_isDisposed) return;
            await InvokeAsync(LoadTransacciones);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            RfcState.Changed -= OnRfcStateChanged;
        }
    }
}
