using OrionERP.Application.Features.Cfdi.DeclaracionPrevia;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Row selection:
    private async Task SelectEmitidaAsync(DeclaracionCfdiBase item)
    {
      if (item is DeclaracionEmitida emitida)
      {
        await SelectCfdiAsync(emitida);
      }
    }

    private async Task SelectRecibidaAsync(DeclaracionCfdiBase item)
    {
      if (item is DeclaracionRecibida recibida)
      {
        await SelectCfdiAsync(recibida);
      }
    }

    private async Task SelectCfdiAsync(DeclaracionCfdiBase? item)
    {
      selectedEmitida = null;
      selectedRecibida = null;
      emitidasComplementos = new List<PagoComplementoResumen>();
      recibidasComplementos = new List<PagoComplementoResumen>();

      if (item == null)
      {
        return;
      }

      if (item is DeclaracionEmitida emitida)
      {
        selectedEmitida = emitida;
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: true);
        return;
      }

      if (item is DeclaracionRecibida recibida)
      {
        selectedRecibida = recibida;
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: false);
        return;
      }

      if (item.EsEmitida)
      {
        selectedEmitida = FindEmitidaById(item.Comprobante_Id) ?? new DeclaracionEmitida(item);
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: true);
        return;
      }

      if (item.EsRecibida)
      {
        selectedRecibida = FindRecibidaById(item.Comprobante_Id) ?? new DeclaracionRecibida(item);
        await LoadComplementosAsync(item.FOLIO_FISCAL, isEmitida: false);
      }
    }

    private DeclaracionEmitida? FindEmitidaById(int comprobanteId)
    {
      if (selectedEmitida?.Comprobante_Id == comprobanteId)
      {
        return selectedEmitida;
      }

      return emitidas?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? emitidasPpd?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? emitidasNomina?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? tipoEEmitidas?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId);
    }

    private DeclaracionRecibida? FindRecibidaById(int comprobanteId)
    {
      if (selectedRecibida?.Comprobante_Id == comprobanteId)
      {
        return selectedRecibida;
      }

      return recibidas?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? recibidasPpd?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? recibidasNomina?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? tipoERecibidas?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId);
    }

    // Toggle Include/Exclude for selected invoice (Emitidas)
    private async Task ToggleEmitidaSelected()
    {
      if (selectedEmitida == null)
      {
        statusMessage = "Selecciona una factura emitida primero.";
        return;
      }
      try
      {
        var wasExcluded = selectedEmitida.D == "✓";
        await DeclaracionService.ToggleInclusionAsync(selectedEmitida.Comprobante_Id);
        await LoadAllData();
        statusMessage = $"Factura recibida marcada como {(wasExcluded ? "EXCLUIDA" : "INCLUIDA")} en la declaración.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al actualizar factura emitida: " + ex.Message);
      }
    }

    // Toggle Include/Exclude for selected invoice (Recibidas)
    private async Task ToggleRecibidaSelected()
    {
      if (selectedRecibida == null)
      {
        statusMessage = "Selecciona una factura recibida primero.";
        return;
      }
      try
      {
        var wasExcluded = selectedRecibida.D == "✓";
        await DeclaracionService.ToggleInclusionAsync(selectedRecibida.Comprobante_Id);
        await LoadAllData();
        statusMessage = $"Factura recibida marcada como {(wasExcluded ? "EXCLUIDA" : "INCLUIDA")} en la declaración.";
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al actualizar factura recibida: " + ex.Message);
      }
    }

    // Exclude all "Pago" or "Devolución" type invoices in Recibidas list
    private async Task ExcludePagosYDevoluciones()
    {
      try
      {
        int affected = await DeclaracionService.ExcludePagosYDevolucionesAsync(selectedRfc ?? string.Empty, selectedYear, isAnnual ? null : selectedMonth);
        await LoadAllData();
        if (affected > 0)
        {
          statusMessage = $"Se excluyeron {affected} comprobantes de tipo Pago/Devolución de la declaración.";
        }
        else
        {
          statusMessage = "No se encontraron CFDIs de tipo Pago o Devolución para excluir.";
        }
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al excluir pagos/devoluciones: " + ex.Message);
      }
    }

    private async Task LoadComplementosAsync(string? uuidText, bool isEmitida)
    {
      if (string.IsNullOrWhiteSpace(uuidText) || !Guid.TryParse(uuidText, out var uuid))
      {
        if (isEmitida)
        {
          emitidasComplementos = new List<PagoComplementoResumen>();
        }
        else
        {
          recibidasComplementos = new List<PagoComplementoResumen>();
        }
        return;
      }

      try
      {
        var resultados = (await DeclaracionService.GetComplementosAsync(uuid)).ToList();

        if (isEmitida)
        {
          emitidasComplementos = resultados;
        }
        else
        {
          recibidasComplementos = resultados;
        }
      }
      catch (Exception ex)
      {
        SetErrorMessage("Error al cargar complementos de pago: " + ex.Message);
      }
    }

    private string GetEmitidaRowClass(DeclaracionCfdiBase item)
    {
      if (item is not DeclaracionEmitida emitida)
      {
        return string.Empty;
      }
      var classes = new List<string>();

      if (selectedEmitida?.Comprobante_Id == emitida.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(emitida.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }

    private string GetRecibidaRowClass(DeclaracionCfdiBase item)
    {
      if (item is not DeclaracionRecibida recibida)
      {
        return string.Empty;
      }
      var classes = new List<string>();

      if (selectedRecibida?.Comprobante_Id == recibida.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(recibida.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }
  }
}
