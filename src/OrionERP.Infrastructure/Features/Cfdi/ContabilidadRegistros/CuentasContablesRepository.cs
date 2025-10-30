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

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel1Async(string rfc, string term, int take = 25)
  {
    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(term))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedTerm = term.Trim();
    var likeTerm = normalizedTerm;

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
  AND Nivel2 = '0'
  AND Nivel3 = '0'
  AND (Nivel1 = @exact OR Descripcion LIKE @like)
ORDER BY Nivel1;";

    using var connection = new SqlConnection(_connectionString);
    return await connection.QueryAsync<CuentasContablesDto>(
        sql,
        new
        {
          take,
          rfc = normalizedRfc,
          exact = normalizedTerm,
          like = $"%{likeTerm}%"
        });
  }

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel2Async(string rfc, string nivel1, string term, int take = 25)
  {
    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(nivel1) || string.IsNullOrWhiteSpace(term))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedNivel1 = nivel1.Trim();
    var normalizedTerm = NormalizeTwoDigits(term);
    var likeTerm = term.Trim();

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
  AND Nivel3 = '0'
  AND (Nivel2 = @exact OR Descripcion LIKE @like)
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
          like = $"%{likeTerm}%"
        });
  }

  public async Task<IEnumerable<CuentasContablesDto>> SearchNivel3Async(string rfc, string nivel1, string nivel2, string term, int take = 25)
  {
    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(nivel1) ||
        string.IsNullOrWhiteSpace(nivel2) || string.IsNullOrWhiteSpace(term))
    {
      return Array.Empty<CuentasContablesDto>();
    }

    var normalizedRfc = rfc.Trim();
    var normalizedNivel1 = nivel1.Trim();
    var normalizedNivel2 = NormalizeTwoDigits(nivel2);
    var normalizedTerm = NormalizeTwoDigits(term);
    var likeTerm = term.Trim();

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
  AND (Nivel3 = @exact OR Descripcion LIKE @like)
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
          like = $"%{likeTerm}%"
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
}
