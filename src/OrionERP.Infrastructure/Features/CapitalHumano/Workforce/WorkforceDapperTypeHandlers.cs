using System.Data;
using Dapper;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public static class WorkforceDapperTypeHandlers
{
  private static int _registered;

  public static void Register()
  {
    if (Interlocked.Exchange(ref _registered, 1) == 1) return;
    SqlMapper.AddTypeHandler(new DateOnlyHandler());
  }

  private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
  {
    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
      parameter.DbType = DbType.Date;
      parameter.Value = value.ToDateTime(TimeOnly.MinValue);
    }

    public override DateOnly Parse(object value)
      => value switch
      {
        DateOnly dateOnly => dateOnly,
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        string text when DateOnly.TryParse(text, out var parsed) => parsed,
        _ => throw new DataException($"Cannot convert {value.GetType().Name} to DateOnly.")
      };
  }
}
