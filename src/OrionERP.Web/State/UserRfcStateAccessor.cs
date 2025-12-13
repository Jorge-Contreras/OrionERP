using OrionERP.Application.Common;
using OrionERP.Infrastructure.Auth;

namespace OrionERP.Web.State;

public sealed class UserRfcStateAccessor : ICurrentRfcAccessor
{
  private readonly IRfcContext _context;

  public UserRfcStateAccessor(IRfcContext context) => _context = context;

  public string? CurrentRfc => _context.CurrentRfc;
}
