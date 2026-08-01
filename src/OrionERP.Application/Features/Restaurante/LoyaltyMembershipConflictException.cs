namespace OrionERP.Application.Features.Restaurante;

public sealed class LoyaltyMembershipConflictException : Exception
{
  public LoyaltyMembershipConflictException(string message)
    : base(message)
  {
  }

  public LoyaltyMembershipConflictException(string message, Exception innerException)
    : base(message, innerException)
  {
  }
}
