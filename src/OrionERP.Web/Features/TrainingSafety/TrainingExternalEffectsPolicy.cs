namespace OrionERP.Web.Features.TrainingSafety;

public interface ITrainingExternalEffectsPolicy
{
  bool IsBlocked { get; }
  void EnsureAllowed(string effect);
  TrainingExternalEffectBlockedException CreateException(string effect);
}

public sealed class TrainingExternalEffectsPolicy : ITrainingExternalEffectsPolicy
{
  private readonly ITrainingEnvironmentState _state;

  public TrainingExternalEffectsPolicy(ITrainingEnvironmentState state) => _state = state;

  public bool IsBlocked => _state.ExternalEffectsBlocked;

  public void EnsureAllowed(string effect)
  {
    if (IsBlocked)
      throw CreateException(effect);
  }

  public TrainingExternalEffectBlockedException CreateException(string effect)
    => new(BlockedMessage(effect));

  public static string BlockedMessage(string effect)
    => $"El entorno de capacitación bloqueó {effect}. Usa únicamente el escenario y los datos simulados de Orion_Training.";
}

public sealed class TrainingExternalEffectBlockedException : InvalidOperationException
{
  public TrainingExternalEffectBlockedException(string message) : base(message) { }
}
