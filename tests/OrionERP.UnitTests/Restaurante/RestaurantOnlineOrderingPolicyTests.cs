using OrionERP.Application.Features.Payments.Clip;
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
  public void DeliveryFingerprint_CoversServerFeeDestinationVerificationAndHandoff()
  {
    var original = Quote();
    original.DeliveryFee = 45m;
    original.Total += original.DeliveryFee;
    original.Request.Fulfillment = new RestaurantOnlineFulfillmentRequest
    {
      Type = RestaurantOrderTypes.Delivery,
      AddressLine = "Calle Principal 5, Calpulalpan, Tlaxcala",
      AddressComplement = "Interior 2",
      Latitude = 19.586123m,
      Longitude = -98.569321m,
      GooglePlaceId = "place-17",
      AddressVerificationStatus = RestaurantDeliveryAddressVerificationStatuses.Validated,
      DropoffPreference = RestaurantDeliveryDropoffPreferences.LeaveAtDoor,
      Instructions = "Portón verde"
    };
    var fingerprint = RestaurantOnlineQuotePolicy.CreateFingerprint(original);

    var feeChanged = Clone(original); feeChanged.DeliveryFee++;
    var pinChanged = Clone(original); pinChanged.Request.Fulfillment.Latitude += 0.000001m;
    var handoffChanged = Clone(original); handoffChanged.Request.Fulfillment.DropoffPreference = RestaurantDeliveryDropoffPreferences.MeetOutside;
    var verificationChanged = Clone(original); verificationChanged.Request.Fulfillment.AddressVerificationStatus = RestaurantDeliveryAddressVerificationStatuses.ManualUnverified;

    Assert.NotEqual(fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(feeChanged));
    Assert.NotEqual(fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(pinChanged));
    Assert.NotEqual(fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(handoffChanged));
    Assert.NotEqual(fingerprint, RestaurantOnlineQuotePolicy.CreateFingerprint(verificationChanged));
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
  public void RestaurantCheckoutCredentials_ChargeWithTheKeyAloneAndTrackTheRefundSecretApart()
  {
    // Verificado contra el sandbox el 2026-09-17: la API de pagos acepta la clave
    // sola como Bearer. Los endpoints de reembolso respondieron 401 con Bearer y
    // con Basic, así que el secreto se conserva y se declara aparte mientras se
    // confirma con Clip; no puede bloquear el arranque de un sitio que sí cobra.
    var options = new RestaurantCheckoutOptions
    {
      Currency = "MXN",
      ClipApiKey = "test_3f51aaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      PublicBaseUrl = "https://brunosgarden.com"
    };

    Assert.True(options.IsClipConfigured);
    Assert.False(options.AreRefundsConfigured);
    Assert.Empty(RestaurantCheckoutOptionsPolicy.Validate(options, production: false));

    options.ClipApiSecret = "secreto-de-prueba";
    Assert.True(options.AreRefundsConfigured);
  }

  [Fact]
  public void RestaurantCheckoutProductionValidation_RequiresLiveCredentialsAndHttps()
  {
    var invalid = new RestaurantCheckoutOptions
    {
      Environment = "Sandbox",
      Currency = "MXN",
      ClipApiKey = "test_3f51aaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
      PublicBaseUrl = "http://brunosgarden.com"
    };
    var errors = RestaurantCheckoutOptionsPolicy.Validate(invalid, production: true);

    Assert.Contains(errors, item => item.Contains("Live", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(errors, item => item.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void RestaurantCheckoutProductionValidation_LetsTheSiteStartBeforeTheLiveProfileIsInstalled()
  {
    // Online ordering ships disabled. On 2026-09-14 the first production publish crashed
    // Bruno's whole website at startup because no payment settings existed yet.
    var withoutClip = new RestaurantCheckoutOptions
    {
      Environment = "Sandbox",
      Currency = "MXN",
      PublicBaseUrl = "https://brunosgarden.com"
    };

    Assert.Empty(RestaurantCheckoutOptionsPolicy.Validate(withoutClip, production: true));
  }

  [Theory]
  // Una clave de prueba en produccion no cobraria nada y el pedido se perderia
  // en silencio; una productiva en sandbox cobraria de verdad. El prefijo de
  // Clip permite atrapar ambos casos antes de arrancar.
  [InlineData("Live", "test_3f51aaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
  [InlineData("Sandbox", "3f51aaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
  public void RestaurantCheckoutValidation_RejectsAnApiKeyFromTheWrongEnvironment(
    string environment,
    string apiKey)
  {
    var options = new RestaurantCheckoutOptions
    {
      Environment = environment,
      Currency = "MXN",
      ClipApiKey = apiKey,
      PublicBaseUrl = "https://brunosgarden.com"
    };

    Assert.False(options.IsApiKeyEnvironmentConsistent);
    Assert.Contains(
      RestaurantCheckoutOptionsPolicy.Validate(options, production: false),
      item => item.Contains("test_", StringComparison.Ordinal));
  }

  [Fact]
  public void RestaurantCheckoutWebhook_IsDerivedFromTheHttpsPublicAddress()
  {
    // Clip no da de alta webhooks ni entrega un identificador que validar: la URL
    // viaja en el cuerpo de cada pago, asi que lo unico necesario para recibir
    // avisos es una direccion publica HTTPS.
    var ready = new RestaurantCheckoutOptions { PublicBaseUrl = "https://brunosgarden.com" };
    Assert.True(ready.IsWebhookConfigured);
    Assert.Equal(
      "https://brunosgarden.com/api/restaurant/checkout/clip-webhook",
      ready.WebhookUrl);

    Assert.False(new RestaurantCheckoutOptions { PublicBaseUrl = "http://brunosgarden.com" }.IsWebhookConfigured);
    Assert.False(new RestaurantCheckoutOptions { PublicBaseUrl = null }.IsWebhookConfigured);
  }

  [Fact]
  public void RestaurantCheckoutValidation_RejectsInstallmentsThatCannotBeRefunded()
  {
    // Clip no reembolsa por API los pagos diferidos (AI1806), asi que un valor
    // fuera de la lista dejaria al panel sin forma de devolver el dinero.
    var options = new RestaurantCheckoutOptions
    {
      Currency = "MXN",
      Installments = 4,
      PublicBaseUrl = "https://brunosgarden.com"
    };

    Assert.Contains(
      RestaurantCheckoutOptionsPolicy.Validate(options, production: false),
      item => item.Contains("Installments", StringComparison.Ordinal));
  }

  [Fact]
  public void ClipExternalReference_RoundTripsTheCheckoutAttemptWithinClipsLimit()
  {
    // Es el unico hilo que liga un cargo con su pedido cuando la respuesta se
    // pierde, porque GET /payments solo filtra por fecha.
    var checkoutAttemptId = Guid.NewGuid();
    var reference = ClipExternalReference.From(checkoutAttemptId);

    Assert.Equal(ClipExternalReference.MaximumLength, reference.Length);
    Assert.True(ClipExternalReference.TryParse(reference, out var parsed));
    Assert.Equal(checkoutAttemptId, parsed);

    Assert.Throws<ArgumentException>(() => ClipExternalReference.From(Guid.Empty));
    Assert.False(ClipExternalReference.TryParse("no-es-un-guid", out _));
    Assert.False(ClipExternalReference.TryParse(null, out _));
    Assert.False(ClipExternalReference.TryParse(checkoutAttemptId.ToString("N"), out _));
  }

  [Theory]
  [InlineData("RE-ISS01", "fondos")]
  [InlineData("RE-ISS07", "vencida")]
  [InlineData("RE-3DS01", "verificación")]
  public void ClipStatusMessages_ExplainWhatTheCardHolderCanDo(string statusCode, string expected)
  {
    // Un "pago rechazado" generico hace que la gente reintente con la misma
    // tarjeta; decir que faltan fondos o que vencio, no.
    Assert.Contains(expected, ClipStatusMessages.ForCode(statusCode), StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ClipStatusMessages_FallBackWithoutInventingAReason()
  {
    var fallback = ClipStatusMessages.ForCode("CODIGO-NUEVO-DE-CLIP");
    Assert.False(string.IsNullOrWhiteSpace(fallback));
    Assert.DoesNotContain("fondos", fallback, StringComparison.OrdinalIgnoreCase);
    Assert.True(ClipStatusMessages.AllowsAnotherCard("RE-ISS01"));
    Assert.False(ClipStatusMessages.AllowsAnotherCard("RE-ERI05"));
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

  private static RestaurantOnlineQuoteSnapshot Clone(RestaurantOnlineQuoteSnapshot source)
    => JsonSerializer.Deserialize<RestaurantOnlineQuoteSnapshot>(JsonSerializer.Serialize(source))
      ?? throw new InvalidOperationException("Could not clone quote.");

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
