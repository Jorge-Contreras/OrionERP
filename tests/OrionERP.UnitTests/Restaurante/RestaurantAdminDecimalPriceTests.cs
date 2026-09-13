using System.Globalization;
using OrionERP.UnitTests.Common;
using OrionERP.Web.Shared;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantAdminDecimalPriceTests
{
  [Fact]
  public void ProductPrice_UsesCultureTolerantDecimalInput()
  {
    var page = RepoFile.Read("src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor");

    Assert.Contains(
      "<DecimalInput TValue=\"decimal\" min=\"0\" step=\"0.01\" @bind-Value=\"productEditor.Price\" />",
      page,
      StringComparison.Ordinal);
    Assert.DoesNotContain(
      "type=\"number\" min=\"0\" step=\"0.01\" @bind=\"productEditor.Price\"",
      page,
      StringComparison.Ordinal);
  }

  [Fact]
  public void DecimalInput_PreservesIntermediateTextWhileEditing()
  {
    var component = RepoFile.Read("src/OrionERP.Web/Shared/DecimalInput.razor");

    Assert.Contains("type=\"text\"", component, StringComparison.Ordinal);
    Assert.Contains("value=\"@Text\"", component, StringComparison.Ordinal);
    Assert.Contains("@oninput=\"OnInput\"", component, StringComparison.Ordinal);
    Assert.Contains("DecimalTextParser.TryParse(Text, out var parsed)", component, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("149.50", "es-MX", "149.50")]
  [InlineData("149,50", "es-MX", "149.50")]
  [InlineData("149,50", "es-ES", "149.50")]
  [InlineData("1,234.50", "es-MX", "1234.50")]
  [InlineData("1.234,50", "es-ES", "1234.50")]
  public void DecimalParser_AcceptsDotOrCommaWithoutChangingMagnitude(
    string text,
    string cultureName,
    string expected)
  {
    var culture = CultureInfo.GetCultureInfo(cultureName);

    var success = DecimalTextParser.TryParse(text, culture, out var value);

    Assert.True(success);
    Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), value);
  }

  [Theory]
  [InlineData("149.5.0")]
  [InlineData("149,5,0")]
  public void DecimalParser_RejectsAmbiguousRepeatedSeparators(string text)
  {
    var success = DecimalTextParser.TryParse(text, CultureInfo.GetCultureInfo("es-MX"), out var value);

    Assert.False(success);
    Assert.Null(value);
  }
}
