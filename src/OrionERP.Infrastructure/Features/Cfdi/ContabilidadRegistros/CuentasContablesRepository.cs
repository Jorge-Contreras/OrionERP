using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

namespace OrionERP.Infrastructure.Features.Cfdi.ContabilidadRegistros;

public sealed class CuentasContablesRepository : ICuentasContablesRepository
{
  private readonly string _connectionString;

  public CuentasContablesRepository(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
        ?? throw new InvalidOperationException("Missing connection string 'OrionDb'.");
  }

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel1Async(string rfc, string term, int take = 200)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedTerm = term?.Trim() ?? string.Empty;
    var hasTerm = normalizedTerm.Length > 0;

    const string sql = @"
SELECT TOP (@take)
       id       AS Id,
       RazonSocial,
       Nivel1,
       Nivel2,
       Nivel3,
       Descripcion
FROM dbo.CuentasContables
WHERE RazonSocial = @rfc
  AND Nivel2 = '00'
  AND Nivel3 = '00'
  AND (@hasTerm = 0 OR Nivel1 = @exact OR Descripcion LIKE @like)
ORDER BY Nivel1;";

    using var connection = new SqlConnection(_connectionString);
    return await connection.QueryAsync<CuentasContablesDto>(
        sql,
        new
        {
          take,
          rfc = normalizedRfc,
          exact = normalizedTerm,
          like = hasTerm ? $"%{normalizedTerm}%" : "%",
          hasTerm
        });
  }

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel2Async(string rfc, string nivel1, string term, int take = 25)
  {
    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(nivel1))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedNivel1 = nivel1.Trim();
    var inputTerm = term ?? string.Empty;
    var trimmedTerm = inputTerm.Trim();
    var hasTerm = trimmedTerm.Length > 0;
    var normalizedTerm = NormalizeTwoDigits(trimmedTerm);

    const string sql = @"
SELECT TOP (@take)
       id       AS Id,
       RazonSocial,
       Nivel1,
       Nivel2,
       Nivel3,
       Descripcion
FROM dbo.CuentasContables
WHERE RazonSocial = @rfc
  AND Nivel1 = @nivel1
  AND Nivel3 = '00'
  AND (@hasTerm = 0 OR Nivel2 = @exact OR Descripcion LIKE @like)
ORDER BY Nivel2;";

    using var connection = new SqlConnection(_connectionString);
    return await connection.QueryAsync<CuentasContablesDto>(
        sql,
        new
        {
          take,
          rfc = normalizedRfc,
          nivel1 = normalizedNivel1,
          exact = normalizedTerm,
          like = hasTerm ? $"%{trimmedTerm}%" : "%",
          hasTerm
        });
  }

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel3Async(string rfc, string nivel1, string nivel2, string term, int take = 25)
  {
    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(nivel1) ||
        string.IsNullOrWhiteSpace(nivel2))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedNivel1 = nivel1.Trim();
    var normalizedNivel2 = NormalizeTwoDigits(nivel2);
    var inputTerm = term ?? string.Empty;
    var trimmedTerm = inputTerm.Trim();
    var hasTerm = trimmedTerm.Length > 0;
    var normalizedTerm = NormalizeTwoDigits(trimmedTerm);

    const string sql = @"
SELECT TOP (@take)
       id       AS Id,
       RazonSocial,
       Nivel1,
       Nivel2,
       Nivel3,
       Descripcion
FROM dbo.CuentasContables
WHERE RazonSocial = @rfc
  AND Nivel1 = @nivel1
  AND Nivel2 = @nivel2
  AND (@hasTerm = 0 OR Nivel3 = @exact OR Descripcion LIKE @like)
ORDER BY Nivel3;";

    using var connection = new SqlConnection(_connectionString);
    return await connection.QueryAsync<CuentasContablesDto>(
        sql,
        new
        {
          take,
          rfc = normalizedRfc,
          nivel1 = normalizedNivel1,
          nivel2 = normalizedNivel2,
          exact = normalizedTerm,
          like = hasTerm ? $"%{trimmedTerm}%" : "%",
          hasTerm
        });
  }

  public async Task<CuentasContablesDto?> GetByIdAsync(int id)
  {
    const string sql = @"
SELECT id         AS Id,
       RazonSocial,
       Nivel1,
       Nivel2,
       Nivel3,
       Descripcion
FROM dbo.CuentasContables
WHERE id = @id;";

    using var connection = new SqlConnection(_connectionString);
    return await connection.QuerySingleOrDefaultAsync<CuentasContablesDto>(sql, new { id });
  }

  private static string NormalizeTwoDigits(string value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return string.Empty;
    }

    var trimmed = value.Trim();
    if (trimmed.Length == 1 && char.IsDigit(trimmed[0]))
    {
      return trimmed.PadLeft(2, '0');
    }

    return trimmed;
  }

  public async Task<int> CreateNivel3Async(string rfc, string nivel1, string nivel2, string nivel3, string descripcion)
  {
      const string checkSql = @"
SELECT COUNT(*)
FROM dbo.CuentasContables
WHERE RazonSocial = @rfc
  AND Nivel1 = @nivel1
  AND Nivel2 = @nivel2
  AND Nivel3 = @nivel3;";

      const string insertSql = @"
INSERT INTO dbo.CuentasContables (RazonSocial, Nivel1, Nivel2, Nivel3, Descripcion)
VALUES (@rfc, @nivel1, @nivel2, @nivel3, @descripcion);
SELECT CAST(SCOPE_IDENTITY() as int);";

      using var connection = new SqlConnection(_connectionString);
      var existingCount = await connection.ExecuteScalarAsync<int>(
          checkSql,
          new
          {
              rfc,
              nivel1,
              nivel2,
              nivel3
          });

      if (existingCount > 0)
      {
          return -1; // Indicates a duplicate
      }

      return await connection.ExecuteScalarAsync<int>(
          insertSql,
          new
          {
              rfc,
              nivel1,
              nivel2,
              nivel3,
              descripcion
          });
  }

  public async Task UpdateNivel3DescripcionAsync(int id, string descripcion)
  {
      const string sql = @"
UPDATE dbo.CuentasContables
SET Descripcion = @descripcion
WHERE id = @id;";

      using var connection = new SqlConnection(_connectionString);
      await connection.ExecuteAsync(sql, new { id, descripcion });
  }
}
