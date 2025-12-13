using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using OrionERP.Application.Common;
using OrionERP.Infrastructure.Common;

namespace OrionERP.Web.Features.Cfdi.DeclaracionPrevia.Pages
{
  public partial class DeclaracionPrevia
  {
    // Row selection:
    private async Task SelectEmitidaAsync(DeclaracionEmitida item) => await SelectCfdiAsync(item);

    private async Task SelectRecibidaAsync(DeclaracionRecibida item) => await SelectCfdiAsync(item);

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
        ?? recibidasNomina?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId)
        ?? tipoERecibidas?.FirstOrDefault(x => x.Comprobante_Id == comprobanteId);
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
            var resultados = await DeclaracionPreviaService.GetComplementosAsync(uuid);

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

    private string GetEmitidaRowClass(DeclaracionEmitida item)
    {
      var classes = new List<string>();

      if (selectedEmitida?.Comprobante_Id == item.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(item.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }

    private string GetRecibidaRowClass(DeclaracionRecibida item)
    {
      var classes = new List<string>();

      if (selectedRecibida?.Comprobante_Id == item.Comprobante_Id)
      {
        classes.Add("table-active");
      }

      if (string.Equals(item.MetodoPago, "PPD", StringComparison.OrdinalIgnoreCase))
      {
        classes.Add("highlight-table-row");
      }

      return string.Join(" ", classes);
    }
  }
}
