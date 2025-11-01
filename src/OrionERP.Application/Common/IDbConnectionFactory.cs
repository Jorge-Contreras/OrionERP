using System.Data;

namespace OrionERP.Application.Common;

public interface IDbConnectionFactory
{
  IDbConnection Create();
}

