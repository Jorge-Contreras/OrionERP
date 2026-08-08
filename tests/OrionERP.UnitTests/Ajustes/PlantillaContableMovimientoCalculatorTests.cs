using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.UnitTests.Ajustes;

public sealed class PlantillaContableMovimientoCalculatorTests
{
  [Fact]
  public void CreateDrafts_MapsNatureToDebitAndCredit()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, naturaleza: "DEBE"),
      CreateLine(id: 2, naturaleza: "HABER")
    };

    var drafts = PlantillaContableMovimientoCalculator.CreateDrafts(lineas, 1250.75m, "Pago renta");

    Assert.Equal(2, drafts.Count);
    Assert.Equal(1250.75m, drafts[0].Debe);
    Assert.Equal(0m, drafts[0].Haber);
    Assert.Equal(0m, drafts[1].Debe);
    Assert.Equal(1250.75m, drafts[1].Haber);
  }

  [Fact]
  public void CreateDrafts_CalculatesSubtotalAndIvaFromTotal()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: "SUBTOTAL_IVA_16"),
      CreateLine(id: 2, montoTipo: "IVA_16")
    };

    var drafts = PlantillaContableMovimientoCalculator.CreateDrafts(lineas, 116m, "Compra");

    Assert.Equal(100m, drafts[0].Debe);
    Assert.Equal(16m, drafts[1].Debe);
  }

  [Fact]
  public void CreateDrafts_AppliesFactorAndRoundsAwayFromZero()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, factor: 0.33335m)
    };

    var drafts = PlantillaContableMovimientoCalculator.CreateDrafts(lineas, 100m, "Prorrateo");

    Assert.Equal(33.34m, drafts.Single().Debe);
  }

  [Fact]
  public void CreateDrafts_UsesFixedOrTransactionConcept()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, conceptoTipo: "TRANSACCION"),
      CreateLine(id: 2, conceptoTipo: "FIJO", conceptoFijo: "  IVA acreditable  ")
    };

    var drafts = PlantillaContableMovimientoCalculator.CreateDrafts(lineas, 100m, " Pago proveedor ");

    Assert.Equal("Pago proveedor", drafts[0].Concepto);
    Assert.Equal("IVA acreditable", drafts[1].Concepto);
  }

  [Fact]
  public void CreatePago20Drafts_UsesAssignedDocumentTaxAmounts()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: PlantillaContableMontoTipos.Pago20Subtotal),
      CreateLine(id: 2, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIva),
      CreateLine(id: 3, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20TotalAsignado)
    };
    var basis = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
      [PlantillaContableMontoTipos.Pago20TotalAsignado] = 782.33m,
      [PlantillaContableMontoTipos.Pago20Subtotal] = 674.42m,
      [PlantillaContableMontoTipos.Pago20TrasladoIva] = 107.91m,
      [PlantillaContableMontoTipos.Pago20TrasladoIsr] = 0m,
      [PlantillaContableMontoTipos.Pago20TrasladoIeps] = 0m,
      [PlantillaContableMontoTipos.Pago20RetencionIsr] = 0m,
      [PlantillaContableMontoTipos.Pago20RetencionIva] = 0m,
      [PlantillaContableMontoTipos.Pago20RetencionIeps] = 0m
    };

    var drafts = PlantillaContableMovimientoCalculator.CreatePago20Drafts(lineas, basis, "TELMEX GRECIA");

    Assert.Equal(3, drafts.Count);
    Assert.Equal(782.33m, drafts.Sum(item => item.Debe));
    Assert.Equal(782.33m, drafts.Sum(item => item.Haber));
    Assert.Equal(674.42m, drafts[0].Debe);
    Assert.Equal(107.91m, drafts[1].Debe);
  }

  [Fact]
  public void CreatePago20Drafts_SupportsTransfersAndRetentionsAndSkipsZeroLines()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: PlantillaContableMontoTipos.Pago20Subtotal),
      CreateLine(id: 2, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIva),
      CreateLine(id: 3, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIeps),
      CreateLine(id: 4, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20RetencionIsr),
      CreateLine(id: 5, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20TotalAsignado),
      CreateLine(id: 6, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIsr)
    };
    var basis = PlantillaContableMontoTipos.Pago20Tipos.ToDictionary(key => key, _ => 0m, StringComparer.OrdinalIgnoreCase);
    basis[PlantillaContableMontoTipos.Pago20TotalAsignado] = 110m;
    basis[PlantillaContableMontoTipos.Pago20Subtotal] = 100m;
    basis[PlantillaContableMontoTipos.Pago20TrasladoIva] = 8m;
    basis[PlantillaContableMontoTipos.Pago20TrasladoIeps] = 4m;
    basis[PlantillaContableMontoTipos.Pago20RetencionIsr] = 2m;

    var drafts = PlantillaContableMovimientoCalculator.CreatePago20Drafts(lineas, basis, "Prorrateo");

    Assert.Equal(5, drafts.Count);
    Assert.Equal(112m, drafts.Sum(item => item.Debe));
    Assert.Equal(112m, drafts.Sum(item => item.Haber));
  }

  [Fact]
  public void CreatePago20Drafts_SupportsEverySatTaxBucket()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: PlantillaContableMontoTipos.Pago20Subtotal),
      CreateLine(id: 2, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIsr),
      CreateLine(id: 3, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIva),
      CreateLine(id: 4, montoTipo: PlantillaContableMontoTipos.Pago20TrasladoIeps),
      CreateLine(id: 5, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20RetencionIsr),
      CreateLine(id: 6, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20RetencionIva),
      CreateLine(id: 7, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20RetencionIeps),
      CreateLine(id: 8, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20TotalAsignado)
    };
    var basis = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
      [PlantillaContableMontoTipos.Pago20TotalAsignado] = 112m,
      [PlantillaContableMontoTipos.Pago20Subtotal] = 100m,
      [PlantillaContableMontoTipos.Pago20TrasladoIsr] = 1m,
      [PlantillaContableMontoTipos.Pago20TrasladoIva] = 8m,
      [PlantillaContableMontoTipos.Pago20TrasladoIeps] = 11m,
      [PlantillaContableMontoTipos.Pago20RetencionIsr] = 2m,
      [PlantillaContableMontoTipos.Pago20RetencionIva] = 3m,
      [PlantillaContableMontoTipos.Pago20RetencionIeps] = 3m
    };

    var drafts = PlantillaContableMovimientoCalculator.CreatePago20Drafts(lineas, basis, "Todos los impuestos");

    Assert.Equal(120m, drafts.Sum(item => item.Debe));
    Assert.Equal(120m, drafts.Sum(item => item.Haber));
    Assert.Equal(8, drafts.Count);
  }

  [Fact]
  public void Pago20AccountingCalculator_ProratesAndAggregatesEverySatTaxBucket()
  {
    var documents = new[]
    {
      new Pago20AccountingDocumentInput(10, 50m, 100m),
      new Pago20AccountingDocumentInput(20, 25m, 100m)
    };
    var transfers = new[]
    {
      new Pago20AccountingTaxInput(10, "001", 10m),
      new Pago20AccountingTaxInput(10, "002", 16m),
      new Pago20AccountingTaxInput(20, "003", 20m)
    };
    var retentions = new[]
    {
      new Pago20AccountingTaxInput(10, "001", 2m),
      new Pago20AccountingTaxInput(10, "002", 4m),
      new Pago20AccountingTaxInput(20, "003", 8m)
    };

    var totals = Pago20AccountingCalculator.Calculate(documents, transfers, retentions);

    Assert.Equal(75m, totals.TotalAsignado);
    Assert.Equal(62m, totals.Subtotal);
    Assert.Equal(5m, totals.TrasladoIsr);
    Assert.Equal(8m, totals.TrasladoIva);
    Assert.Equal(5m, totals.TrasladoIeps);
    Assert.Equal(1m, totals.RetencionIsr);
    Assert.Equal(2m, totals.RetencionIva);
    Assert.Equal(2m, totals.RetencionIeps);
  }

  [Fact]
  public void Pago20AccountingCalculator_AggregatesBeforeRoundingAwayFromZero()
  {
    var documents = new[]
    {
      new Pago20AccountingDocumentInput(10, 1m, 2m),
      new Pago20AccountingDocumentInput(20, 1m, 2m)
    };
    var transfers = new[]
    {
      new Pago20AccountingTaxInput(10, "002", 0.01m),
      new Pago20AccountingTaxInput(20, "002", 0.01m)
    };

    var totals = Pago20AccountingCalculator.Calculate(documents, transfers, []);

    Assert.Equal(0.01m, totals.TrasladoIva);
    Assert.Equal(1.99m, totals.Subtotal);
  }

  [Theory]
  [InlineData(100, 100.01, false)]
  [InlineData(100, 100.02, true)]
  public void Pago20Basis_RequiresExplicitConfirmationBeyondOneCent(
      decimal transactionAmount,
      decimal assignedAmount,
      bool expected)
  {
    var basis = new Pago20AccountingBasisDto
    {
      TransaccionMonto = transactionAmount,
      TotalAsignado = assignedAmount
    };

    Assert.Equal(expected, basis.RequiresAmountOverride);
  }

  [Fact]
  public void CreatePago20Drafts_RejectsMissingNonZeroSource()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: PlantillaContableMontoTipos.Pago20Subtotal),
      CreateLine(id: 2, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20TotalAsignado)
    };
    var basis = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
      [PlantillaContableMontoTipos.Pago20Subtotal] = 100m,
      [PlantillaContableMontoTipos.Pago20TrasladoIva] = 16m,
      [PlantillaContableMontoTipos.Pago20TotalAsignado] = 116m
    };

    var exception = Assert.Throws<ArgumentException>(() =>
        PlantillaContableMovimientoCalculator.CreatePago20Drafts(lineas, basis, "Compra"));

    Assert.Contains(PlantillaContableMontoTipos.Pago20TrasladoIva, exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void CreatePago20Drafts_RejectsUnbalancedTemplate()
  {
    var lineas = new[]
    {
      CreateLine(id: 1, montoTipo: PlantillaContableMontoTipos.Pago20Subtotal),
      CreateLine(id: 2, naturaleza: "HABER", montoTipo: PlantillaContableMontoTipos.Pago20TotalAsignado)
    };
    var basis = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
    {
      [PlantillaContableMontoTipos.Pago20Subtotal] = 100m,
      [PlantillaContableMontoTipos.Pago20TotalAsignado] = 116m
    };

    Assert.Throws<ArgumentException>(() =>
        PlantillaContableMovimientoCalculator.CreatePago20Drafts(lineas, basis, "Compra"));
  }

  private static PlantillaContableLineaDto CreateLine(
      int id,
      string naturaleza = "DEBE",
      string montoTipo = "MONTO_TOTAL",
      decimal factor = 1m,
      string conceptoTipo = "TRANSACCION",
      string? conceptoFijo = null)
    => new()
    {
      PlantillaContableLineaId = id,
      PlantillaContableId = 10,
      Orden = id,
      CuentaContableId = 100 + id,
      CuentaRfc = "AAA010101AAA",
      Nivel1 = "1000",
      Nivel2 = id.ToString("000"),
      Nivel3 = "000",
      CuentaContable = $"Cuenta {id}",
      Naturaleza = naturaleza,
      MontoTipo = montoTipo,
      Factor = factor,
      ConceptoTipo = conceptoTipo,
      ConceptoFijo = conceptoFijo,
      Activa = true
    };
}
