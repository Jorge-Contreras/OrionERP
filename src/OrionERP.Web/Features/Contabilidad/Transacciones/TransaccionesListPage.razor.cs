using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Web.Features.Contabilidad.Transacciones
{
    public partial class TransaccionesListPage : ComponentBase
    {
        [Inject]
        public ITransaccionService TransaccionService { get; set; } = default!;

        protected bool IsLoading { get; set; }
        protected List<TransaccionListItemDto> Transacciones { get; set; } = new();
        protected TransaccionFilter Filter { get; set; } = new();

        protected override async Task OnInitializedAsync()
        {
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
            Transacciones = (await TransaccionService.GetTransaccionesListAsync(Filter)).ToList();
            IsLoading = false;
        }
    }
}
