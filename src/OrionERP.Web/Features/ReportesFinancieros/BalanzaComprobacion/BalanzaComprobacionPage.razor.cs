using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.State;
using System.Globalization;
using Microsoft.AspNetCore.Http.Extensions;


namespace OrionERP.Web.Features.ReportesFinancieros.BalanzaComprobacion
{
    public partial class BalanzaComprobacionPage : ComponentBase, IDisposable
    {
        [Inject]
        private IUserRfcState RfcState { get; set; } = default!;

        [Inject]
        private IReportesFinancierosService ReportesService { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        [Inject]
        private NavigationManager Navigation { get; set; } = default!;

        private static readonly CultureInfo MexicanCulture = new("es-MX");

        public int Anio { get; set; } = DateTime.Now.Year;
        public int? Mes { get; set; } = DateTime.Now.Month;
        public string? CurrentRfc { get; private set; }
        public bool IsLoading { get; private set; }
        public string? ErrorMessage { get; private set; }
        public List<BalanzaComprobacionRow> Resultados { get; private set; } = new();
        private string? SelectedRowId { get; set; }

        private string MesSelectValue => Mes?.ToString() ?? string.Empty;
        private bool EsAnual => string.Equals(Resultados.FirstOrDefault()?.ModoReporte, "ANUAL", StringComparison.OrdinalIgnoreCase);

        private string DebeMesLabel => EsAnual ? "Debe Año" : "Debe Mes";
        private string HaberMesLabel => EsAnual ? "Haber Año" : "Haber Mes";
        private string SaldoMesLabel => EsAnual ? "Saldo Año" : "Saldo Mes";

        protected override void OnInitialized()
        {
            RfcState.Changed += OnRfcStateChanged;
            CurrentRfc = RfcState.CurrentRfc;
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

        private async Task OnAnioChanged(ChangeEventArgs e)
        {
            if (int.TryParse(e.Value?.ToString(), out var anio))
            {
                Anio = anio;
                await LoadDataAsync();
            }
        }

        private async Task OnMesChanged(ChangeEventArgs e)
        {
            var value = e.Value?.ToString();
            Mes = int.TryParse(value, out var mes) ? mes : null;
            await LoadDataAsync();
        }

        private async Task OnRfcChangedFromPicker(string? rfc)
        {
            CurrentRfc = rfc;
            await LoadDataAsync();
        }

        private async Task RefreshAsync()
        {
            await LoadDataAsync();
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
                var rows = await ReportesService.GetBalanzaComprobacionAsync(Anio, Mes, CurrentRfc);
                Resultados = rows.ToList();
                SelectedRowId = null;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }

        private IEnumerable<BalanzaComprobacionRow> OrderedRows => Resultados
            .OrderBy(r => r.Nivel1)
            .ThenBy(r => r.SortNivel2)
            .ThenBy(r => r.SortNivel3)
            .ThenBy(r => r.NivelJerarquia);

    private string PeriodoDescripcion
        {
            get
            {
                var firstRow = Resultados.FirstOrDefault();
                if (firstRow is null)
                {
                    return string.Empty;
                }

                if (firstRow.PeriodoInicio == default || firstRow.PeriodoFin == default)
                {
                    return string.Empty;
                }

                return $"{firstRow.PeriodoInicio:dd/MM/yyyy} - {firstRow.PeriodoFin:dd/MM/yyyy}";
            }
        }

        private string GetCuentaDescripcion(BalanzaComprobacionRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.Nombre_Cuenta))
            {
                return row.Nombre_Cuenta;
            }

            if (!string.IsNullOrWhiteSpace(row.Nivel3Descripcion))
            {
                return row.Nivel3Descripcion;
            }

            if (!string.IsNullOrWhiteSpace(row.Nivel2Descripcion))
            {
                return row.Nivel2Descripcion;
            }

            return row.Nivel1Descripcion ?? string.Empty;
        }

        private string GetRowClass(BalanzaComprobacionRow row)
        {
            var levelClass = row.NivelJerarquia switch
            {
                1 => "level-1",
                2 => "level-2",
                3 => "level-3",
                _ => string.Empty
            };

            return SelectedRowId == GetRowId(row)
                ? $"{levelClass} balanza-row-selected".Trim()
                : levelClass;
        }

        private void SelectRow(BalanzaComprobacionRow row)
        {
            SelectedRowId = GetRowId(row);
        }

        private static string GetRowId(BalanzaComprobacionRow row)
            => $"{row.Nivel1}|{row.Nivel2}|{row.Nivel3}|{row.NivelJerarquia}";

        private string GetIndentClass(BalanzaComprobacionRow row) => row.NivelJerarquia switch
        {
            1 => "balanza-indent-1",
            2 => "balanza-indent-2",
            3 => "balanza-indent-3",
            _ => string.Empty
        };

        private string FormatMonto(decimal value) => value.ToString("N2", MexicanCulture);

        private async Task PrintAsync()
        {
            await JS.InvokeVoidAsync(
                "orionPrintReport",
                "balanza-comprobacion-print-root",
                "Balanza de Comprobación",
                string.IsNullOrWhiteSpace(CurrentRfc) ? PeriodoDescripcion : $"RFC: {CurrentRfc}  Periodo: {PeriodoDescripcion}");
        }

        private async Task GoToContabilidadRegistros(BalanzaComprobacionRow row)
        {
            if (string.IsNullOrWhiteSpace(CurrentRfc))
            {
                return;
            }

            var query = new QueryBuilder
            {
                { "nivel1", row.Nivel1 },
            };

            if (!string.IsNullOrWhiteSpace(row.Nivel2))
            {
                query.Add("nivel2", row.Nivel2);
            }

            if (!string.IsNullOrWhiteSpace(row.Nivel3))
            {
                query.Add("nivel3", row.Nivel3);
            }

            query.Add("anio", Anio.ToString(CultureInfo.InvariantCulture));

            if (Mes.HasValue)
            {
                query.Add("mes", Mes.Value.ToString(CultureInfo.InvariantCulture));
            }

            query.Add("rfc", CurrentRfc);

            var registrosContablesUrl = $"/cfdi/registros-contables{query.ToQueryString()}";
            await JS.InvokeVoidAsync("open", registrosContablesUrl, "_blank");
        }

        public void Dispose()
        {
            RfcState.Changed -= OnRfcStateChanged;
        }
    }
}
