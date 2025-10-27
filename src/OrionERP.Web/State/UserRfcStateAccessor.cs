using OrionERP.Application.Common;

namespace OrionERP.Web.State;

public sealed class UserRfcStateAccessor : ICurrentRfcAccessor
{
  private readonly IUserRfcState _state;

  public UserRfcStateAccessor(IUserRfcState state) => _state = state;

  public string? CurrentRfc => _state.CurrentRfc;
}
