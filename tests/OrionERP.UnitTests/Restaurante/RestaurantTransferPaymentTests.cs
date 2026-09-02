using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.RegularExpressions;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantTransferPaymentTests
{
  private const string SampleHolder = "Jorge Contreras Carpio";
  private const string SampleAccount = "4065810178";
  private const string SampleClabe = "021830040658101780";
  private const string SampleCard = "4830303151973944";

  [Fact]
  public void NormalizeDigits_StripsSeparatorsAndDiscardsEmptyValues()
  {
    Assert.Equal(SampleClabe, RestaurantTransferPaymentRules.NormalizeDigits("021 830 04065810178 0"));
    Assert.Equal(SampleCard, RestaurantTransferPaymentRules.NormalizeDigits("4830-3031-5197-3944"));
    Assert.Null(RestaurantTransferPaymentRules.NormalizeDigits("   "));
    Assert.Null(RestaurantTransferPaymentRules.NormalizeDigits("sin dígitos"));
  }

  [Fact]
  public void IsValidClabe_ChecksLengthAndVerificationDigit()
  {
    Assert.True(RestaurantTransferPaymentRules.IsValidClabe(SampleClabe));
    Assert.True(RestaurantTransferPaymentRules.IsValidClabe("021 830 04065810178 0"));
    // Mismo largo, dígito verificador incorrecto.
    Assert.False(RestaurantTransferPaymentRules.IsValidClabe("021830040658101781"));
    // Los dos últimos dígitos transpuestos.
    Assert.False(RestaurantTransferPaymentRules.IsValidClabe("021830040658101708"));
    Assert.False(RestaurantTransferPaymentRules.IsValidClabe("02183004065810178"));
    Assert.False(RestaurantTransferPaymentRules.IsValidClabe(null));
  }

  [Fact]
  public void IsValidCardNumber_AppliesLuhnAndLengthBounds()
  {
    Assert.True(RestaurantTransferPaymentRules.IsValidCardNumber(SampleCard));
    Assert.True(RestaurantTransferPaymentRules.IsValidCardNumber("4830 3031 5197 3944"));
    Assert.False(RestaurantTransferPaymentRules.IsValidCardNumber("4830303151973945"));
    Assert.False(RestaurantTransferPaymentRules.IsValidCardNumber("48303031"));
    Assert.False(RestaurantTransferPaymentRules.IsValidCardNumber(null));
  }

  [Fact]
  public void Format_GroupsDigitsForThermalReading()
  {
    Assert.Equal("021 830 04065810178 0", RestaurantTransferPaymentRules.FormatClabe(SampleClabe));
    Assert.Equal("4830 3031 5197 3944", RestaurantTransferPaymentRules.FormatCardNumber(SampleCard));
    Assert.Equal("4065 8101 78", RestaurantTransferPaymentRules.FormatAccountNumber(SampleAccount));
    Assert.Equal(string.Empty, RestaurantTransferPaymentRules.FormatClabe(null));
  }

  [Fact]
  public void SiteUpsert_AcceptsTheConfiguredSampleAccount()
  {
    var request = CreateSiteRequest();

    Assert.Empty(Validate(request));
  }

  [Fact]
  public void SiteUpsert_RejectsBadClabeCardAndDestinationsWithoutHolder()
  {
    var badClabe = CreateSiteRequest();
    badClabe.TransferClabe = "021830040658101781";
    Assert.Contains(Validate(badClabe), result => result.MemberNames.Contains(nameof(RestaurantSiteUpsertRequest.TransferClabe)));

    var badCard = CreateSiteRequest();
    badCard.TransferCardNumber = "4830303151973945";
    Assert.Contains(Validate(badCard), result => result.MemberNames.Contains(nameof(RestaurantSiteUpsertRequest.TransferCardNumber)));

    var badAccount = CreateSiteRequest();
    badAccount.TransferAccountNumber = "406";
    Assert.Contains(Validate(badAccount), result => result.MemberNames.Contains(nameof(RestaurantSiteUpsertRequest.TransferAccountNumber)));

    var orphanDestination = CreateSiteRequest();
    orphanDestination.TransferAccountHolder = "  ";
    Assert.Contains(Validate(orphanDestination), result => result.MemberNames.Contains(nameof(RestaurantSiteUpsertRequest.TransferAccountHolder)));
  }

  [Fact]
  public void SiteUpsert_AllowsSitesThatDoNotAcceptTransfers()
  {
    var request = CreateSiteRequest();
    request.TransferAccountHolder = null;
    request.TransferBankName = null;
    request.TransferAccountNumber = null;
    request.TransferClabe = null;
    request.TransferCardNumber = null;
    request.TransferInstructions = null;

    Assert.Empty(Validate(request));
  }

  [Fact]
  public void HasTransferPaymentDetails_RequiresHolderAndAtLeastOneDestination()
  {
    Assert.True(CreateSite().HasTransferPaymentDetails);

    var withoutDestination = CreateSite();
    withoutDestination.TransferAccountNumber = null;
    withoutDestination.TransferClabe = null;
    withoutDestination.TransferCardNumber = null;
    Assert.False(withoutDestination.HasTransferPaymentDetails);

    var withoutHolder = CreateSite();
    withoutHolder.TransferAccountHolder = null;
    Assert.False(withoutHolder.HasTransferPaymentDetails);
  }

  [Fact]
  public void FromSite_NormalizesStoredValuesAndCarriesTheOrderContext()
  {
    var site = CreateSite();
    site.TransferClabe = "021 830 04065810178 0";

    var slip = RestaurantTransferSlipDocumentModel.FromSite(
      site,
      250.50m,
      "  Ana López  ",
      new DateTimeOffset(2026, 9, 2, 14, 33, 0, TimeSpan.FromHours(-6)),
      7);

    Assert.Equal(SampleClabe, slip.Clabe);
    Assert.Equal(SampleCard, slip.CardNumber);
    Assert.Equal(SampleAccount, slip.AccountNumber);
    Assert.Equal(SampleHolder, slip.AccountHolder);
    Assert.Equal("Ana López", slip.Reference);
    Assert.Equal(250.50m, slip.Amount);
    Assert.Equal(7, slip.Folio);
  }

  [Fact]
  public void GenerateTransferSlip_PrintsASingleThermalPageWithTheBankData()
  {
    var service = new RestaurantReceiptPdfService();

    var pdf = service.GenerateTransferSlip(RestaurantTransferSlipDocumentModel.FromSite(
      CreateSite(),
      250m,
      "Ana López",
      new DateTimeOffset(2026, 9, 2, 14, 33, 0, TimeSpan.FromHours(-6))));

    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    Assert.Single(Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?!s)\b"));
    Assert.True(pdf.Length > 1_000);
  }

  [Fact]
  public void GenerateTransferSlip_OmitsDestinationsThatWereNotConfigured()
  {
    var service = new RestaurantReceiptPdfService();
    var site = CreateSite();
    site.TransferAccountNumber = null;
    site.TransferCardNumber = null;
    site.TransferInstructions = null;

    var pdf = service.GenerateTransferSlip(RestaurantTransferSlipDocumentModel.FromSite(
      site,
      0m,
      null,
      DateTimeOffset.UtcNow));

    Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
    Assert.Single(Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?!s)\b"));
  }

  private static IReadOnlyList<ValidationResult> Validate(RestaurantSiteUpsertRequest request)
  {
    var results = new List<ValidationResult>();
    Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
    return results;
  }

  private static RestaurantSiteUpsertRequest CreateSiteRequest() => new()
  {
    Rfc = BrunoRestaurantConstants.Rfc,
    SiteCode = BrunoRestaurantConstants.SiteCode,
    Name = "Bruno's",
    TransferAccountHolder = SampleHolder,
    TransferBankName = "BBVA",
    TransferAccountNumber = SampleAccount,
    TransferClabe = SampleClabe,
    TransferCardNumber = SampleCard,
    TransferInstructions = "Envía tu comprobante al mostrador."
  };

  private static RestaurantSiteDto CreateSite() => new()
  {
    Id = 1,
    Rfc = BrunoRestaurantConstants.Rfc,
    SiteCode = BrunoRestaurantConstants.SiteCode,
    Name = "Bruno's",
    TimeZoneId = "Central Standard Time (Mexico)",
    TaxRate = 0.16m,
    PricesIncludeTax = true,
    IsEnabled = true,
    TransferAccountHolder = SampleHolder,
    TransferBankName = "BBVA",
    TransferAccountNumber = SampleAccount,
    TransferClabe = SampleClabe,
    TransferCardNumber = SampleCard,
    TransferInstructions = "Envía tu comprobante al mostrador."
  };
}
