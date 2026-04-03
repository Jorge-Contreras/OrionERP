using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
            Filter.Year = DateTime.Now.Year;
            Filter.Month = DateTime.Now.Month;
            await LoadTransacciones();
        }

        protected async Task Search()
        {
            await LoadTransacciones();
        }

        protected async Task ClearFilters()
        {
            Filter = new TransaccionFilter();
            Filter.Month ??= DateTime.Now.Month;
            Filter.Year ??= DateTime.Now.Year;
            await LoadTransacciones();
        }

        protected async Task Sort(string columnName)
        {
            if (Filter.SortBy == columnName)
            {
                Filter.SortAsc = !Filter.SortAsc;
            }
            else
            {
                Filter.SortBy = columnName;
                Filter.SortAsc = true;
            }
            await LoadTransacciones();
        }

        protected async Task OnFilterKeyDown(KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
            {
                await Search();
            }
        }

        protected async Task OnFilterSelectionChanged()
        {
            await Search();
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
