using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.State;
using System.Globalization;

namespace OrionERP.Web.Features.ReportesFinancieros.EstadoPerdidasGanancias
{
    public partial class EstadoPerdidasGananciasPage : ComponentBase, IDisposable
    {
        [Inject]
        private IUserRfcState RfcState { get; set; } = default!;

        [Inject]
        private IReportesFinancierosService ReportesService { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        private static readonly CultureInfo MexicanCulture = new("es-MX");

        private int _anio = DateTime.Now.Year;
        public int Anio
        {
            get => _anio;
            set
            {
                if (_anio == value)
                {
                    return;
                }

                _anio = value;
                SetDateRange();
                _ = LoadDataAsync();
            }
        }

        public int? Mes { get; private set; } = DateTime.Now.Month;
        public string? CurrentRfc { get; private set; }
        public bool IsLoading { get; private set; }
        public string? ErrorMessage { get; private set; }
        public List<EstadoPerdidasGananciasRow> Resultados { get; private set; } = new();
        public DateTime StartDate { get; private set; }
        public DateTime EndDate { get; private set; }

        private string MesSelectValue => Mes?.ToString() ?? string.Empty;

        protected override void OnInitialized()
        {
            RfcState.Changed += OnRfcStateChanged;
            CurrentRfc = RfcState.CurrentRfc;
            SetDateRange();
        }

        protected override async Task OnInitializedAsync()
        {
            await LoadDataAsync();
        }

        private async void OnRfcStateChanged()
        {
            CurrentRfc = RfcState.CurrentRfc;
            await LoadDataAsync();
            await InvokeAsync(StateHasChanged);
        }

        private async Task OnMesChanged(ChangeEventArgs e)
        {
            var value = e.Value?.ToString();
            Mes = int.TryParse(value, out var mes) ? mes : null;
            SetDateRange();
            await LoadDataAsync();
        }

        private void SetDateRange()
        {
            if (Mes.HasValue)
            {
                StartDate = new DateTime(Anio, Mes.Value, 1);
                EndDate = StartDate.AddMonths(1).AddDays(-1);
                return;
            }

            StartDate = new DateTime(Anio, 1, 1);
            EndDate = new DateTime(Anio, 12, 31);
        }

        private async Task LoadDataAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentRfc))
            {
                Resultados.Clear();
                await InvokeAsync(StateHasChanged);
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            await InvokeAsync(StateHasChanged);

            try
            {
                var rows = await ReportesService.GetEstadoPerdidasGananciasAsync(StartDate, EndDate, CurrentRfc);
                Resultados = rows.OrderBy(r => r.ID).ToList();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                Resultados.Clear();
            }
            finally
            {
                IsLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private string FormatMonto(decimal value) => value.ToString("N2", MexicanCulture);

        private async Task PrintAsync()
        {
            await JS.InvokeVoidAsync(
                "orionPrintReport",
                "estado-perdidas-ganancias-print-root",
                $"Estado de Perdidas y Ganancias {CurrentRfc}",
                $"Del {StartDate:dd/MM/yyyy} al {EndDate:dd/MM/yyyy}");
        }

        public void Dispose()
        {
            RfcState.Changed -= OnRfcStateChanged;
        }
    }
}
