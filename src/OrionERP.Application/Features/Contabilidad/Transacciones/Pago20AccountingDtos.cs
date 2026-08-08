using OrionERP.Application.Features.Ajustes;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed record Pago20AccountingBasisDto
{
  public int TransaccionId { get; init; }
  public string Contexto { get; init; } = PlantillaContableContextos.Pago20Recibido;
  public string Direccion { get; init; } = string.Empty;
  public decimal TransaccionMonto { get; init; }
  public decimal TotalAsignado { get; init; }
  public decimal Subtotal { get; init; }
  public decimal TrasladoIsr { get; init; }
  public decimal TrasladoIva { get; init; }
  public decimal TrasladoIeps { get; init; }
  public decimal RetencionIsr { get; init; }
  public decimal RetencionIva { get; init; }
  public decimal RetencionIeps { get; init; }
  public bool RequiresAmountOverride => decimal.Abs(decimal.Abs(TransaccionMonto) - TotalAsignado) > 0.01m;

  public IReadOnlyDictionary<string, decimal> ToAmountDictionary()
    => new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
      [PlantillaContableMontoTipos.Pago20TotalAsignado] = TotalAsignado,
      [PlantillaContableMontoTipos.Pago20Subtotal] = Subtotal,
      [PlantillaContableMontoTipos.Pago20TrasladoIsr] = TrasladoIsr,
      [PlantillaContableMontoTipos.Pago20TrasladoIva] = TrasladoIva,
      [PlantillaContableMontoTipos.Pago20TrasladoIeps] = TrasladoIeps,
      [PlantillaContableMontoTipos.Pago20RetencionIsr] = RetencionIsr,
      [PlantillaContableMontoTipos.Pago20RetencionIva] = RetencionIva,
      [PlantillaContableMontoTipos.Pago20RetencionIeps] = RetencionIeps
    };
}

public sealed record Pago20AccountingBasisResult(bool Success, string Message, Pago20AccountingBasisDto? Basis = null)
{
  public static Pago20AccountingBasisResult Ok(Pago20AccountingBasisDto basis)
    => new(true, "Base contable Pago20 calculada correctamente.", basis);

  public static Pago20AccountingBasisResult Fail(string message)
    => new(false, message);
}

public sealed record Pago20AccountingDocumentInput(int DoctoRelacionadoId, decimal MontoAsignado, decimal ImpPagado);

public sealed record Pago20AccountingTaxInput(int DoctoRelacionadoId, string? Impuesto, decimal Importe);

public sealed record Pago20AccountingTaxTotals(
    decimal TotalAsignado,
    decimal Subtotal,
    decimal TrasladoIsr,
    decimal TrasladoIva,
    decimal TrasladoIeps,
    decimal RetencionIsr,
    decimal RetencionIva,
    decimal RetencionIeps);

public static class Pago20AccountingCalculator
{
  public static Pago20AccountingTaxTotals Calculate(
      IReadOnlyCollection<Pago20AccountingDocumentInput> documents,
      IReadOnlyCollection<Pago20AccountingTaxInput> transfers,
      IReadOnlyCollection<Pago20AccountingTaxInput> retentions)
  {
    ArgumentNullException.ThrowIfNull(documents);
    ArgumentNullException.ThrowIfNull(transfers);
    ArgumentNullException.ThrowIfNull(retentions);

    if (documents.Count == 0)
      throw new ArgumentException("Se requiere al menos un documento Pago20.", nameof(documents));
    if (documents.Any(item => item.MontoAsignado <= 0m || item.ImpPagado <= 0m))
      throw new ArgumentException("Los montos asignados e ImpPagado deben ser mayores que cero.", nameof(documents));

    var documentsById = documents.ToDictionary(item => item.DoctoRelacionadoId);
    decimal Prorate(Pago20AccountingTaxInput tax)
    {
      if (!documentsById.TryGetValue(tax.DoctoRelacionadoId, out var document))
        throw new ArgumentException($"El impuesto referencia el documento Pago20 inexistente {tax.DoctoRelacionadoId}.");

      return tax.Importe * (document.MontoAsignado / document.ImpPagado);
    }

    decimal SumTax(IEnumerable<Pago20AccountingTaxInput> rows, string code)
      => RoundCurrency(rows.Where(item => string.Equals(item.Impuesto, code, StringComparison.Ordinal)).Sum(Prorate));

    var total = RoundCurrency(documents.Sum(item => item.MontoAsignado));
    var trasladoIsr = SumTax(transfers, "001");
    var trasladoIva = SumTax(transfers, "002");
    var trasladoIeps = SumTax(transfers, "003");
    var retencionIsr = SumTax(retentions, "001");
    var retencionIva = SumTax(retentions, "002");
    var retencionIeps = SumTax(retentions, "003");
    var subtotal = RoundCurrency(total - trasladoIsr - trasladoIva - trasladoIeps + retencionIsr + retencionIva + retencionIeps);

    return new Pago20AccountingTaxTotals(
        total,
        subtotal,
        trasladoIsr,
        trasladoIva,
        trasladoIeps,
        retencionIsr,
        retencionIva,
        retencionIeps);
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

public sealed record TransaccionRegularCfdiLinkRequest(int TransaccionId, long ComprobanteId, decimal Monto);

public sealed record TransaccionPago20LinkRequest(int TransaccionId, int DoctoRelacionadoId, decimal Monto);
