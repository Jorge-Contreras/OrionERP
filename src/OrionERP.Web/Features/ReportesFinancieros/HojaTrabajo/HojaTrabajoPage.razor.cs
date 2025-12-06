using Microsoft.AspNetCore.Components;
using OrionERP.Web.State;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using OrionERP.Application.Features.ReportesFinancieros;
using Microsoft.JSInterop;

namespace OrionERP.Web.Features.ReportesFinancieros.HojaTrabajo
{
    public partial class HojaTrabajoPage : ComponentBase, IDisposable
    {
        [Inject]
        private IJSRuntime JS { get; set; } = default!;
        [Inject]
        private IUserRfcState RfcState { get; set; } = default!;

        [Inject]
        private IReportesFinancierosService ReportesService { get; set; } = default!;

        private int _anio = DateTime.Now.Year;
        public int Anio
        {
            get => _anio;
            set
            {
                if (_anio != value)
                {
                    _anio = value;
                    _ = LoadHojaTrabajoData();
                }
            }
        }

        public string? CurrentRfc { get; private set; }
        public bool IsLoading { get; private set; }
        public bool IsExporting { get; private set; }
        public string? ErrorMessage { get; private set; }
        public List<HojaTrabajoTablaDto> HojaTrabajoCfdi { get; private set; } = new();
        public List<HojaTrabajoTablaDto> HojaTrabajoComplementos { get; private set; } = new();
        public List<HojaTrabajoTablaDto> HojaTrabajoContabilidad { get; private set; } = new();
        public List<HojaTrabajoTablaDto> HojaTrabajoAcumulados { get; private set; } = new();
        public HojaTrabajoTab ActiveTab { get; private set; } = HojaTrabajoTab.Cfdi;

        private readonly Dictionary<HojaTrabajoTab, HojaTrabajoTablaDto?> _selectedRows = new();

        private static readonly CultureInfo MexicanCulture = new("es-MX");

        protected override void OnInitialized()
        {
            RfcState.Changed += OnRfcStateChanged;
            CurrentRfc = RfcState.CurrentRfc;
        }

        protected override async Task OnInitializedAsync()
        {
            await LoadHojaTrabajoData();
        }

        private async void OnRfcStateChanged()
        {
            CurrentRfc = RfcState.CurrentRfc;
            await LoadHojaTrabajoData();
            await InvokeAsync(StateHasChanged);
        }

        private async Task LoadHojaTrabajoData()
        {
            if (string.IsNullOrEmpty(CurrentRfc))
            {
                HojaTrabajoCfdi.Clear();
                HojaTrabajoComplementos.Clear();
                HojaTrabajoContabilidad.Clear();
                HojaTrabajoAcumulados.Clear();
                _selectedRows.Clear();
                return;
            }

            IsLoading = true;
            ErrorMessage = null;
            await InvokeAsync(StateHasChanged);

            try
            {
                var result = await ReportesService.GetHojaTrabajoAsync(Anio, CurrentRfc);
                HojaTrabajoCfdi = result.Cfdi;
                HojaTrabajoComplementos = result.Complementos;
                HojaTrabajoContabilidad = result.Contabilidad;
                HojaTrabajoAcumulados = result.Acumulados;
                ActiveTab = HojaTrabajoTab.Cfdi;
                _selectedRows.Clear();
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

        private string GetRowClass(HojaTrabajoTablaDto row, List<HojaTrabajoTablaDto> data, HojaTrabajoTab tab)
        {
            var index = data.IndexOf(row);
            var baseClass = index switch
            {
                0 or 1 => "section-0",
                2 or 3 => "section-1",
                4 or 5 => "section-2",
                6 or 7 => "section-3",
                8 or 9 => "section-4",
                _ => "section-5",
            };

            if (TryGetSelectedRow(tab, out var selectedRow) && selectedRow == row)
            {
                return $"{baseClass} table-active fw-bold".Trim();
            }

            return baseClass;
        }

        private string GetCellClass(HojaTrabajoTablaDto row, int monthIndex)
        {
            var value = GetMonthValue(row, monthIndex);
            return value < 0 ? "negative-number" : string.Empty;
        }

        private string GetFormattedValue(HojaTrabajoTablaDto row, int monthIndex)
        {
            var value = GetMonthValue(row, monthIndex);
            var format = row.Descripcion == "COEFICIENTE_UTILIDAD" ? "N4" : "N2";
            return value.ToString(format, MexicanCulture);
        }

        private decimal GetMonthValue(HojaTrabajoTablaDto row, int monthIndex)
        {
            return monthIndex switch
            {
                0 => row.ENERO,
                1 => row.FEBRERO,
                2 => row.MARZO,
                3 => row.ABRIL,
                4 => row.MAYO,
                5 => row.JUNIO,
                6 => row.JULIO,
                7 => row.AGOSTO,
                8 => row.SEPTIEMBRE,
                9 => row.OCTUBRE,
                10 => row.NOVIEMBRE,
                11 => row.DICIEMBRE,
                _ => 0
            };
        }

        public void Dispose()
        {
            RfcState.Changed -= OnRfcStateChanged;
        }

        private void SelectRow(HojaTrabajoTablaDto row, HojaTrabajoTab tab)
        {
            _selectedRows[tab] = row;
        }

        private bool TryGetSelectedRow(HojaTrabajoTab tab, out HojaTrabajoTablaDto? row)
        {
            return _selectedRows.TryGetValue(tab, out row);
        }

        private void SetActiveTab(HojaTrabajoTab tab)
        {
            ActiveTab = tab;
        }
    }

    public enum HojaTrabajoTab
    {
        Cfdi,
        Complementos,
        Contabilidad,
        Acumulados
    }
}
