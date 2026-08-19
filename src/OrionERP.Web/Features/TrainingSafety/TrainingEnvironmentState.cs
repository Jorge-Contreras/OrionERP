namespace OrionERP.Web.Features.TrainingSafety;

public interface ITrainingEnvironmentState
{
  bool IsTraining { get; }
  bool ExternalEffectsBlocked { get; }
  string EnvironmentName { get; }
  string DatabaseCatalog { get; }
  bool DatabaseSafetyVerified { get; }
  int DatabaseSchemaVersion { get; }
  bool DataSanitized { get; }
  bool SyntheticDataOnly { get; }
  bool RuntimeLoginIsolated { get; }
  string BannerText { get; }
}

public sealed record TrainingEnvironmentState(
  bool IsTraining,
  string EnvironmentName,
  string DatabaseCatalog,
  TrainingDatabaseSafetyAttestation? DatabaseSafety = null) : ITrainingEnvironmentState
{
  public bool ExternalEffectsBlocked => IsTraining;
  public bool DatabaseSafetyVerified => !IsTraining || DatabaseSafety?.Verified == true;
  public int DatabaseSchemaVersion => DatabaseSafety?.SchemaVersion ?? 0;
  public bool DataSanitized => DatabaseSafety?.DataSanitized == true;
  public bool SyntheticDataOnly => DatabaseSafety?.SyntheticDataOnly == true;
  public bool RuntimeLoginIsolated => DatabaseSafety?.RuntimeLoginIsolated == true;
  public string BannerText => IsTraining
    ? "ENTORNO DE PRÁCTICA · DATOS SIMULADOS · ACCIONES EXTERNAS BLOQUEADAS"
    : string.Empty;
}
