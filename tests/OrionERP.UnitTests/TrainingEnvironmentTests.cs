using OrionERP.Web.Features.TrainingSafety;

namespace OrionERP.UnitTests;

public sealed class TrainingEnvironmentTests
{
  private const string TrainingConnection =
    "Server=example.invalid,1433;Database=Orion_Training;User Id=orion;Password=not-used;Encrypt=True;";

  [Fact]
  public void ValidateStartup_AllowsTheNormalProductionSqlLoginAgainstTheClone()
  {
    var exception = Record.Exception(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      publicTrainingOrigin: "https://capacitacion.orion.land"));

    Assert.Null(exception);
  }

  [Fact]
  public void ValidateStartup_DoesNotRequireASpecialTrainingLogin()
  {
    var integratedConnection =
      "Server=example.invalid;Database=Orion_Training;Integrated Security=True;Encrypt=True;";

    var exception = Record.Exception(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      integratedConnection,
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
    var connection = $"Server=example.invalid;Database={catalog};User Id=orion;Password=not-used;Encrypt=True;";

    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      connection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));
  }

  [Theory]
  [InlineData("Server=example.invalid;Database=Orion_Training;User Id=orion;Password=x;Encrypt=False;")]
  [InlineData("Server=example.invalid;Database=Orion_Training;AttachDbFilename=C:\\temp\\training.mdf;Integrated Security=True;Encrypt=True;")]
  public void ValidateStartup_RejectsUnsafeConnectionShapes(string connection)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      connection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true));
  }

  [Theory]
  [InlineData("Production", true)]
  [InlineData("Development", true)]
  [InlineData("Training", false)]
  public void ValidateStartup_RejectsEnvironmentAndServiceMarkerMismatch(string environmentName, bool marker)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
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
    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      "Production",
      TrainingConnection,
      new PlatformIsolationOptions(),
      "http://localhost:5000",
      false));
  }

  [Fact]
  public void ValidateStartup_RequiresDedicatedKeysCookiesHostAndOrigin()
  {
    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      new PlatformIsolationOptions(),
      "http://localhost:5030",
      true));

    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "*"));

    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      "http://localhost:5030",
      true,
      "localhost;127.0.0.1;orionerp.orion.land",
      "https://orionerp.orion.land"));
  }

  [Theory]
  [InlineData(5000)]
  [InlineData(5010)]
  [InlineData(5020)]
  public void ValidateStartup_RejectsPortsOwnedByOtherServices(int port)
  {
    Assert.Throws<InvalidOperationException>(() => TrainingEnvironmentValidator.ValidateStartup(
      TrainingEnvironment.Name,
      TrainingConnection,
      CreateTrainingIsolation(),
      $"http://localhost:{port}",
      true));
  }

  [Fact]
  public void TrainingState_DescribesAProductionCloneWithoutSyntheticDataClaims()
  {
    var state = new TrainingEnvironmentState(
      true,
      TrainingEnvironment.Name,
      TrainingEnvironment.RequiredDatabaseCatalog);

    Assert.True(state.IsTraining);
    Assert.Contains("COPIA DE PRODUCCIÓN", state.BannerText, StringComparison.Ordinal);
    Assert.DoesNotContain("SIMULADOS", state.BannerText, StringComparison.Ordinal);
  }

  private static PlatformIsolationOptions CreateTrainingIsolation() => new()
  {
    DataProtectionApplicationName = "OrionERP",
    DataProtectionKeyPath = "App_Data/training-keys",
    AntiforgeryCookieName = ".OrionERP.Training.Antiforgery",
    IdentityCookieName = ".OrionERP.Training.Identity",
    KioskDeviceCookieName = "orion-training-kiosk-device"
  };
}
