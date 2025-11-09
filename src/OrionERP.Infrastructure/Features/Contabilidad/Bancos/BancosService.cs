using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Bancos;

namespace OrionERP.Infrastructure.Features.Contabilidad.Bancos;

public sealed class BancosService : IBancosService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public BancosService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(CancellationToken cancellationToken = default)
  {
    const string sql = @"
SELECT 
    Cuenta_Banco_ID AS CuentaBancoId,
    Nombre_Banco AS NombreBanco,
    Numero_Cuenta AS NumeroCuenta,
    Tipo_Cuenta AS TipoCuenta,
    Nombre_Titular AS NombreTitular,
    CLABE_Cuenta AS ClabeCuenta,
    RFC,
    Activo,
    Fecha_Alta AS FechaAlta
FROM bancos.Cuentas_Banco
ORDER BY Fecha_Alta DESC;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<BankAccountDto>(sql).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<IReadOnlyList<int>> GetAvailableYearsAsync(string rfc, CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<int>();
    }

    const string sql = @"
SELECT DISTINCT YEAR(Dia) AS Year
FROM bancos.Movimientos
WHERE RFC = @Rfc
ORDER BY Year DESC;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<int>(sql, new { Rfc = rfc }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<IReadOnlyList<BankMovementDto>> GetMovementsAsync(
      string rfc,
      int? accountId,
      int year,
      int month,
      string? textFilter,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<BankMovementDto>();
    }

    const string sql = @"
SELECT 
    M.Movimiento_ID AS MovimientoId,
    M.Dia,
    M.Secuencia_Diaria AS Line,
    M.Concepto,
    M.Tipo,
    M.Cargo,
    M.Abono,
    M.Saldo,
    M.Fecha_Carga AS FechaCarga,
    M.Nombre_Banco AS NombreBanco,
    M.Numero_Cuenta AS NumeroCuenta,
    M.Secuencia_Clave AS SecuenciaClave,
    M.Transaccion_ID AS Policy
FROM bancos.Movimientos AS M
LEFT JOIN dbo.Transacciones AS T ON M.Transaccion_ID = T.ID
WHERE M.RFC = @Rfc
  AND (@AccountId IS NULL OR M.Cuenta_Banco_ID = @AccountId)
  AND YEAR(M.Dia) = @Year
  AND MONTH(M.Dia) = @Month
  AND (@TextFilter IS NULL OR M.Concepto LIKE '%' + @TextFilter + '%')
ORDER BY M.Secuencia_Clave;";

    var parameters = new
    {
      Rfc = rfc,
      AccountId = accountId,
      Year = year,
      Month = month,
      TextFilter = string.IsNullOrWhiteSpace(textFilter) ? null : textFilter
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<BankMovementDto>(sql, parameters).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<ProcessBbvaResult?> ProcessBbvaFileAsync(
      string fileContents,
      int accountId,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(fileContents))
    {
      throw new ArgumentException("File contents cannot be empty.", nameof(fileContents));
    }

    if (accountId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    const string storedProcedure = "bancos.Procesar_Movimientos_BBVA";

    var parameters = new DynamicParameters();
    parameters.Add("@ArchivoTexto", fileContents, DbType.String);
    parameters.Add("@Cuenta_Banco_ID", accountId, DbType.Int32);

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var record = await connection.QuerySingleOrDefaultAsync<IDictionary<string, object?>>( 
        storedProcedure,
        parameters,
        commandType: CommandType.StoredProcedure).ConfigureAwait(false);

    cancellationToken.ThrowIfCancellationRequested();

    if (record is null)
    {
      return null;
    }

    return new ProcessBbvaResult
    {
      Insertados = Convert.ToInt32(record["Insertados"] ?? 0),
      Actualizados = Convert.ToInt32(record["Actualizados"] ?? 0),
      CuentaBancoId = Convert.ToInt32(record["Cuenta_Banco_ID"] ?? 0),
      NombreBanco = Convert.ToString(record["Nombre_Banco"] ?? string.Empty) ?? string.Empty,
      NumeroCuenta = Convert.ToString(record["Numero_Cuenta"] ?? string.Empty) ?? string.Empty,
      ArchivoHash = Convert.ToString(record["ArchivoHash"] ?? string.Empty) ?? string.Empty,
      BalanceWarnings = Convert.ToInt32(record["Balance_Warnings"] ?? 0)
    };
  }

  private async Task<IDbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
  {
    var connection = _connectionFactory.Create();

    if (connection is DbConnection dbConnection)
    {
      await dbConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
      return dbConnection;
    }

    connection.Open();
    return connection;
  }
}
