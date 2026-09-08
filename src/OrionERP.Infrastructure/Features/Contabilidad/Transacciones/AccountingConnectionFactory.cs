using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Common;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones;

/// <summary>
/// Toda operación contable recibe una conexión con la empresa fijada, siguiendo el
/// patrón de <c>SqlConnectionFactory</c> y <c>HospitalityConnectionFactory</c>. No
/// se confía en un contexto ambiente: cada conexión declara su RFC legacy y su
/// <c>CompanyId</c> técnico, y la base comprueba que el par exista y sea el mismo.
/// </summary>
public sealed class AccountingConnectionFactory
{
  public const string RfcSessionKey = "OrionRfc";
  public const string CompanyIdSessionKey = "OrionERP.CompanyId";

  private const string InitializeSql = """
    EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@Rfc, @read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.CompanyId', @value=@CompanyId, @read_only=0;
    IF NOT EXISTS
    (
      SELECT 1 FROM orion.Company
      WHERE CompanyId = @CompanyId AND Rfc = @Rfc AND IsActive = 1
    )
      THROW 51950, 'La empresa contable de la sesion no coincide con orion.Company.', 1;
    """;

  private readonly string _connectionString;
  private readonly ICurrentCompanyContext _company;

  public AccountingConnectionFactory(IConfiguration configuration, ICurrentCompanyContext company)
  {
    ArgumentNullException.ThrowIfNull(configuration);
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
    _company = company ?? throw new ArgumentNullException(nameof(company));
  }

  public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
  {
    var rfc = _company.RequireRfc();
    var companyId = await _company.RequireCompanyIdAsync(ct);
    var connection = new SqlConnection(_connectionString);
    try
    {
      await connection.OpenAsync(ct);
      await connection.ExecuteAsync(new CommandDefinition(
        InitializeSql, new { Rfc = rfc, CompanyId = companyId }, cancellationToken: ct));
      return connection;
    }
    catch { await connection.DisposeAsync(); throw; }
  }

  /// <summary>Fija el mismo contexto sobre una conexión ya abierta por el llamador.</summary>
  public static Task InitializeAsync(
    SqlConnection connection,
    string rfc,
    long companyId,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(connection);
    if (companyId <= 0 || string.IsNullOrWhiteSpace(rfc))
      throw new UnauthorizedAccessException("El alcance contable no es válido.");
    if (connection.State != ConnectionState.Open)
      throw new InvalidOperationException("La conexión contable debe estar abierta.");
    return connection.ExecuteAsync(new CommandDefinition(
      InitializeSql, new { Rfc = rfc, CompanyId = companyId }, cancellationToken: ct));
  }
}
