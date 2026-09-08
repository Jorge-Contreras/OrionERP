using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>
/// Abre su propia conexión en vez de tomar <see cref="IDbConnectionFactory"/>:
/// esa fábrica depende del RFC de la sesión, y el contexto de empresa depende de
/// esta resolución. <c>orion.Company</c> no está bajo RLS, así que la consulta no
/// necesita contexto para evaluarse.
/// </summary>
public sealed class CompanyIdentityResolver : ICompanyIdentityResolver
{
  private readonly string _connectionString;

  public CompanyIdentityResolver(IConfiguration configuration)
  {
    ArgumentNullException.ThrowIfNull(configuration);
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
  }

  public async Task<long?> ResolveCompanyIdAsync(string legacyRfc, CancellationToken ct = default)
  {
    var normalized = string.IsNullOrWhiteSpace(legacyRfc) ? string.Empty : legacyRfc.Trim().ToUpperInvariant();
    if (normalized.Length == 0) return null;
    await using var connection = new SqlConnection(_connectionString);
    // Rfc es la clave primaria de orion.Company, así que la coincidencia exacta no
    // puede ser ambigua. TaxRfc queda deliberadamente fuera de la consulta.
    return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
      "SELECT CompanyId FROM orion.Company WHERE Rfc = @Rfc AND IsActive = 1;",
      new { Rfc = normalized }, cancellationToken: ct));
  }
}
