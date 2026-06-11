using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Web.State;
using System.Globalization;
using System.Text;
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
        private string SearchText { get; set; } = string.Empty;
        private readonly HashSet<string> ExpandedAccountKeys = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> ParentAccountKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        private string MesSelectValue => Mes?.ToString() ?? string.Empty;
        private bool EsAnual => string.Equals(Resultados.FirstOrDefault()?.ModoReporte, "ANUAL", StringComparison.OrdinalIgnoreCase);
        private bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchText);

        private string DebeMesLabel => EsAnual ? "Debe Año" : "Debe Mes";
        private string HaberMesLabel => EsAnual ? "Haber Año" : "Haber Mes";
        private string SaldoMesLabel => EsAnual ? "Saldo Año" : "Saldo Mes";
        private decimal SummarySaldoInicial => Resultados.Where(row => row.NivelJerarquia == 1).Sum(row => row.Saldo_Inicial);
        private decimal SummaryDebe => Resultados.Where(row => row.NivelJerarquia == 1).Sum(row => row.Debe_Mes);
        private decimal SummaryHaber => Resultados.Where(row => row.NivelJerarquia == 1).Sum(row => row.Haber_Mes);
        private decimal SummarySaldoFinal => Resultados.Where(row => row.NivelJerarquia == 1).Sum(row => row.Saldo_Final);

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
                SelectedRowId = null;
                ExpandedAccountKeys.Clear();
                ParentAccountKeys.Clear();
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
                ExpandedAccountKeys.Clear();
                ParentAccountKeys = BuildParentAccountKeys(Resultados);
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

        private IEnumerable<BalanzaComprobacionRow> VisibleRows
        {
            get
            {
                if (!IsSearchActive)
                {
                    return OrderedRows.Where(IsVisibleByExpansion);
                }

                var visibleRowIds = GetSearchVisibleRowIds();
                return OrderedRows.Where(row => visibleRowIds.Contains(GetRowId(row)));
            }
        }

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

        private string GetCuentaDisplayName(BalanzaComprobacionRow row)
        {
            var descripcion = GetCuentaDescripcion(row);
            var codigo = GetCuentaCodigo(row);

            if (string.IsNullOrWhiteSpace(codigo))
            {
                return descripcion;
            }

            if (string.IsNullOrWhiteSpace(descripcion))
            {
                return codigo;
            }

            return $"{descripcion} - {codigo}";
        }

        private static string GetCuentaCodigo(BalanzaComprobacionRow row)
        {
            var segments = new List<string>();

            AddAccountSegment(segments, row.Nivel1);

            if (row.NivelJerarquia >= 2)
            {
                AddAccountSegment(segments, row.Nivel2);
            }

            if (row.NivelJerarquia >= 3)
            {
                AddAccountSegment(segments, row.Nivel3);
            }

            return string.Join("-", segments);
        }

        private static void AddAccountSegment(List<string> segments, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                segments.Add(value.Trim());
            }
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

            var classes = new List<string>();

            if (!string.IsNullOrWhiteSpace(levelClass))
            {
                classes.Add(levelClass);
            }

            if (HasChildren(row))
            {
                classes.Add("balanza-row--has-children");
            }

            if (IsExpanded(row))
            {
                classes.Add("balanza-row--expanded");
            }

            if (SelectedRowId == GetRowId(row))
            {
                classes.Add("balanza-row-selected");
            }

            if (IsSearchActive)
            {
                classes.Add(IsSearchMatch(row) ? "balanza-row--search-match" : "balanza-row--search-context");
            }

            return string.Join(" ", classes);
        }

        private void SelectRow(BalanzaComprobacionRow row)
        {
            SelectedRowId = GetRowId(row);
        }

        private static string GetRowId(BalanzaComprobacionRow row)
            => GetRowId(row.Nivel1, row.Nivel2, row.Nivel3, row.NivelJerarquia);

        private static string GetRowId(string? nivel1, string? nivel2, string? nivel3, int nivelJerarquia)
            => $"{nivel1}|{nivel2}|{nivel3}|{nivelJerarquia}";

        private string GetIndentClass(BalanzaComprobacionRow row) => row.NivelJerarquia switch
        {
            1 => "balanza-indent-1",
            2 => "balanza-indent-2",
            3 => "balanza-indent-3",
            _ => string.Empty
        };

        private string FormatMonto(decimal value) => value.ToString("N2", MexicanCulture);

        private bool IsVisibleByExpansion(BalanzaComprobacionRow row)
        {
            return row.NivelJerarquia switch
            {
                1 => true,
                2 => ExpandedAccountKeys.Contains(GetNivel1ExpansionKey(row.Nivel1)),
                3 => ExpandedAccountKeys.Contains(GetNivel1ExpansionKey(row.Nivel1))
                    && ExpandedAccountKeys.Contains(GetNivel2ExpansionKey(row.Nivel1, row.Nivel2)),
                _ => true
            };
        }

        private bool HasChildren(BalanzaComprobacionRow row)
            => row.NivelJerarquia is 1 or 2 && ParentAccountKeys.Contains(GetExpansionKey(row));

        private bool IsExpanded(BalanzaComprobacionRow row)
            => HasChildren(row) && ExpandedAccountKeys.Contains(GetExpansionKey(row));

        private string GetToggleIconClass(BalanzaComprobacionRow row)
            => IsExpanded(row) ? "bi bi-chevron-down" : "bi bi-chevron-right";

        private string GetAriaExpanded(BalanzaComprobacionRow row)
            => IsExpanded(row) ? "true" : "false";

        private string GetToggleLabel(BalanzaComprobacionRow row)
        {
            var action = IsExpanded(row) ? "Contraer" : "Expandir";
            return $"{action} {GetCuentaDisplayName(row)}";
        }

        private void ToggleRowExpansion(BalanzaComprobacionRow row)
        {
            if (!HasChildren(row))
            {
                return;
            }

            var key = GetExpansionKey(row);
            if (!ExpandedAccountKeys.Add(key))
            {
                ExpandedAccountKeys.Remove(key);
                CollapseDescendants(row);
            }
        }

        private void CollapseDescendants(BalanzaComprobacionRow row)
        {
            if (row.NivelJerarquia == 1)
            {
                ExpandedAccountKeys.RemoveWhere(key => key.StartsWith(GetNivel2ExpansionPrefix(row.Nivel1), StringComparison.OrdinalIgnoreCase));
            }
        }

        private void SetHierarchyDepth(int depth)
        {
            ExpandedAccountKeys.Clear();

            if (depth >= 2)
            {
                foreach (var row in Resultados.Where(row => row.NivelJerarquia == 1 && HasChildren(row)))
                {
                    ExpandedAccountKeys.Add(GetExpansionKey(row));
                }
            }

            if (depth >= 3)
            {
                foreach (var row in Resultados.Where(row => row.NivelJerarquia == 2 && HasChildren(row)))
                {
                    ExpandedAccountKeys.Add(GetExpansionKey(row));
                }
            }
        }

        private string GetHierarchyDepthButtonClass(int depth)
            => IsHierarchyDepthActive(depth) ? "btn btn-primary btn-sm" : "btn btn-outline-secondary btn-sm";

        private bool IsHierarchyDepthActive(int depth)
        {
            var expectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (depth >= 2)
            {
                foreach (var row in Resultados.Where(row => row.NivelJerarquia == 1 && HasChildren(row)))
                {
                    expectedKeys.Add(GetExpansionKey(row));
                }
            }

            if (depth >= 3)
            {
                foreach (var row in Resultados.Where(row => row.NivelJerarquia == 2 && HasChildren(row)))
                {
                    expectedKeys.Add(GetExpansionKey(row));
                }
            }

            return ExpandedAccountKeys.SetEquals(expectedKeys);
        }

        private void ClearSearch()
        {
            SearchText = string.Empty;
        }

        private HashSet<string> GetSearchVisibleRowIds()
        {
            var visibleRowIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in OrderedRows)
            {
                if (!IsSearchMatch(row))
                {
                    continue;
                }

                visibleRowIds.Add(GetRowId(row));

                foreach (var ancestorId in GetAncestorRowIds(row))
                {
                    visibleRowIds.Add(ancestorId);
                }
            }

            return visibleRowIds;
        }

        private IEnumerable<string> GetAncestorRowIds(BalanzaComprobacionRow row)
        {
            if (row.NivelJerarquia >= 2)
            {
                yield return GetRowId(row.Nivel1, null, null, 1);
            }

            if (row.NivelJerarquia >= 3)
            {
                yield return GetRowId(row.Nivel1, row.Nivel2, null, 2);
            }
        }

        private bool IsSearchMatch(BalanzaComprobacionRow row)
        {
            var search = NormalizeForSearch(SearchText);
            if (string.IsNullOrWhiteSpace(search))
            {
                return true;
            }

            var searchableText = string.Join(
                ' ',
                GetCuentaCodigo(row),
                GetCuentaDescripcion(row),
                row.Nombre_Cuenta,
                row.Nivel1,
                row.Nivel2,
                row.Nivel3,
                row.Nivel1Descripcion,
                row.Nivel2Descripcion,
                row.Nivel3Descripcion);

            return NormalizeForSearch(searchableText).Contains(search, StringComparison.Ordinal);
        }

        private static string NormalizeForSearch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToUpperInvariant(character));
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static HashSet<string> BuildParentAccountKeys(IEnumerable<BalanzaComprobacionRow> rows)
        {
            var parentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.NivelJerarquia == 2)
                {
                    parentKeys.Add(GetNivel1ExpansionKey(row.Nivel1));
                }
                else if (row.NivelJerarquia == 3)
                {
                    parentKeys.Add(GetNivel2ExpansionKey(row.Nivel1, row.Nivel2));
                }
            }

            return parentKeys;
        }

        private static string GetExpansionKey(BalanzaComprobacionRow row)
            => row.NivelJerarquia switch
            {
                1 => GetNivel1ExpansionKey(row.Nivel1),
                2 => GetNivel2ExpansionKey(row.Nivel1, row.Nivel2),
                _ => GetRowId(row)
            };

        private static string GetNivel1ExpansionKey(string? nivel1)
            => $"nivel1|{nivel1}";

        private static string GetNivel2ExpansionKey(string? nivel1, string? nivel2)
            => $"{GetNivel2ExpansionPrefix(nivel1)}{nivel2}";

        private static string GetNivel2ExpansionPrefix(string? nivel1)
            => $"nivel2|{nivel1}|";

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

            var registrosContablesUrl = $"/contabilidad/registros-contables{query.ToQueryString()}";
            await JS.InvokeVoidAsync("open", registrosContablesUrl, "_blank", "noopener,noreferrer");
        }

        public void Dispose()
        {
            RfcState.Changed -= OnRfcStateChanged;
        }
    }
}
