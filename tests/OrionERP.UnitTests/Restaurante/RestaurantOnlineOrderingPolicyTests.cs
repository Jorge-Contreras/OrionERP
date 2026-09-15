using OrionERP.Application.Features.Payments.PayPal;
using OrionERP.Application.Features.Restaurante;
using OrionERP.Infrastructure.Features.Restaurante;
using System.Reflection;
using System.Text.Json;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantOnlineOrderingPolicyTests
{
  [Theory]
  [InlineData(RestaurantOrderRejectionCategory.Availability)]
  [InlineData(RestaurantOrderRejectionCategory.Product)]
  [InlineData(RestaurantOrderRejectionCategory.Modifier)]
  [InlineData(RestaurantOrderRejectionCategory.Combo)]
  [InlineData(RestaurantOrderRejectionCategory.Promotion)]
  [InlineData(RestaurantOrderRejectionCategory.PromotionCode)]
  [InlineData(RestaurantOrderRejectionCategory.Member)]
  [InlineData(RestaurantOrderRejectionCategory.Inventory)]
  [InlineData(RestaurantOrderRejectionCategory.Pricing)]
  public void Importer_TreatsTypedBusinessRejectionsAsDeterministic(
    RestaurantOrderRejectionCategory category)
  {
    Assert.True(IsDeterministic(new RestaurantOrderBusinessRejectionException(category, "rule changed")));
  }

  [Fact]
  public void Importer_LeavesUnclassifiedTechnicalFailuresRetryable()
  {
    Assert.False(IsDeterministic(new InvalidOperationException("SQL connection interrupted")));
    Assert.False(IsDeterministic(new InvalidOperationException("inventario agotado")));
  }

  [Fact]
  public void TrackingToken_IsStablePerSiteAttempt_AndStoredHashDoesNotExposeIt()
  {
    var attempt = Guid.Parse("17b37f34-d9cc-45ae-b45f-f350d2ca7750");

    var first = RestaurantOnlineTrackingTokenPolicy.Create(21, attempt);
    var repeated = RestaurantOnlineTrackingTokenPolicy.Create(21, attempt);
    var anotherSite = RestaurantOnlineTrackingTokenPolicy.Create(22, attempt);
    var hash = RestaurantOnlineTrackingTokenPolicy.Hash(first);

    Assert.Equal(first, repeated);
    Assert.NotEqual(first, anotherSite);
    Assert.Equal(43, first.Length);
    Assert.Equal(32, hash.Length);
    Assert.DoesNotContain(first, Convert.ToHexString(hash), StringComparison.Ordinal);
  }

  private static bool IsDeterministic(Exception exception)
  {
    var method = typeof(RestaurantOnlineOrderImportProcessor).GetMethod(
      "IsDeterministicFulfillmentFailure",
      BindingFlags.NonPublic | BindingFlags.Static)
      ?? throw new InvalidOperationException("The import failure classifier was not found.");
    return (bool)(method.Invoke(null, [exception])
      ?? throw new InvalidOperationException("The import failure classifier returned no result."));
  }

  [Fact]
  public void QuoteFingerprint_ChangesWithTenantTotalsPromotionsAndItemNotes()
  {
    var original = Quote();
    original.Fingerprint = RestaurantOnlineQuotePolicy.CreateFingerprint(original);
    var same = Quote();
    var anotherTenant = Quote();
    anotherTenant.PublicSiteId++;
    var anotherTotal = Quote();
    anotherTotal.Total++;
    var anotherNote = Quote();
    anotherNote.Request.Lines[0].Notes = "sin cebolla";
    var anotherTax = Quote();
    anotherTax.Tax++;
    var anotherPromotion = Quote();
    anotherPromotion.Promotions =
    [
      new RestaurantPromotionAdjustmentDto
      {
        PromotionId = 9,
        RuleType = "Percent",
        DiscountAmount = 10m
      }
    ];
    var anotherConfiguration = Quote();
    anotherConfiguration.SettingsConfigurationVersion++;
    var anotherTerms = Quote();
    anotherTerms.TermsVersion = "2026-10-01";
    var anotherPrivacy = Quote();
    anotherPrivacy.PrivacyVersion = "2026-10-01";

    Assert.Equal(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(same));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherTenant));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherTotal));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherNote));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherTax));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherPromotion));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherConfiguration));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherTerms));
    Assert.NotEqual(original.Fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(anotherPrivacy));
  }

  [Fact]
  public void OnlineSchedule_HandlesPreviousDaysOvernightInterval()
  {
    const string schedule = """
      { "Monday": [ { "opens": "18:00", "closes": "02:00" } ] }
      """;

    Assert.True(RestaurantOnlineQuotePolicy.IsOpen(
      schedule,
      TimeZoneInfo.Utc.Id,
      new DateTimeOffset(2026, 9, 15, 1, 30, 0, TimeSpan.Zero)));
    Assert.False(RestaurantOnlineQuotePolicy.IsOpen(
      schedule,
      TimeZoneInfo.Utc.Id,
      new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero)));
    Assert.False(RestaurantOnlineQuotePolicy.IsOpen("{}", TimeZoneInfo.Utc.Id, DateTimeOffset.UtcNow));
  }

  [Fact]
  public void CartCalculator_UsesCatalogPriceAndRequiresOnlineOptIn()
  {
    var catalog = Catalog(online: true);
    var request = new RestaurantOnlineQuoteRequest
    {
      Lines =
      [
        new RestaurantOnlineCartLineRequest
        {
          ProductId = 10,
          MenuSectionId = 3,
          Quantity = 2,
          ModifierOptionIds = [101],
          Notes = "  sin popote  "
        }
      ]
    };

    var result = RestaurantOnlineCartCalculator.Calculate(catalog, request);

    Assert.Equal(250m, result.MerchandiseTotal);
    Assert.Equal(125m, result.Lines[0].QuoteLine.UnitPrice);
    Assert.Equal("sin popote", result.Lines[0].OrderLine.Notes);

    catalog = Catalog(online: false);
    var exception = Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(catalog, request));
    Assert.Contains("no está habilitado", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CartCalculator_RejectsFractionalQuantityAndLongNotes()
  {
    var catalog = Catalog(online: true);
    var fractional = new RestaurantOnlineQuoteRequest
    {
      Lines = [new() { ProductId = 10, MenuSectionId = 3, Quantity = 1.5m, ModifierOptionIds = [101] }]
    };
    var longNote = new RestaurantOnlineQuoteRequest
    {
      Lines = [new() { ProductId = 10, MenuSectionId = 3, Quantity = 1, ModifierOptionIds = [101], Notes = new string('x', 501) }]
    };

    Assert.Throws<InvalidOperationException>(() => RestaurantOnlineCartCalculator.Calculate(catalog, fractional));
    Assert.Throws<InvalidOperationException>(() => RestaurantOnlineCartCalculator.Calculate(catalog, longNote));
  }

  [Fact]
  public void CartCalculator_RejectsRepeatedModifierIdsFromForgedCart()
  {
    var request = new RestaurantOnlineQuoteRequest
    {
      Lines =
      [
        new RestaurantOnlineCartLineRequest
        {
          ProductId = 10,
          MenuSectionId = 3,
          Quantity = 1,
          ModifierOptionIds = [101, 101]
        }
      ]
    };

    var exception = Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(Catalog(online: true), request));

    Assert.Contains("repetir", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData("{\"lines\":null}")]
  [InlineData("{\"lines\":[null]}")]
  [InlineData("{\"lines\":[{\"productId\":10,\"menuSectionId\":3,\"modifierOptionIds\":null}]}")]
  [InlineData("{\"lines\":[{\"productId\":10,\"menuSectionId\":3,\"comboSelections\":null}]}")]
  [InlineData("{\"lines\":[{\"productId\":10,\"menuSectionId\":3,\"comboSelections\":[null]}]}")]
  [InlineData("{\"lines\":[{\"productId\":10,\"menuSectionId\":3,\"comboSelections\":[{\"comboSlotId\":1,\"comboSlotOptionId\":1,\"modifierOptionIds\":null}]}]}")]
  public void CartCalculator_RejectsExplicitNullJsonCollectionsWithoutDereferencing(string json)
  {
    var request = JsonSerializer.Deserialize<RestaurantOnlineQuoteRequest>(
      json,
      new JsonSerializerOptions(JsonSerializerDefaults.Web));

    Assert.NotNull(request);
    Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(Catalog(online: true), request));
  }

  [Fact]
  public void CartCalculator_BoundsNestedCollectionCountsBeforePricing()
  {
    var tooManyLineModifiers = new RestaurantOnlineQuoteRequest
    {
      Lines = [new() { ProductId = 10, MenuSectionId = 3, ModifierOptionIds = Enumerable.Range(1, 101).Select(Convert.ToInt64).ToList() }]
    };
    var tooManyComboSelections = new RestaurantOnlineQuoteRequest
    {
      Lines =
      [
        new()
        {
          ProductId = 10,
          MenuSectionId = 3,
          ComboSelections = Enumerable.Range(1, 101)
            .Select(index => new RestaurantOnlineComboSelectionRequest
            {
              ComboSlotId = index,
              ComboSlotOptionId = index
            })
            .ToList()
        }
      ]
    };
    var tooManyComponentModifiers = new RestaurantOnlineQuoteRequest
    {
      Lines =
      [
        new()
        {
          ProductId = 10,
          MenuSectionId = 3,
          ComboSelections =
          [
            new()
            {
              ComboSlotId = 1,
              ComboSlotOptionId = 1,
              ModifierOptionIds = Enumerable.Range(1, 101).Select(Convert.ToInt64).ToList()
            }
          ]
        }
      ]
    };

    Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(Catalog(online: true), tooManyLineModifiers));
    Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(Catalog(online: true), tooManyComboSelections));
    Assert.Throws<InvalidOperationException>(() =>
      RestaurantOnlineCartCalculator.Calculate(Catalog(online: true), tooManyComponentModifiers));
  }

  [Fact]
  public void CartCalculator_RequiresOnlineOptInForEverySelectedComboComponent()
  {
    var component = new RestaurantProductDto
    {
      Id = 20,
      Sku = "SIDE-20",
      Name = "Ensalada",
      Price = 80m,
      IsActive = true,
      CanOrderOnline = false
    };
    var catalog = new RestaurantPosCatalogDto
    {
      Site = new RestaurantSiteDto { TaxRate = 0.16m, PricesIncludeTax = true },
      Sections =
      [
        new RestaurantMenuSectionDto
        {
          Id = 3,
          Name = "Combos",
          Products =
          [
            new RestaurantProductDto
            {
              Id = 10,
              Sku = "COMBO-10",
              Name = "Combo Bruno",
              ProductKind = RestaurantProductKinds.Combo,
              Price = 200m,
              IsActive = true,
              CanOrderOnline = true,
              ComboSlots =
              [
                new RestaurantComboSlotDto
                {
                  Id = 50,
                  Name = "Guarnición",
                  MinSelections = 1,
                  MaxSelections = 1,
                  IsActive = true,
                  Options =
                  [
                    new RestaurantComboSlotOptionDto
                    {
                      Id = 101,
                      ComponentProductId = component.Id,
                      ComponentProductName = component.Name,
                      ComponentSku = component.Sku,
                      PriceDelta = 15m,
                      IsActive = true,
                      ComponentProduct = component
                    }
                  ]
                }
              ]
            }
          ]
        }
      ]
    };
    var request = new RestaurantOnlineQuoteRequest
    {
      Lines =
      [
        new RestaurantOnlineCartLineRequest
        {
          ProductId = 10,
          MenuSectionId = 3,
          Quantity = 1,
          ComboSelections =
          [
            new RestaurantOnlineComboSelectionRequest
            {
              ComboSlotId = 50,
              ComboSlotOptionId = 101
            }
          ]
        }
      ]
    };

    var disabledComponent = Assert.Throws<RestaurantOrderBusinessRejectionException>(() =>
      RestaurantOnlineCartCalculator.Calculate(catalog, request));
    Assert.Equal(RestaurantOrderRejectionCategory.Combo, disabledComponent.Category);

    component.CanOrderOnline = true;
    var result = RestaurantOnlineCartCalculator.Calculate(catalog, request);

    Assert.Equal(215m, result.MerchandiseTotal);
    Assert.Equal("Guarnición: Ensalada", Assert.Single(result.Lines[0].QuoteLine.ComboSelections));
  }

  [Fact]
  public void RestaurantCheckoutProductionValidation_RequiresLiveWebhookAndHttps()
  {
    var invalid = new RestaurantCheckoutOptions
    {
      Environment = "Sandbox",
      Currency = "MXN",
      PayPalClientId = "client",
      PayPalClientSecret = "secret",
      PublicBaseUrl = "http://brunosgarden.com"
    };
    var errors = RestaurantCheckoutOptionsPolicy.Validate(invalid, production: true);

    Assert.Contains(errors, item => item.Contains("Live", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(errors, item => item.Contains("Webhook", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(errors, item => item.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void RestaurantCheckoutProductionValidation_LetsTheSiteStartBeforeTheLiveProfileIsInstalled()
  {
    // Online ordering ships disabled. On 2026-09-14 the first production publish crashed
    // Bruno's whole website at startup because no PayPal settings existed yet.
    var withoutPayPal = new RestaurantCheckoutOptions
    {
      Environment = "Sandbox",
      Currency = "MXN",
      PublicBaseUrl = "https://brunosgarden.com"
    };

    Assert.Empty(RestaurantCheckoutOptionsPolicy.Validate(withoutPayPal, production: true));

    var partialLiveProfile = new RestaurantCheckoutOptions
    {
      Environment = "Live",
      Currency = "MXN",
      PayPalClientId = "client",
      PublicBaseUrl = "https://brunosgarden.com"
    };
    var errors = RestaurantCheckoutOptionsPolicy.Validate(partialLiveProfile, production: true);
    Assert.Contains(errors, item => item.Contains("credenciales", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(errors, item => item.Contains("Webhook", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void PayPalRequestIds_AreDeterministicAndProviderBounded()
  {
    var source = "brunos-capture-17b37f34d9cc45aeb45ff350d2ca7750";
    var first = PayPalRequestId.From(source);

    Assert.Equal(first, PayPalRequestId.From(source));
    Assert.Equal(PayPalRequestId.MaximumLength, first.Length);
    Assert.NotEqual(first, PayPalRequestId.From(source + "-different"));
  }

  [Fact]
  public void PayPalMetadata_IsStableAndScopedToTheVerifiedPublicSite()
  {
    var checkoutAttemptId = Guid.Parse("17b37f34-d9cc-45ae-b45f-f350d2ca7750");
    var fingerprint = new string('a', 64);

    Assert.Equal(
      "online:brunos-main:17b37f34d9cc45aeb45ff350d2ca7750",
      RestaurantPayPalMetadataPolicy.ReferenceId("brunos-main", checkoutAttemptId));
    Assert.Equal(
      "BRUNOS-MAIN-17b37f34d9cc45aeb45ff350d2ca7750",
      RestaurantPayPalMetadataPolicy.InvoiceId("brunos-main", checkoutAttemptId));
    Assert.Equal(
      $"brunos-main:{fingerprint.ToUpperInvariant()}",
      RestaurantPayPalMetadataPolicy.CustomId("brunos-main", fingerprint));

    var firstRequestId = RestaurantPayPalMetadataPolicy.RequestId("brunos-main", "capture", checkoutAttemptId);
    Assert.Equal(firstRequestId, RestaurantPayPalMetadataPolicy.RequestId("brunos-main", "capture", checkoutAttemptId));
    Assert.Equal(PayPalRequestId.MaximumLength, firstRequestId.Length);
    Assert.NotEqual(
      firstRequestId,
      RestaurantPayPalMetadataPolicy.RequestId("another-site", "capture", checkoutAttemptId));
    Assert.NotEqual(
      firstRequestId,
      RestaurantPayPalMetadataPolicy.RequestId("brunos-main", "create", checkoutAttemptId));
  }

  [Theory]
  [InlineData("unsafe site")]
  [InlineData("site/with/slashes")]
  public void PayPalMetadata_RejectsUnsafePublicSiteKeys(string publicSiteKey)
  {
    Assert.Throws<ArgumentException>(() =>
      RestaurantPayPalMetadataPolicy.ReferenceId(publicSiteKey, Guid.NewGuid()));
  }

  [Fact]
  public void PayPalMetadata_RejectsOversizedKeysAndNonSha256Fingerprints()
  {
    Assert.Throws<ArgumentException>(() =>
      RestaurantPayPalMetadataPolicy.ReferenceId(new string('a', 49), Guid.NewGuid()));
    Assert.Throws<ArgumentException>(() =>
      RestaurantPayPalMetadataPolicy.CustomId("brunos-main", "not-a-sha256-fingerprint"));
    Assert.Throws<ArgumentException>(() =>
      RestaurantPayPalMetadataPolicy.ReferenceId("brunos-main", Guid.Empty));
  }

  private static RestaurantOnlineQuoteSnapshot Quote()
    => new()
    {
      PublicSiteId = 21,
      PublicSiteKey = "brunos-main",
      Rfc = "BRUNOS260707L26",
      SiteId = 1,
      SettingsConfigurationVersion = 7,
      TermsVersion = "2026-09-14",
      PrivacyVersion = "2026-09-14",
      Currency = "MXN",
      Total = 125m,
      Request = new RestaurantOnlineQuoteRequest
      {
        Lines =
        [
          new RestaurantOnlineCartLineRequest
          {
            ProductId = 10,
            MenuSectionId = 3,
            Quantity = 1,
            ModifierOptionIds = [7, 5]
          }
        ]
      }
    };

  private static RestaurantPosCatalogDto Catalog(bool online)
    => new()
    {
      Site = new RestaurantSiteDto { TaxRate = 0.16m, PricesIncludeTax = true },
      Sections =
      [
        new RestaurantMenuSectionDto
        {
          Id = 3,
          Name = "Bebidas",
          Products =
          [
            new RestaurantProductDto
            {
              Id = 10,
              Sku = "BEB-10",
              Name = "Limonada",
              Price = 100m,
              IsActive = true,
              CanOrderOnline = online,
              ModifierGroups =
              [
                new RestaurantModifierGroupDto
                {
                  Id = 50,
                  Name = "Tamaño",
                  MinSelections = 1,
                  MaxSelections = 1,
                  Options = [new RestaurantModifierOptionDto { Id = 101, Name = "Grande", PriceDelta = 25m }]
                }
              ]
            }
          ]
        }
      ]
    };
}
