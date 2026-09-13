using OrionERP.Application.Features.Logistica.Purchasing;

namespace OrionERP.UnitTests.Logistica;

public sealed class PurchasePolicyAmountsTests
{
  [Fact]
  public void FullPending_SplitsExactlyWhatWasReceived()
  {
    var amounts = PurchasePolicyAmounts.ForPending(recibidoSubtotal: 1000m, recibido: 1160m, porContabilizar: 1160m);

    Assert.Equal(new PurchasePolicyAmounts(1000m, 160m, 1160m), amounts);
  }

  [Fact]
  public void PartialPending_KeepsTheReceivedTaxProportion()
  {
    var amounts = PurchasePolicyAmounts.ForPending(recibidoSubtotal: 1000m, recibido: 1160m, porContabilizar: 580m);

    Assert.Equal(500m, amounts.Subtotal);
    Assert.Equal(80m, amounts.Iva);
    Assert.Equal(580m, amounts.Total);
  }

  [Fact]
  public void RoundingCent_GoesToIvaSoTheEntryBalances()
  {
    var amounts = PurchasePolicyAmounts.ForPending(recibidoSubtotal: 86.21m, recibido: 100m, porContabilizar: 33.33m);

    Assert.Equal(amounts.Total, amounts.Subtotal + amounts.Iva);
    Assert.Equal(28.73m, amounts.Subtotal);
    Assert.Equal(4.60m, amounts.Iva);
  }

  [Fact]
  public void ReceiptsWithoutTax_ProduceNoIvaLine()
  {
    var amounts = PurchasePolicyAmounts.ForPending(recibidoSubtotal: 450m, recibido: 450m, porContabilizar: 450m);

    Assert.Equal(0m, amounts.Iva);
    Assert.Equal(450m, amounts.Subtotal);
  }

  [Theory]
  [InlineData(0, 1160)]
  [InlineData(-5, 1160)]
  [InlineData(100, 0)]
  public void NothingPending_ProducesNothing(decimal porContabilizar, decimal recibido)
  {
    Assert.Equal(default, PurchasePolicyAmounts.ForPending(1000m, recibido, porContabilizar));
  }

  [Fact]
  public void InconsistentSubtotal_NeverProducesNegativeTax()
  {
    var amounts = PurchasePolicyAmounts.ForPending(recibidoSubtotal: 1200m, recibido: 1160m, porContabilizar: 1160m);

    Assert.Equal(1160m, amounts.Subtotal);
    Assert.Equal(0m, amounts.Iva);
  }
}
