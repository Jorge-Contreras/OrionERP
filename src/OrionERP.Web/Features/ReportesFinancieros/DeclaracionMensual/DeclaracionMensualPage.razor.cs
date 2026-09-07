using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Web.Features.ReportesFinancieros.DeclaracionMensual;

public partial class DeclaracionMensualPage : ComponentBase
{
  [Inject] private ICurrentCompanyContext RfcState { get; set; } = default!;
  [Inject] private IDeclaracionMensualService Servicio { get; set; } = default!;
  [Inject] private IJSRuntime JS { get; set; } = default!;

  private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

  private static readonly string[] NombresMes =
  [
    "ENERO", "FEBRERO", "MARZO", "ABRIL", "MAYO", "JUNIO",
    "JULIO", "AGOSTO", "SEPTIEMBRE", "OCTUBRE", "NOVIEMBRE", "DICIEMBRE"
  ];

  private enum Pestana { Cifras, Conciliacion, Hallazgos, Ejercicio, Cierre }

  private Pestana Activa { get; set; } = Pestana.Cifras;

  public int Anio { get; set; }
  public int Mes { get; set; }

  private string Rfc => RfcState.RequireRfc();

  private DeclaracionMensualReport? Reporte { get; set; }
  private IReadOnlyList<DeclaracionEjercicioRow> Ejercicio { get; set; } = [];

  private bool IsLoading { get; set; }
  private bool IsWorking { get; set; }
  private string? ErrorMessage { get; set; }
  private string? Aviso { get; set; }

  private List<DeclaracionImportada> Previsualizadas { get; } = [];
  private string? ErrorImportacion { get; set; }
  private bool ConfirmandoCierre { get; set; }

  protected override async Task OnInitializedAsync()
  {
    // Abre en el mes anterior: es el que toca declarar entre el 1 y el 17 del
    // mes en curso, y por tanto el que se va a consultar nueve de cada diez veces.
    var referencia = DateTime.Today.AddMonths(-1);
    Anio = referencia.Year;
    Mes = referencia.Month;

    await CargarAsync();
  }

  private async Task CargarAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      Reporte = await Servicio.GetMensualAsync(Rfc, Anio, Mes);
      Ejercicio = await Servicio.GetEjercicioAsync(Rfc, Anio);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      Reporte = null;
      Ejercicio = [];
    }
    finally
    {
      IsLoading = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private Task OnFiltroCambiadoAsync()
  {
    Aviso = null;
    ConfirmandoCierre = false;
    return CargarAsync();
  }

  private void SetPestana(Pestana pestana) => Activa = pestana;

  private string? Activo(Pestana pestana) => Activa == pestana ? "active" : null;

  private static string Money(decimal? valor) =>
    valor is null ? "—" : valor.Value.ToString("N2", Mx);

  private static string Tasa(decimal? valor) =>
    valor is null ? "—" : valor.Value.ToString("0.0000", Mx);

  private static string VencimientoClase(int dias) =>
    dias < 0 ? "is-bad" : dias <= 5 ? "is-warn" : "is-ok";

  private static string TextoVencimiento(int dias) => dias switch
  {
    < 0 => $"Vencido hace {Math.Abs(dias)} día(s)",
    0 => "Vence hoy",
    _ => $"Faltan {dias} día(s)"
  };

  private static string TextoDeclaracion(DeclaracionEncabezadoRow cab) =>
    !cab.TieneDeclaracion ? "Sin presentar"
      : cab.DeclaracionTipo == "C" ? "Complementaria"
      : "Normal";

  private string HallazgosClase()
  {
    var resumen = Reporte?.HallazgosResumen;
    if (resumen is null || resumen.Total == 0)
    {
      return "is-ok";
    }

    return resumen.Altas > 0 ? "is-bad" : "is-warn";
  }

  /// <summary>
  /// El coeficiente cambia a media año, cuando se presenta la anual del
  /// ejercicio anterior, asi que decir de que anual viene evita tener que
  /// adivinarlo al revisar una complementaria vieja.
  /// </summary>
  private static string OrigenCoeficiente(DeclaracionEncabezadoRow cab) =>
    cab.CoeficienteOrigen is { } ejercicio ? $" (anual {ejercicio})" : string.Empty;
}
