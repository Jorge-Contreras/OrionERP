using OrionERP.Application.Features.Ajustes;

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
