using OrionERP.Web.Features.TrainingSafety;

namespace OrionERP.UnitTests;

public sealed class TrainingSafetyTests
{
  private const string TrainingConnection =
    "Server=example.invalid,1433;Database=Orion_Training;User Id=orion_training_runtime;Password=not-used;Encrypt=True;";

  [Theory]
  [InlineData("Server=example.invalid;Database=Orion_Training;Integrated Security=True;Encrypt=True;")]
  [InlineData("Server=example.invalid;Database=Orion_Training;User Id=training;Password=not-used;Encrypt=True;")]
  [InlineData("Server=example.invalid;Database=Orion_Training;User Id=orion_training_runtime;Password=not-used;Encrypt=False;")]
  public void ValidateStartup_RejectsWrongRuntimeAuthenticationBoundary(string connection)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      connection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));
  }

  [Fact]
  public void ValidateStartup_AllowsOnlyIsolatedTrainingConfiguration()
  {
    var exception = Record.Exception(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));

    Assert.Null(exception);
  }

  [Theory]
  [InlineData("grupocarpio")]
  [InlineData("Orion_Sandbox")]
  [InlineData("Orion_Training_Copy")]
  [InlineData("")]
  public void ValidateStartup_RejectsEveryCatalogExceptOrionTraining(string catalog)
  {
    var connection = $"Server=example.invalid;Database={catalog};Integrated Security=True;";

    var exception = Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      connection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));

    Assert.Contains("Training startup blocked", exception.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ValidateStartup_RejectsMissingOrMalformedConnectionString()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      null,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));

    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      "this is not a connection string",
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));
  }

  [Fact]
  public void ValidateStartup_RejectsProductionKeysCookiesAndPort()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      new PlatformIsolationOptions(),
      "http://localhost:5030",
      true));

    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5000",
      true));

    var aliasedProductionKeyPath = new PlatformIsolationOptions
    {
      DataProtectionApplicationName = "OrionERP.Training",
      DataProtectionKeyPath = ".\\App_Data\\keys",
      AntiforgeryCookieName = ".OrionERP.Training.Antiforgery",
      IdentityCookieName = ".OrionERP.Training.Identity"
    };
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      aliasedProductionKeyPath,
      "http://localhost:5030",
      true));
  }

  [Fact]
  public async Task TrainingHttpHandler_BlocksBeforeUsingNetwork()
  {
    var state = new TrainingEnvironmentState(true, TrainingEnvironment.Name, TrainingEnvironment.RequiredDatabaseCatalog);
    var policy = new TrainingExternalEffectsPolicy(state);
    using var handler = new TrainingBlockedHttpMessageHandler(policy)
    {
      InnerHandler = new ThrowIfReachedHandler()
    };
    using var client = new HttpClient(handler);

    var exception = await Assert.ThrowsAsync<TrainingExternalEffectBlockedException>(
      () => client.GetAsync("https://api.facturama.mx/test"));

    Assert.Contains("entorno de capacitación", exception.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void NonTrainingValidation_DoesNotChangeProductionBehavior()
  {
    var exception = Record.Exception(() => TrainingSafetyValidator.ValidateStartup(
      "Production",
      "Server=example.invalid;Database=grupocarpio;Integrated Security=True;",
      new PlatformIsolationOptions(),
      "http://localhost:5000",
      false));

    Assert.Null(exception);
  }

  [Theory]
  [InlineData("Production", true)]
  [InlineData("Development", true)]
  [InlineData("Training", false)]
  public void ValidateStartup_RejectsEnvironmentAndServiceMarkerMismatch(
    string environmentName,
    bool marker)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      environmentName,
      environmentName == "Training"
        ? TrainingConnection
        : "Server=example.invalid;Database=grupocarpio;Integrated Security=True;",
      environmentName == "Training" ? CreateTrainingIsolation() : new PlatformIsolationOptions(),
      environmentName == "Training" ? "http://localhost:5030" : "http://localhost:5000",
      marker));
  }

  [Fact]
  public void ValidateStartup_RejectsTrainingCatalogOutsideTrainingEnvironment()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      "Production",
      TrainingConnection,
      new PlatformIsolationOptions(),
      "http://localhost:5000",
      false));
  }

  [Fact]
  public void ValidateStartup_RejectsWildcardAllowedHosts()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "*"));
  }

  [Fact]
  public void ValidateStartup_RequiresReadinessHostAndRejectsProductionPublicOrigin()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "localhost"));

    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "localhost;127.0.0.1;orionerp.orion.land",
      "https://orionerp.orion.land"));

    var exception = Record.Exception(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "localhost;127.0.0.1;training.orion.land",
      "https://training.orion.land"));
    Assert.Null(exception);
  }

  [Fact]
  public void TrainingState_RequiresDatabaseSafetyAttestation()
  {
    var unsafeState = new TrainingEnvironmentState(
      true,
      TrainingEnvironment.Name,
      TrainingEnvironment.RequiredDatabaseCatalog);
    Assert.False(unsafeState.DatabaseSafetyVerified);

    var safeState = new TrainingEnvironmentState(
      true,
      TrainingEnvironment.Name,
      TrainingEnvironment.RequiredDatabaseCatalog,
      new TrainingDatabaseSafetyAttestation(true, 1, true, true, true));
    Assert.True(safeState.DatabaseSafetyVerified);
    Assert.True(safeState.RuntimeLoginIsolated);
  }

  [Theory]
  [InlineData(5000)]
  [InlineData(5010)]
  [InlineData(5020)]
  public void ValidateStartup_RejectsPortsOwnedByProductionServices(int port)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingSafetyValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      $"http://localhost:{port}",
      true));
  }

  private static PlatformIsolationOptions CreateTrainingIsolation() => new()
  {
    DataProtectionApplicationName = "OrionERP.Training",
    DataProtectionKeyPath = "App_Data/training-keys",
    AntiforgeryCookieName = ".OrionERP.Training.Antiforgery",
    IdentityCookieName = ".OrionERP.Training.Identity",
    KioskDeviceCookieName = "orion-training-kiosk-device"
  };

  private sealed class ThrowIfReachedHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(
      HttpRequestMessage request,
      CancellationToken cancellationToken)
      => throw new Xunit.Sdk.XunitException("The network handler must never be reached in Training.");
  }
}
