namespace OrionERP.Web.Features.TrainingSafety;

public interface ITrainingEnvironmentState
{
  bool IsTraining { get; }
  string EnvironmentName { get; }
  string DatabaseCatalog { get; }
  string BannerText { get; }
}

public sealed record TrainingEnvironmentState(
  bool IsTraining,
  string EnvironmentName,
  string DatabaseCatalog) : ITrainingEnvironmentState
{
  public string BannerText => IsTraining
    ? "ENTORNO DE PRÁCTICA · COPIA DE PRODUCCIÓN · LOS CAMBIOS NO AFECTAN EL SISTEMA EN VIVO"
    : string.Empty;
}
