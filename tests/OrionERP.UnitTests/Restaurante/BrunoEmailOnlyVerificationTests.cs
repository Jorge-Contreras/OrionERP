namespace OrionERP.UnitTests.Restaurante;

public sealed class BrunoEmailOnlyVerificationTests
{
  [Fact]
  public void LoyaltyActivationAndUsage_RequireConfirmedEmailOnly()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");

    Assert.Contains("WHEN (EmailVerified=1 OR @EmailVerified=1)", service, StringComparison.Ordinal);
    Assert.Contains(
      "member.NormalizedPhone=@NormalizedPhone AND member.EmailVerified=1",
      service,
      StringComparison.Ordinal);
    Assert.DoesNotContain(
      "member.NormalizedPhone=@NormalizedPhone AND member.PhoneVerified=1",
      service,
      StringComparison.Ordinal);
    Assert.DoesNotContain("AND (PhoneVerified=1 OR @PhoneVerified=1)", service, StringComparison.Ordinal);
    Assert.DoesNotContain("AND EmailVerified=1 AND PhoneVerified=1", service, StringComparison.Ordinal);
  }

  [Fact]
  public void LoyaltyPhoneLookup_NormalizesLocalMexicanNumbers()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");

    Assert.Contains("if (digits.Length == 10) digits = $\"52{digits}\";", service, StringComparison.Ordinal);
    Assert.Contains("return digits.Length is >= 12 and <= 15 ? digits : null;", service, StringComparison.Ordinal);
  }

  [Fact]
  public void Registration_SendsEmailAndShowsEmailConfirmationInstructions()
  {
    var registration = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/Register.cshtml.cs");
    var confirmation = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/RegisterConfirmation.cshtml");

    Assert.Contains("SendConfirmationLinkAsync", registration, StringComparison.Ordinal);
    Assert.Contains("RedirectToPage(\"/Account/RegisterConfirmation\")", registration, StringComparison.Ordinal);
    Assert.Contains("activar tu membresía", confirmation, StringComparison.Ordinal);
    Assert.DoesNotContain("PhoneVerification", registration, StringComparison.Ordinal);
    Assert.DoesNotContain("VerifyPhone", registration, StringComparison.Ordinal);
  }

  [Fact]
  public void Registration_ShowsMembershipConflictAsValidationInsteadOfErrorPage()
  {
    var registration = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/Register.cshtml.cs");
    var loyaltyService = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/LoyaltyService.cs");

    Assert.Contains("catch (LoyaltyMembershipConflictException ex)", registration, StringComparison.Ordinal);
    Assert.Contains("Ya existe una membresía con ese correo o teléfono", registration, StringComparison.Ordinal);
    Assert.Contains("TryDeleteCreatedUserAsync(user)", registration, StringComparison.Ordinal);
    Assert.Contains("throw new LoyaltyMembershipConflictException", loyaltyService, StringComparison.Ordinal);
    Assert.DoesNotContain(
      "throw new InvalidOperationException(\"El correo o teléfono ya pertenece a otra membresía.\")",
      loyaltyService,
      StringComparison.Ordinal);
  }

  [Fact]
  public void ResendConfirmation_IsProtectedAndDoesNotRevealAccountState()
  {
    var page = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/ResendConfirmation.cshtml");
    var handler = ReadRepoFile("src/OrionERP.Bruno.Web/Pages/Account/ResendConfirmation.cshtml.cs");

    Assert.Contains("Si existe una cuenta pendiente", page, StringComparison.Ordinal);
    Assert.Contains("data-action=\"resend-confirmation\"", page, StringComparison.Ordinal);
    Assert.Contains("[EnableRateLimiting(\"account\")]", handler, StringComparison.Ordinal);
    Assert.Contains("\"resend-confirmation\"", handler, StringComparison.Ordinal);
    Assert.Contains("!user.EmailConfirmed", handler, StringComparison.Ordinal);
    Assert.Contains("!user.ClosedAt.HasValue", handler, StringComparison.Ordinal);
    Assert.Contains("SendConfirmationLinkAsync", handler, StringComparison.Ordinal);
  }

  [Fact]
  public void BrunoSurface_HasNoActiveTwilioIntegration()
  {
    var files = new[]
    {
      "src/OrionERP.Bruno.Web/Program.cs",
      "src/OrionERP.Bruno.Web/appsettings.json",
      "src/OrionERP.Bruno.Web/Configuration/BrunoSecurityOptions.cs",
      "src/OrionERP.Bruno.Web/Services/BrunoVerificationServices.cs",
      "deploy/brunos/production.env.example"
    };
    var content = string.Join('\n', files.Select(ReadRepoFile));

    Assert.DoesNotContain("Twilio", content, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("IBrunoPhoneVerificationService", content, StringComparison.Ordinal);
    Assert.False(File.Exists(GetRepoPath("src/OrionERP.Bruno.Web/Pages/Account/VerifyPhone.cshtml")));
    Assert.False(File.Exists(GetRepoPath("src/OrionERP.Bruno.Web/Pages/Account/VerifyPhone.cshtml.cs")));
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(GetRepoPath(relativePath));

  private static string GetRepoPath(string relativePath)
    => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath));
}
