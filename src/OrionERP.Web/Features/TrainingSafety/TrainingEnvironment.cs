namespace OrionERP.Web.Features.TrainingSafety;

public static class TrainingEnvironment
{
  public const string Name = "Training";
  public const string ConfigurationPrefix = "ORION_TRAINING_";
  public const string ServiceMarkerVariable = "ORION_TRAINING_SERVICE";
  public const string RequiredDatabaseCatalog = "Orion_Training";
  public const string RequiredRuntimeLogin = "orion_training_runtime";

  public static bool IsReservedProductionPort(int port) => port is 5000 or 5010 or 5020;
}
