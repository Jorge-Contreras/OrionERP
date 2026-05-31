using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.UnitTests.Bonhomia;

public class BonhomiaCheckoutOptionsValidatorTests
{
  [Fact]
  public void ValidateForEnvironment_AllowsSandbox_WhenDevelopment()
  {
    var options = new BonhomiaCheckoutOptions
    {
      Environment = "Sandbox"
    };

    var errors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(options, "Development");

    Assert.Empty(errors);
  }

  [Fact]
  public void ValidateForEnvironment_RejectsSandboxPayPal_WhenProduction()
  {
    var options = CreateProductionOptions();
    options.Environment = "Sandbox";

    var errors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(options, "Production");

    Assert.Contains(errors, error => error.Contains("Live or Production", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void ValidateForEnvironment_RejectsMissingPayPalCredentials_WhenProduction()
  {
    var options = CreateProductionOptions();
    options.PayPalClientId = string.Empty;
    options.PayPalClientSecret = string.Empty;

    var errors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(options, "Production");

    Assert.Contains(errors, error => error.Contains("PayPalClientId", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(errors, error => error.Contains("PayPalClientSecret", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void ValidateForEnvironment_RejectsNonHttpsPublicBaseUrl_WhenProduction()
  {
    var options = CreateProductionOptions();
    options.PublicBaseUrl = "http://bonhomiasuites.com";

    var errors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(options, "Production");

    Assert.Contains(errors, error => error.Contains("absolute HTTPS URL", StringComparison.OrdinalIgnoreCase));
  }

  [Fact]
  public void ValidateForEnvironment_AcceptsLivePayPalWithHttpsPublicBaseUrl_WhenProduction()
  {
    var options = CreateProductionOptions();

    var errors = BonhomiaCheckoutOptionsValidator.ValidateForEnvironment(options, "Production");

    Assert.Empty(errors);
  }

  private static BonhomiaCheckoutOptions CreateProductionOptions()
    => new()
    {
      Environment = "Live",
      PayPalClientId = "live-client-id",
      PayPalClientSecret = "live-client-secret",
      PublicBaseUrl = "https://bonhomiasuites.com"
    };
}
