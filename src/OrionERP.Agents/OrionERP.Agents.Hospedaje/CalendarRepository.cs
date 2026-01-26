using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace OrionERP.Agents.Hospedaje;

public sealed class CalendarRepository
{
  private readonly string _connectionString;

  public CalendarRepository(string connectionString)
  {
    _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
  }

  public async Task<IReadOnlyList<IDictionary<string, object?>>> GetFullCalendarAsync(DateOnly startDate, DateOnly endDate)
  {
    if (endDate < startDate)
      throw new ArgumentException("endDate must be >= startDate.");

    await using var cn = new SqlConnection(_connectionString);

    // Your SP:
    // dbo.GET_FULL_CALENDAR @StartDate, @EndDate
    var rows = await cn.QueryAsync(
        sql: "dbo.GET_FULL_CALENDAR",
        param: new
        {
          StartDate = startDate.ToDateTime(TimeOnly.MinValue),
          EndDate = endDate.ToDateTime(TimeOnly.MinValue)
        },
        commandType: CommandType.StoredProcedure
    );

    // Dapper returns dynamic rows; convert to dictionaries for flexible column access
    var list = new List<IDictionary<string, object?>>();
    foreach (var row in rows)
    {
      if (row is IDictionary<string, object?> dict)
        list.Add(dict);
      else
        list.Add(new Dictionary<string, object?>());
    }

    return list;
  }
}
