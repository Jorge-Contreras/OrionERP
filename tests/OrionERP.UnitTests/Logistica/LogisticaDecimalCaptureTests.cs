using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

/// <summary>
/// En logística todo se captura con decimales: 1.5 kg, 0.75 litros, $12.35 por pieza. Un
/// <c>input type="number"</c> deja de reportar el valor mientras el usuario escribe el punto,
/// así que Blazor lo borraba y la cifra decimal nunca entraba. Estas pruebas cuidan que la
/// captura numérica de logística use el componente tolerante a decimales.
/// </summary>
public class LogisticaDecimalCaptureTests
{
  private static readonly string[] LogisticsPages =
  [
    "src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor",
    "src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor",
    "src/OrionERP.Web/Features/Logistica/Locations/UbicacionesPage.razor",
    "src/OrionERP.Web/Features/Logistica/PhysicalCounts/ConteosFisicosPage.razor"
  ];

  [Fact]
  public void DecimalInput_KeepsWhatTheUserTypesAndParsesConDecimales()
  {
    var component = RepoFile.Read("src/OrionERP.Web/Shared/DecimalInput.razor");

    Assert.Contains("type=\"text\"", component, StringComparison.Ordinal);
    Assert.Contains("inputmode=\"decimal\"", component, StringComparison.Ordinal);
    Assert.DoesNotContain("type=\"number\"", component, StringComparison.Ordinal);
    Assert.Contains("if (IsEditing)", component, StringComparison.Ordinal);
    Assert.Contains("CultureInfo.InvariantCulture, out var invariantValue", component, StringComparison.Ordinal);
    Assert.Contains("CultureInfo.CurrentCulture, out var currentCultureValue", component, StringComparison.Ordinal);
  }

  [Fact]
  public void LogisticsPages_CaptureQuantitiesAndPricesWithoutNativeNumberInputs()
  {
    foreach (var relativePath in LogisticsPages)
    {
      var page = RepoFile.Read(relativePath);

      Assert.DoesNotContain("type=\"number\"", page, StringComparison.Ordinal);

      // Materiales conserva el campo numérico nativo solo para la vida de anaquel, que se
      // cuenta en días enteros; el resto de logística captura cantidades y precios decimales.
      var allowedNativeNumberInputs = relativePath.EndsWith("MaterialesPage.razor", StringComparison.Ordinal) ? 1 : 0;
      Assert.Equal(allowedNativeNumberInputs, CountOccurrences(page, "<InputNumber"));
    }

    Assert.Contains(
      "<InputNumber id=\"material-shelf-life\"",
      RepoFile.Read("src/OrionERP.Web/Features/Logistica/Materials/MaterialesPage.razor"),
      StringComparison.Ordinal);
  }

  [Fact]
  public void ComprasPage_UsesDecimalInputForPricesQuantitiesAndTicketTotals()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor");
    var codeBehind = RepoFile.Read("src/OrionERP.Web/Features/Logistica/Purchasing/ComprasPage.razor.cs");

    Assert.Contains("@bind-Value=\"line.BaseUnitPrice\"", page, StringComparison.Ordinal);
    Assert.Contains("ValueChanged=\"@SetPendingAllocationDisplayQuantity\"", page, StringComparison.Ordinal);
    Assert.Contains("SetAllocationDisplayQuantity(SelectedLine, allocation, value)", page, StringComparison.Ordinal);
    Assert.Contains("SetReceiveNowDisplayQuantity(item, value)", page, StringComparison.Ordinal);
    Assert.Contains("UpdateReceiveTotalAmount(item, value)", page, StringComparison.Ordinal);
    Assert.Equal(5, CountOccurrences(page, "<DecimalInput"));
    Assert.Contains("UpdateReceiveTotalAmount(ReceiveAllocationInput item, decimal? amount)", codeBehind, StringComparison.Ordinal);
  }

  private static int CountOccurrences(string source, string value)
  {
    var count = 0;
    var index = source.IndexOf(value, StringComparison.Ordinal);
    while (index >= 0)
    {
      count++;
      index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
    }

    return count;
  }
}
