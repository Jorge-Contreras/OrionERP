using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Common;

public interface IDbStoredProcService
{
  Task<int> ExecuteAsync(
      string storedProcedure,
      IReadOnlyDictionary<string, object?> parameters,
      CancellationToken cancellationToken = default);
}

