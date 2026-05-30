using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Common;

public interface ICurrentUserAccessor
{
  ValueTask<string?> GetUserNameAsync(CancellationToken cancellationToken = default);
}
