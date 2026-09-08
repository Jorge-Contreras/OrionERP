using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Platform;

public sealed class PublicWebsiteInstanceTests
{
  [Fact]
  public void Create_NormalizesTrustedInstanceConfiguration()
  {
    var instance = PublicWebsiteInstancePolicy.Create(
      Options(
        publicSiteKey: " Bonhomia-Main ",
        companyRfc: "ohm191112q26",
        siteKey: "BONHOMIA-SUITES",
        moduleCode: "hospitality",
        canonicalHost: "BONHOMIASUITES.COM."),
      PlatformModuleCodes.Hospitality);

    Assert.Equal("bonhomia-main", instance.PublicSiteKey);
    Assert.Equal("OHM191112Q26", instance.ExpectedCompanyRfc);
    Assert.Equal("bonhomia-suites", instance.SiteKey);
    Assert.Equal(PlatformModuleCodes.Hospitality, instance.ModuleCode);
    Assert.Equal("bonhomiasuites.com", instance.CanonicalHost);
    Assert.Equal(5010, instance.LoopbackPort);
  }

  [Theory]
  [InlineData("")]
  [InlineData("ACCOUNTING_CORE")]
  [InlineData("RESTAURANT")]
  public void Create_RejectsMissingOrWrongFixedModule(string configuredModule)
  {
    var options = Options(moduleCode: configuredModule);

    Assert.Throws<InvalidOperationException>(() =>
      PublicWebsiteInstancePolicy.Create(options, PlatformModuleCodes.Hospitality));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  [InlineData(65536)]
  public void Create_RejectsInvalidLoopbackPort(int port)
  {
    var options = Options();
    options.LoopbackPort = port;

    Assert.Throws<InvalidOperationException>(() =>
      PublicWebsiteInstancePolicy.Create(options, PlatformModuleCodes.Hospitality));
  }

  [Theory]
  [InlineData(
    "src/OrionERP.Bonhomia.Web/appsettings.Development.json",
    "bonhomia-main",
    "OHM191112Q26",
    "bonhomia-suites",
    "HOSPITALITY",
    "bonhomiasuites.com")]
  [InlineData(
    "src/OrionERP.Bruno.Web/appsettings.Development.json",
    "brunos-main",
    "BRUNOS260707L26",
    "brunos-01",
    "RESTAURANT",
    "brunosgarden.com")]
  public void DevelopmentDefaults_MatchProvisionedPublicSiteKeys(
    string path,
    string publicSiteKey,
    string companyRfc,
    string siteKey,
    string moduleCode,
    string canonicalHost)
  {
    using var json = JsonDocument.Parse(RepoFile.Read(path));
    var section = json.RootElement.GetProperty(PublicWebsiteInstanceOptions.SectionName);

    Assert.Equal(publicSiteKey, section.GetProperty("PublicSiteKey").GetString());
    Assert.Equal(companyRfc, section.GetProperty("ExpectedCompanyRfc").GetString());
    Assert.Equal(siteKey, section.GetProperty("SiteKey").GetString());
    Assert.Equal(moduleCode, section.GetProperty("ModuleCode").GetString());
    Assert.Equal(canonicalHost, section.GetProperty("CanonicalHost").GetString());
  }

  [Theory]
  [InlineData("src/OrionERP.Bonhomia.Web/appsettings.json", 5010)]
  [InlineData("src/OrionERP.Bruno.Web/appsettings.json", 5020)]
  public void ProductionBase_RequiresExplicitIdentityButKeepsCompatiblePort(
    string path,
    int expectedPort)
  {
    using var json = JsonDocument.Parse(RepoFile.Read(path));
    var section = json.RootElement.GetProperty(PublicWebsiteInstanceOptions.SectionName);

    Assert.Equal(string.Empty, section.GetProperty("PublicSiteKey").GetString());
    Assert.Equal(string.Empty, section.GetProperty("ExpectedCompanyRfc").GetString());
    Assert.Equal(string.Empty, section.GetProperty("SiteKey").GetString());
    Assert.Equal(string.Empty, section.GetProperty("ModuleCode").GetString());
    Assert.Equal(string.Empty, section.GetProperty("CanonicalHost").GetString());
    Assert.Equal(expectedPort, section.GetProperty("LoopbackPort").GetInt32());
  }

  [Fact]
  public void PublishTargets_MaterializeExplicitCurrentInstanceSettings()
  {
    var bonhomia = RepoFile.Read("Publish-Bonhomia-prod.ps1");
    var bruno = RepoFile.Read("Publish-Bruno-prod.ps1");
    var publishAll = RepoFile.Read("Publish-All-prod.ps1");

    foreach (var script in new[] { bonhomia, publishAll })
    {
      Assert.Contains("deployment\\public-sites\\bonhomia-main.json", script, StringComparison.Ordinal);
      Assert.Contains("http://127.0.0.1:5010/readyz", script, StringComparison.Ordinal);
      Assert.DoesNotContain("http://127.0.0.1:5010/healthz", script, StringComparison.Ordinal);
    }

    foreach (var script in new[] { bruno, publishAll })
    {
      Assert.Contains("deployment\\public-sites\\brunos-main.json", script, StringComparison.Ordinal);
      Assert.Contains("http://127.0.0.1:5020/readyz", script, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void DeploymentProfiles_ContainCompleteNonSecretInstanceConfiguration()
  {
    using var bonhomia = JsonDocument.Parse(RepoFile.Read("deployment/public-sites/bonhomia-main.json"));
    using var bruno = JsonDocument.Parse(RepoFile.Read("deployment/public-sites/brunos-main.json"));

    AssertDeploymentIdentity(
      bonhomia.RootElement,
      "bonhomia-main",
      "OHM191112Q26",
      "bonhomia-suites",
      PlatformModuleCodes.Hospitality,
      "bonhomiasuites.com",
      5010);
    Assert.True(bonhomia.RootElement.TryGetProperty("HospitalityWebsite", out var hospitality));
    Assert.NotEmpty(hospitality.GetProperty("Rooms").EnumerateArray());
    Assert.Equal(
      "recepcion@bonhomiasuites.com",
      bonhomia.RootElement.GetProperty("BonhomiaGraphMail").GetProperty("SenderAddress").GetString());

    AssertDeploymentIdentity(
      bruno.RootElement,
      "brunos-main",
      "BRUNOS260707L26",
      "brunos-01",
      PlatformModuleCodes.Restaurant,
      "brunosgarden.com",
      5020);
    Assert.Equal(
      "info@brunosgarden.com",
      bruno.RootElement.GetProperty("BrunoGraphMail").GetProperty("SenderAddress").GetString());

    AssertNoSecretProperties(bonhomia.RootElement);
    AssertNoSecretProperties(bruno.RootElement);
  }

  [Theory]
  [InlineData(
    "deployment/public-sites/bonhomia-main.json",
    "src/OrionERP.Bonhomia.Web/wwwroot",
    PlatformModuleCodes.Hospitality)]
  [InlineData(
    "deployment/public-sites/brunos-main.json",
    "src/OrionERP.Bruno.Web/wwwroot",
    PlatformModuleCodes.Restaurant)]
  public void DeploymentProfiles_PassTheSameTypedPoliciesAndAssetChecksAsTheHosts(
    string profilePath,
    string webRootPath,
    string moduleCode)
  {
    using var document = JsonDocument.Parse(RepoFile.Read(profilePath));
    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var identityOptions = JsonSerializer.Deserialize<PublicWebsiteInstanceOptions>(
      document.RootElement.GetProperty("PublicWebsite").GetRawText(),
      jsonOptions)!;
    var identity = PublicWebsiteInstancePolicy.Create(identityOptions, moduleCode);

    string[] requiredAssets;
    HospitalityWebsiteOptions? hospitalityOptions = null;
    if (moduleCode == PlatformModuleCodes.Hospitality)
    {
      hospitalityOptions = JsonSerializer.Deserialize<HospitalityWebsiteOptions>(
        document.RootElement.GetProperty("HospitalityWebsite").GetRawText(),
        jsonOptions)!;
      requiredAssets = hospitalityOptions.HomeGalleryAssetKeys
        .Concat(hospitalityOptions.BuildingGalleryAssetKeys)
        .Concat(hospitalityOptions.Rooms.Select(room => room.PrimaryAssetKey))
        .Concat(hospitalityOptions.Rooms.SelectMany(room => room.GalleryAssetKeys))
        .Append("hero-image")
        .Append("letterhead-logo")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }
    else
    {
      requiredAssets =
      [
        "open-graph", "home-hero", "menu-hero", "visit-hero", "membership-hero",
        "hero-decoration", "membership-decoration"
      ];
    }

    var presentationOptions = JsonSerializer.Deserialize<PublicWebsitePresentationOptions>(
      document.RootElement.GetProperty("PublicWebsitePresentation").GetRawText(),
      jsonOptions)!;
    var presentation = PublicWebsitePresentationPolicy.Create(
      presentationOptions,
      identity,
      requiredAssets);
    if (hospitalityOptions is not null)
      Assert.NotNull(HospitalityWebsitePolicy.Create(hospitalityOptions, presentation));

    PublicWebsitePresentationPolicy.EnsureAssetsExist(
      presentation,
      RepositoryPath(webRootPath));
  }

  [Theory]
  [InlineData(
    "deployment/public-sites/bonhomia-main.json",
    "src/OrionERP.Bonhomia.Web/appsettings.Development.json",
    true)]
  [InlineData(
    "deployment/public-sites/brunos-main.json",
    "src/OrionERP.Bruno.Web/appsettings.Development.json",
    false)]
  public void DeploymentProfiles_DoNotDriftFromValidatedDevelopmentProfiles(
    string deploymentPath,
    string developmentPath,
    bool hasHospitality)
  {
    using var deployment = JsonDocument.Parse(RepoFile.Read(deploymentPath));
    using var development = JsonDocument.Parse(RepoFile.Read(developmentPath));

    Assert.Equal(
      development.RootElement.GetProperty("PublicWebsitePresentation").GetRawText(),
      deployment.RootElement.GetProperty("PublicWebsitePresentation").GetRawText());
    if (hasHospitality)
      Assert.Equal(
        development.RootElement.GetProperty("HospitalityWebsite").GetRawText(),
        deployment.RootElement.GetProperty("HospitalityWebsite").GetRawText());
  }

  [Fact]
  public void PublishWorker_WritesTargetConfigurationWithoutEmbeddingSecrets()
  {
    var worker = RepoFile.Read("Publish-prod.ps1");
    var hospitalityProgram = RepoFile.Read("src/OrionERP.Bonhomia.Web/Program.cs");
    var restaurantProgram = RepoFile.Read("src/OrionERP.Bruno.Web/Program.cs");

    Assert.Contains("[object]$InstanceSettings", worker, StringComparison.Ordinal);
    Assert.Contains("[string]$InstanceSettingsPath", worker, StringComparison.Ordinal);
    Assert.Contains("Read-InstanceSettings", worker, StringComparison.Ordinal);
    Assert.Contains("appsettings.Instance.json", worker, StringComparison.Ordinal);
    Assert.Contains("Write-InstanceSettings -Destination $outputFullPath -Settings $InstanceSettings", worker, StringComparison.Ordinal);
    Assert.Contains("AddJsonFile(\"appsettings.Instance.json\"", hospitalityProgram, StringComparison.Ordinal);
    Assert.Contains("AddJsonFile(\"appsettings.Instance.json\"", restaurantProgram, StringComparison.Ordinal);
    Assert.Contains("profiles may contain public configuration only", worker, StringComparison.Ordinal);
    Assert.Contains("Assert-InstanceSettings -Settings $InstanceSettings", worker, StringComparison.Ordinal);
    Assert.Contains("Test-InstanceProfileArtifact", worker, StringComparison.Ordinal);
    Assert.Contains("ASPNETCORE_ENVIRONMENT", worker, StringComparison.Ordinal);
    Assert.Contains("DOTNET_ENVIRONMENT", worker, StringComparison.Ordinal);
    Assert.Contains("EnvironmentVariableTarget]::Process", worker, StringComparison.Ordinal);
    Assert.Contains("$environmentSnapshot", worker, StringComparison.Ordinal);
    Assert.Contains("Resolve-OrionSafeChildDirectory", worker, StringComparison.Ordinal);
    Assert.Contains("Assert-LoopbackHealthCheckUrl", worker, StringComparison.Ordinal);
    Assert.Contains("Coordinated publishing requires service", worker, StringComparison.Ordinal);
    Assert.Contains("Save-PreviousRelease", worker, StringComparison.Ordinal);
    Assert.Contains("RollbackToPreviousRelease", worker, StringComparison.Ordinal);
    Assert.Contains("Public website address overrides are forbidden in Production", hospitalityProgram, StringComparison.Ordinal);
    Assert.Contains("Public website address overrides are forbidden in Production", restaurantProgram, StringComparison.Ordinal);
  }

  [Fact]
  public void HospitalityDevelopmentEnvironmentOverridesUserSecrets()
  {
    var program = RepoFile.Read("src/OrionERP.Bonhomia.Web/Program.cs");
    var userSecrets = program.IndexOf("AddUserSecrets<Program>", StringComparison.Ordinal);
    var environment = program.IndexOf("AddEnvironmentVariables(prefix: \"ASPNETCORE_\")", StringComparison.Ordinal);
    var commandLine = program.IndexOf("AddCommandLine(args)", StringComparison.Ordinal);

    Assert.True(userSecrets >= 0);
    Assert.True(environment > userSecrets);
    Assert.True(commandLine > environment);
  }

  [Fact]
  public void ProductionPublishEntryPoints_UseTheCommonGitAndRollbackSafetyContract()
  {
    var safety = RepoFile.Read("deployment/Publish-Safety.ps1");
    var worker = RepoFile.Read("Publish-prod.ps1");
    var publishAll = RepoFile.Read("Publish-All-prod.ps1");
    var bonhomia = RepoFile.Read("Publish-Bonhomia-prod.ps1");
    var bruno = RepoFile.Read("Publish-Bruno-prod.ps1");

    Assert.Contains("function Assert-OrionProductionGitState", safety, StringComparison.Ordinal);
    Assert.Contains("origin/main", safety, StringComparison.Ordinal);
    Assert.Contains("Assert-OrionProductionGitState", worker, StringComparison.Ordinal);
    Assert.Contains("Assert-OrionProductionGitState", publishAll, StringComparison.Ordinal);
    Assert.Contains("deployment\\Publish-Safety.ps1", bonhomia, StringComparison.Ordinal);
    Assert.Contains("Assert-OrionProductionGitState", bonhomia, StringComparison.Ordinal);
    Assert.Contains("AllowNonMain", bonhomia, StringComparison.Ordinal);
    Assert.Contains("AllowDirty", bonhomia, StringComparison.Ordinal);
    Assert.Contains("RollbackToPreviousRelease", bonhomia, StringComparison.Ordinal);
    Assert.Contains("deployment\\Publish-Safety.ps1", bruno, StringComparison.Ordinal);
    Assert.Contains("Assert-OrionProductionGitState", bruno, StringComparison.Ordinal);
    Assert.Contains("AllowNonMain", bruno, StringComparison.Ordinal);
    Assert.Contains("AllowDirty", bruno, StringComparison.Ordinal);
    Assert.Contains("RollbackToPreviousRelease", bruno, StringComparison.Ordinal);
  }

  [Fact]
  public void PublicHostServices_BuildWithoutAdministrationDependencies()
  {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddPublicWebsiteInstance(
      "Server=(localdb)\\MSSQLLocalDB;Database=Orion_Sandbox;Integrated Security=True;TrustServerCertificate=True;",
      Definition(),
      Presentation());

    using var provider = services.BuildServiceProvider(new ServiceProviderOptions
    {
      ValidateOnBuild = true,
      ValidateScopes = true
    });
    using var scope = provider.CreateScope();

    Assert.NotNull(provider.GetRequiredService<IPublicWebsiteInstanceContext>());
    Assert.Equal("bonhomia-main", provider.GetRequiredService<PublicWebsitePresentationDefinition>().PublicSiteKey);
    Assert.NotNull(provider.GetRequiredService<ICurrentRfcAccessor>());
    Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPublicSiteResolver>());
    Assert.Null(scope.ServiceProvider.GetService<IPlatformAdministrationAccessValidator>());
    Assert.Null(scope.ServiceProvider.GetService<IPlatformAdministrationScopeAccessor>());
    Assert.Null(scope.ServiceProvider.GetService<IPlatformAdministrationReader>());
    Assert.Null(scope.ServiceProvider.GetService<IPlatformAdministrationService>());
  }

  [Fact]
  public async Task CanonicalHostMiddleware_RejectsUnexpectedProductionHost()
  {
    var nextCalled = false;
    var middleware = new ConfiguredCanonicalHostMiddleware(
      _ => { nextCalled = true; return Task.CompletedTask; },
      Definition(),
      Environment(Environments.Production));
    var context = Context("attacker.example", "/menu");

    await middleware.InvokeAsync(context);

    Assert.False(nextCalled);
    Assert.Equal(StatusCodes.Status421MisdirectedRequest, context.Response.StatusCode);
  }

  [Fact]
  public async Task CanonicalHostMiddleware_RedirectsOnlyTheExplicitWwwAlias()
  {
    var middleware = new ConfiguredCanonicalHostMiddleware(
      _ => Task.CompletedTask,
      Definition(),
      Environment(Environments.Production));
    var context = Context("www.bonhomiasuites.com", "/reservar");
    context.Request.QueryString = new QueryString("?step=2");

    await middleware.InvokeAsync(context);

    Assert.Equal(StatusCodes.Status308PermanentRedirect, context.Response.StatusCode);
    Assert.Equal("https://bonhomiasuites.com/reservar?step=2", context.Response.Headers.Location);
  }

  [Theory]
  [InlineData("/healthz")]
  [InlineData("/readyz")]
  public async Task CanonicalHostMiddleware_AllowsLocalOperationalProbes(string path)
  {
    var nextCalled = false;
    var middleware = new ConfiguredCanonicalHostMiddleware(
      _ => { nextCalled = true; return Task.CompletedTask; },
      Definition(),
      Environment(Environments.Production));

    await middleware.InvokeAsync(Context("127.0.0.1", path));

    Assert.True(nextCalled);
  }

  [Fact]
  public async Task BindingGate_FailsClosedWithoutCallingTheWebsite()
  {
    var nextCalled = false;
    var middleware = new PublicWebsiteBindingGateMiddleware(
      _ => { nextCalled = true; return Task.CompletedTask; },
      NullLogger<PublicWebsiteBindingGateMiddleware>.Instance);
    var website = new StubWebsiteContext(
      new PublicSiteResolutionException(PublicSiteResolutionFailure.NotFound, "missing"));
    var context = Context("bonhomiasuites.com", "/");

    await middleware.InvokeAsync(context, website);

    Assert.False(nextCalled);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    Assert.Equal("no-store", context.Response.Headers.CacheControl);
  }

  [Fact]
  public async Task BindingGate_BypassesOnlyLivenessAndStillValidatesReadiness()
  {
    var middleware = new PublicWebsiteBindingGateMiddleware(
      _ => Task.CompletedTask,
      NullLogger<PublicWebsiteBindingGateMiddleware>.Instance);
    var website = new StubWebsiteContext();

    await middleware.InvokeAsync(Context("127.0.0.1", "/healthz"), website);
    Assert.Equal(0, website.ResolveCalls);

    await middleware.InvokeAsync(Context("127.0.0.1", "/readyz"), website);
    Assert.Equal(1, website.ResolveCalls);
  }

  [Fact]
  public async Task InstanceContext_UsesMonotonicTimeToBoundBindingCache()
  {
    var resolver = new CountingResolver();
    var services = new ServiceCollection();
    services.AddScoped<IPublicSiteResolver>(_ => resolver);
    using var provider = services.BuildServiceProvider();
    var time = new AdjustableTimeProvider();
    var context = new PublicWebsiteInstanceContext(
      Definition(),
      Presentation(),
      provider.GetRequiredService<IServiceScopeFactory>(),
      time,
      NullLogger<PublicWebsiteInstanceContext>.Instance);

    await context.ResolveRequiredAsync();
    time.MoveWallClockBackwardAndAdvanceTimestamp(TimeSpan.FromSeconds(31));
    await context.ResolveRequiredAsync();

    Assert.Equal(2, resolver.ResolveCalls);
  }

  [Fact]
  public async Task InstanceContext_DoesNotCacheFallbackPastItsUtcExpiry()
  {
    var time = new AdjustableTimeProvider();
    var resolver = new FixedBindingResolver(new PublicSiteBinding(
      1,
      "bonhomia-main",
      2,
      "OHM191112Q26",
      "OHM191112Q26",
      null,
      3,
      "bonhomia-suites",
      "Bonhomía Suites",
      "Central Standard Time (Mexico)",
      PlatformModuleCodes.Hospitality,
      1,
      "bonhomiasuites.com",
      2,
      2,
      2,
      1,
      1,
      time.UtcNow.AddSeconds(5).UtcDateTime));
    var services = new ServiceCollection();
    services.AddScoped<IPublicSiteResolver>(_ => resolver);
    using var provider = services.BuildServiceProvider();
    var context = new PublicWebsiteInstanceContext(
      Definition(),
      Presentation(),
      provider.GetRequiredService<IServiceScopeFactory>(),
      time,
      NullLogger<PublicWebsiteInstanceContext>.Instance);

    await context.ResolveRequiredAsync();
    time.Advance(TimeSpan.FromSeconds(5));

    await Assert.ThrowsAsync<PublicWebsitePresentationException>(() => context.ResolveRequiredAsync());
    Assert.Equal(2, resolver.ResolveCalls);
  }

  private static PublicWebsiteInstanceOptions Options(
    string publicSiteKey = "bonhomia-main",
    string companyRfc = "OHM191112Q26",
    string siteKey = "bonhomia-suites",
    string moduleCode = PlatformModuleCodes.Hospitality,
    string canonicalHost = "bonhomiasuites.com")
    => new()
    {
      PublicSiteKey = publicSiteKey,
      ExpectedCompanyRfc = companyRfc,
      SiteKey = siteKey,
      ModuleCode = moduleCode,
      CanonicalHost = canonicalHost,
      LoopbackPort = 5010
    };

  private static void AssertDeploymentIdentity(
    JsonElement root,
    string publicSiteKey,
    string companyRfc,
    string siteKey,
    string moduleCode,
    string canonicalHost,
    int loopbackPort)
  {
    var identity = root.GetProperty("PublicWebsite");
    var presentation = root.GetProperty("PublicWebsitePresentation");
    Assert.Equal(publicSiteKey, identity.GetProperty("PublicSiteKey").GetString());
    Assert.Equal(companyRfc, identity.GetProperty("ExpectedCompanyRfc").GetString());
    Assert.Equal(siteKey, identity.GetProperty("SiteKey").GetString());
    Assert.Equal(moduleCode, identity.GetProperty("ModuleCode").GetString());
    Assert.Equal(canonicalHost, identity.GetProperty("CanonicalHost").GetString());
    Assert.Equal(loopbackPort, identity.GetProperty("LoopbackPort").GetInt32());
    Assert.Equal(publicSiteKey, presentation.GetProperty("PublicSiteKey").GetString());
    Assert.True(presentation.GetProperty("BrandingVersion").GetInt64() > 0);
    Assert.True(presentation.GetProperty("ContentVersion").GetInt64() > 0);
    Assert.True(presentation.GetProperty("Assets").EnumerateObject().Count() >= 2);
  }

  private static void AssertNoSecretProperties(JsonElement element)
  {
    var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
      "ConnectionStrings",
      "Password",
      "ClientSecret",
      "SecretKey",
      "ApiKey",
      "TenantId",
      "ClientId",
      "PayPalClientId"
    };
    if (element.ValueKind == JsonValueKind.Object)
    {
      foreach (var property in element.EnumerateObject())
      {
        Assert.DoesNotContain(property.Name, forbidden);
        AssertNoSecretProperties(property.Value);
      }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
      foreach (var item in element.EnumerateArray())
        AssertNoSecretProperties(item);
    }
  }

  private static string RepositoryPath(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
      directory = directory.Parent;
    Assert.NotNull(directory);
    return Path.Combine(directory!.FullName, relativePath);
  }

  private static PublicWebsiteInstanceDefinition Definition()
    => new(
      "bonhomia-main",
      "OHM191112Q26",
      "bonhomia-suites",
      PlatformModuleCodes.Hospitality,
      "bonhomiasuites.com",
      5010);

  private static PublicWebsitePresentationDefinition Presentation()
    => PublicWebsitePresentationPolicy.Create(
      new PublicWebsitePresentationOptions
      {
        PublicSiteKey = "bonhomia-main",
        BrandingVersion = 1,
        ContentVersion = 1,
        PublicName = "Bonhomía Suites",
        ShortName = "Bonhomía",
        LegalName = "Orion Habitat de México, S.A. de C.V.",
        LocationName = "Calpulalpan, Tlaxcala",
        Tagline = "Tu estancia, a tu ritmo.",
        FooterSummary = "Hospedaje amueblado.",
        SeoDescription = "Hospedaje amueblado en Calpulalpan.",
        Locale = "es-MX",
        PublicEmail = "reservas@example.com",
        WhatsAppE164 = "+527491234567",
        WhatsAppDisplay = "+52 749 123 4567",
        OperatingAddress = "Dirección operativa",
        FiscalAddress = "Dirección fiscal",
        PrivacyEmail = "privacidad@example.com",
        PrivacyVersion = "2026-09-01",
        PrivacyUpdatedDisplay = "1 de septiembre de 2026",
        TermsVersion = "2026-09-01",
        TermsUpdatedDisplay = "1 de septiembre de 2026",
        PrimaryColor = "#123456",
        PrimaryDarkColor = "#0A1B2C",
        AccentColor = "#ABCDEF",
        Assets = new()
        {
          ["logo"] = "/instances/bonhomia/logo.svg",
          ["favicon"] = "/instances/bonhomia/favicon.png"
        }
      },
      Definition());

  private static DefaultHttpContext Context(string host, string path)
  {
    var context = new DefaultHttpContext();
    context.Request.Host = new HostString(host);
    context.Request.Path = path;
    context.Response.Body = new MemoryStream();
    return context;
  }

  private static IWebHostEnvironment Environment(string name)
    => new StubWebHostEnvironment { EnvironmentName = name };

  private sealed class StubWebsiteContext : IPublicWebsiteInstanceContext
  {
    private readonly Exception? _exception;

    public StubWebsiteContext(Exception? exception = null) => _exception = exception;

    public PublicWebsiteInstanceDefinition Instance => Definition();
    public string CurrentRfc => Instance.ExpectedCompanyRfc;
    public int ResolveCalls { get; private set; }

    public Task<PublicSiteBinding> ResolveRequiredAsync(CancellationToken ct = default)
    {
      ResolveCalls++;
      return _exception is null
        ? Task.FromResult(new PublicSiteBinding(
          1,
          Instance.PublicSiteKey,
          2,
          Instance.ExpectedCompanyRfc,
          Instance.ExpectedCompanyRfc,
          null,
          3,
          Instance.SiteKey,
          "Bonhomía Suites",
          "Central Standard Time (Mexico)",
          Instance.ModuleCode,
          1,
          Instance.CanonicalHost,
          1,
          1,
          1))
        : Task.FromException<PublicSiteBinding>(_exception);
    }
  }

  private sealed class StubWebHostEnvironment : IWebHostEnvironment
  {
    public string ApplicationName { get; set; } = "Tests";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ContentRootPath { get; set; } = string.Empty;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
  }

  private sealed class CountingResolver : IPublicSiteResolver
  {
    public int ResolveCalls { get; private set; }

    public Task<PublicSiteBinding> ResolveRequiredAsync(
      PublicSiteResolutionRequest request,
      CancellationToken ct = default)
    {
      ResolveCalls++;
      return Task.FromResult(new PublicSiteBinding(
        1,
        request.PublicSiteKey,
        2,
        request.ExpectedCompanyRfc,
        request.ExpectedCompanyRfc,
        null,
        3,
        request.ExpectedSiteKey,
        "Sede",
        "Central Standard Time (Mexico)",
        request.ExpectedModuleCode,
        1,
        request.ExpectedCanonicalHost,
        1,
        1,
        1));
    }
  }

  private sealed class FixedBindingResolver(PublicSiteBinding binding) : IPublicSiteResolver
  {
    public int ResolveCalls { get; private set; }

    public Task<PublicSiteBinding> ResolveRequiredAsync(
      PublicSiteResolutionRequest request,
      CancellationToken ct = default)
    {
      ResolveCalls++;
      return Task.FromResult(binding);
    }
  }

  private sealed class AdjustableTimeProvider : TimeProvider
  {
    private DateTimeOffset _utcNow = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long GetTimestamp() => _timestamp;
    public DateTimeOffset UtcNow => _utcNow;

    public void MoveWallClockBackwardAndAdvanceTimestamp(TimeSpan elapsed)
    {
      _utcNow = _utcNow.Subtract(TimeSpan.FromDays(1));
      _timestamp += elapsed.Ticks;
    }

    public void Advance(TimeSpan elapsed)
    {
      _utcNow = _utcNow.Add(elapsed);
      _timestamp += elapsed.Ticks;
    }
  }
}
