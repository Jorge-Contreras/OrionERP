using OrionERP.Application.Features.Logistica.Stock;

namespace OrionERP.UnitTests.Logistica;

public sealed class WasteReasonCatalogTests
{
  [Fact]
  public void Codes_FitTheColumnAndDoNotRepeat()
  {
    var codes = WasteReasonCatalog.Selectable.Select(reason => reason.Code).ToArray();

    Assert.Equal(codes.Length, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    Assert.All(codes, code => Assert.InRange(code.Length, 1, WasteReasonCatalog.MaxCodeLength));
    Assert.All(codes, code => Assert.Equal(code.ToUpperInvariant(), code));
  }

  [Fact]
  public void Expired_ComesFirstBecauseItIsTheCaseThatCreatedTheModule()
    => Assert.Equal(WasteReasonCatalog.Expired, WasteReasonCatalog.Selectable[0].Code);

  [Fact]
  public void EveryReason_ExplainsItselfToTheOperator()
    => Assert.All(WasteReasonCatalog.Selectable, reason =>
    {
      Assert.False(string.IsNullOrWhiteSpace(reason.Label));
      Assert.False(string.IsNullOrWhiteSpace(reason.Help));
    });

  [Fact]
  public void OnlyOpaqueReasons_DemandAWrittenNote()
  {
    var demanding = WasteReasonCatalog.Selectable
      .Where(reason => reason.RequiresNote)
      .Select(reason => reason.Code)
      .OrderBy(code => code, StringComparer.Ordinal)
      .ToArray();

    Assert.Equal(new[] { WasteReasonCatalog.Missing, WasteReasonCatalog.Other }, demanding);
  }

  [Fact]
  public void Reversal_IsInternalAndNeverOffered()
  {
    Assert.DoesNotContain(WasteReasonCatalog.Reversal, WasteReasonCatalog.Selectable.Select(reason => reason.Code));
    Assert.False(WasteReasonCatalog.IsSelectable(WasteReasonCatalog.Reversal));
    Assert.Equal("Reversa", WasteReasonCatalog.LabelFor(WasteReasonCatalog.Reversal));
  }

  [Theory]
  [InlineData("caducidad")]
  [InlineData("  CADUCIDAD  ")]
  public void Find_IgnoresCaseAndSurroundingSpace(string code)
    => Assert.Equal(WasteReasonCatalog.Expired, WasteReasonCatalog.Find(code)?.Code);

  [Fact]
  public void UnknownCode_IsNotSelectableButStillReadable()
  {
    Assert.False(WasteReasonCatalog.IsSelectable("INVENTADO"));
    Assert.Equal("INVENTADO", WasteReasonCatalog.LabelFor("INVENTADO"));
    Assert.Equal("Sin motivo", WasteReasonCatalog.LabelFor(" "));
  }

  [Fact]
  public void Normalize_MatchesHowTheCodeIsStored()
    => Assert.Equal("CADUCIDAD", WasteReasonCatalog.Normalize(" caducidad "));
}
