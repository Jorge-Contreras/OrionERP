using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.JSInterop;
using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Web.Features.ReportesFinancieros.DeclaracionMensual;

public partial class DeclaracionMensualPage
{
  /// <summary>Tamano maximo por acuse. Los del SAT rondan los 200 KB.</summary>
  private const long MaxTamanoPdf = 8 * 1024 * 1024;

  private const int MaxArchivos = 24;

  private bool HayHallazgosAltos =>
    (Reporte?.HallazgosResumen?.Altas ?? 0) > 0;

  private bool CierreYaGenerado =>
    Reporte?.Encabezado?.TransaccionIdCierre is > 0;

  private bool IsrYaGenerado =>
    Reporte?.Encabezado?.TransaccionIdIsr is > 0;

  private bool HayAsientoPropuesto =>
    Reporte?.Cierre.Count > 0;

  /// <summary>
  /// El asiento de ISR siempre trae sus dos renglones; "hay algo que registrar"
  /// es que el importe sea positivo y las cuentas destino esten resueltas.
  /// </summary>
  private bool HayAsientoIsr =>
    Reporte?.CierreIsr is { Count: > 0 } filas
      && filas[0].Neto > 0
      && !string.IsNullOrEmpty(filas[0].Cuenta);

  /// <summary>
  /// El boton se bloquea mientras haya hallazgos de severidad alta. La razon es
  /// concreta: un CFDI sin poliza o con IVA distinto al contabilizado significa
  /// que 118 y 208 todavia no tienen el saldo correcto, y cerrar sobre saldos
  /// equivocados manda la diferencia a IVA a favor o por pagar, que es
  /// exactamente el error que este trabajo viene a corregir.
  /// </summary>
  private bool PuedeGenerarCierre =>
    !IsWorking && HayAsientoPropuesto && !CierreYaGenerado && !HayHallazgosAltos;

  /// <summary>
  /// Mismo criterio que el cierre de IVA: los hallazgos de severidad alta
  /// (CFDI sin poliza, ingreso sin CFDI) mueven los ingresos, y el ISR
  /// provisional se calcula sobre ellos, asi que registrar la provision con
  /// hallazgos pendientes fijaria un numero que va a cambiar.
  /// </summary>
  private bool PuedeGenerarIsr =>
    !IsWorking && HayAsientoIsr && !IsrYaGenerado && !HayHallazgosAltos;

  private void PedirConfirmacionCierre() => ConfirmandoCierre = true;

  private void CancelarCierre() => ConfirmandoCierre = false;

  private void PedirConfirmacionIsr() => ConfirmandoIsr = true;

  private void CancelarIsr() => ConfirmandoIsr = false;

  private async Task GenerarCierreAsync()
  {
    ConfirmandoCierre = false;
    IsWorking = true;
    ErrorMessage = null;
    Aviso = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      var (transaccionId, _) = await Servicio.GenerarCierreAsync(
        Rfc, Anio, Mes, regenerar: false, usuario: RfcState.DisplayName ?? "OrionERP");

      Aviso = $"Se genero la poliza de cierre {transaccionId}.";
      await CargarAsync();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsWorking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task GenerarIsrAsync()
  {
    ConfirmandoIsr = false;
    IsWorking = true;
    ErrorMessage = null;
    Aviso = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      var (transaccionId, _) = await Servicio.GenerarIsrAsync(
        Rfc, Anio, Mes, regenerar: false, usuario: RfcState.DisplayName ?? "OrionERP");

      Aviso = $"Se genero la poliza de ISR provisional {transaccionId}.";
      await CargarAsync();
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
    }
    finally
    {
      IsWorking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private async Task OnAcusesSeleccionadosAsync(InputFileChangeEventArgs e)
  {
    Previsualizadas.Clear();
    ErrorImportacion = null;
    IsWorking = true;
    await InvokeAsync(StateHasChanged);

    try
    {
      foreach (var archivo in e.GetMultipleFiles(MaxArchivos))
      {
        if (archivo.Size > MaxTamanoPdf)
        {
          ErrorImportacion = $"{archivo.Name} pesa mas de 8 MB y no se leyo.";
          continue;
        }

        using var origen = archivo.OpenReadStream(MaxTamanoPdf);
        using var memoria = new MemoryStream();
        await origen.CopyToAsync(memoria);
        memoria.Position = 0;

        Previsualizadas.Add(Servicio.LeerAcuse(memoria, archivo.Name));
      }
    }
    catch (Exception ex)
    {
      ErrorImportacion = ex.Message;
    }
    finally
    {
      IsWorking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  /// <summary>
  /// Nada se guarda hasta que el usuario ve el detalle de lo leido. Un acuse mal
  /// interpretado contaminaria el historial con el que se decide presentar
  /// complementarias, asi que la vista previa no es un adorno.
  /// </summary>
  private async Task ConfirmarImportacionAsync()
  {
    var validas = Previsualizadas.Where(x => x.EsValida).ToList();
    if (validas.Count == 0)
    {
      ErrorImportacion = "No hay acuses validos que guardar.";
      return;
    }

    IsWorking = true;
    ErrorImportacion = null;
    await InvokeAsync(StateHasChanged);

    try
    {
      var resultado = await Servicio.GuardarImportacionAsync(
        Rfc, validas, RfcState.DisplayName ?? "OrionERP");

      Aviso = $"Acuses guardados: {resultado.Insertadas} nuevos, "
            + $"{resultado.Actualizadas} actualizados, {resultado.Omitidas} omitidos.";

      if (resultado.Mensajes.Count > 0)
      {
        ErrorImportacion = string.Join(" ", resultado.Mensajes);
      }

      Previsualizadas.Clear();
      await CargarAsync();
    }
    catch (Exception ex)
    {
      ErrorImportacion = ex.Message;
    }
    finally
    {
      IsWorking = false;
      await InvokeAsync(StateHasChanged);
    }
  }

  private void DescartarImportacion()
  {
    Previsualizadas.Clear();
    ErrorImportacion = null;
  }

  private async Task CopiarAsync(decimal? valor)
  {
    if (valor is null)
    {
      return;
    }

    // El portal del SAT captura pesos enteros, asi que se copia redondeado.
    var texto = Math.Round(valor.Value, 0, MidpointRounding.AwayFromZero)
      .ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    await JS.InvokeVoidAsync("navigator.clipboard.writeText", texto);
  }

  private async Task ImprimirAsync() =>
    await JS.InvokeVoidAsync("orionPrintReport", "declaracion-mensual-print-root",
      "Declaracion Mensual ISR e IVA",
      $"RFC: {Rfc}  Periodo: {NombresMes[Mes - 1]} {Anio}");

  private async Task AbrirDetalleAsync(DeclaracionHallazgoRow hallazgo)
  {
    if (string.IsNullOrWhiteSpace(hallazgo.RutaDetalle))
    {
      return;
    }

    var query = new QueryBuilder
    {
      { "rfc", Rfc },
      { "anio", Anio.ToString(System.Globalization.CultureInfo.InvariantCulture) },
      { "mes", Mes.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };

    if (hallazgo.ComprobanteId is > 0)
    {
      query.Add("comprobante", hallazgo.ComprobanteId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    await JS.InvokeVoidAsync("open", $"{hallazgo.RutaDetalle}{query.ToQueryString()}",
      "_blank", "noopener,noreferrer");
  }
}
