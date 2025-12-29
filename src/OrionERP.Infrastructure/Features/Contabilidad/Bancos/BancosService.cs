using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Bancos;

namespace OrionERP.Infrastructure.Features.Contabilidad.Bancos;

public sealed class BancosService : IBancosService
{
  private readonly IDbConnectionFactory _connectionFactory;

  private const string AccountSelectSql = @"
SELECT
    Cuenta_Banco_ID AS CuentaBancoId,
    Nombre_Banco AS NombreBanco,
    Numero_Cuenta AS NumeroCuenta,
    Tipo_Cuenta AS TipoCuenta,
    Nombre_Titular AS NombreTitular,
    CLABE_Cuenta AS ClabeCuenta,
    RFC,
    Activo,
    Fecha_Alta AS FechaAlta,
    Cuenta_Contable_ID AS CuentaContableId,
    Cuenta_Contable_Egreso AS CuentaContableEgreso,
    Cuenta_Contable_Ingreso AS CuentaContableIngreso
FROM bancos.Cuentas_Banco
";

  public BancosService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<BankAccountDto>> GetAccountsAsync(
      string rfc,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<BankAccountDto>();
    }

    var sql = AccountSelectSql + "WHERE RFC = @Rfc\nORDER BY Fecha_Alta DESC;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<BankAccountDto>(sql, new { Rfc = rfc }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<BankAccountDto> CreateAccountAsync(
      BankAccountRequest request,
      CancellationToken cancellationToken = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (string.IsNullOrWhiteSpace(request.Rfc))
    {
      throw new ArgumentException("RFC is required.", nameof(request));
    }

    const string insertSql = @"
INSERT INTO bancos.Cuentas_Banco (
    Nombre_Banco,
    Numero_Cuenta,
    Tipo_Cuenta,
    Nombre_Titular,
    CLABE_Cuenta,
    RFC,
    Activo,
    Cuenta_Contable_ID,
    Cuenta_Contable_Egreso,
    Cuenta_Contable_Ingreso)
OUTPUT INSERTED.Cuenta_Banco_ID
VALUES (
    @NombreBanco,
    @NumeroCuenta,
    @TipoCuenta,
    @NombreTitular,
    @ClabeCuenta,
    @Rfc,
    @Activo,
    @CuentaContableId,
    @CuentaContableEgreso,
    @CuentaContableIngreso);
";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var parameters = new
    {
      request.NombreBanco,
      request.NumeroCuenta,
      request.TipoCuenta,
      request.NombreTitular,
      request.ClabeCuenta,
      request.Rfc,
      request.Activo,
      request.CuentaContableId,
      request.CuentaContableEgreso,
      request.CuentaContableIngreso
    };

    var newId = await connection.ExecuteScalarAsync<int>(insertSql, parameters).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();

    var account = await GetAccountByIdAsync(connection, newId, cancellationToken).ConfigureAwait(false);

    if (account is null)
    {
      throw new InvalidOperationException("The inserted bank account could not be retrieved.");
    }

    return account;
  }

  public async Task<BankAccountDto?> UpdateAccountAsync(
      int accountId,
      BankAccountRequest request,
      CancellationToken cancellationToken = default)
  {
    if (accountId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (string.IsNullOrWhiteSpace(request.Rfc))
    {
      throw new ArgumentException("RFC is required.", nameof(request));
    }

    const string updateSql = @"
UPDATE bancos.Cuentas_Banco
SET
    Nombre_Banco = @NombreBanco,
    Numero_Cuenta = @NumeroCuenta,
    Tipo_Cuenta = @TipoCuenta,
    Nombre_Titular = @NombreTitular,
    CLABE_Cuenta = @ClabeCuenta,
    RFC = @Rfc,
    Activo = @Activo,
    Cuenta_Contable_ID = @CuentaContableId,
    Cuenta_Contable_Egreso = @CuentaContableEgreso,
    Cuenta_Contable_Ingreso = @CuentaContableIngreso
WHERE Cuenta_Banco_ID = @CuentaBancoId
  AND RFC = @Rfc;
";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var parameters = new
    {
      CuentaBancoId = accountId,
      request.NombreBanco,
      request.NumeroCuenta,
      request.TipoCuenta,
      request.NombreTitular,
      request.ClabeCuenta,
      request.Rfc,
      request.Activo,
      request.CuentaContableId,
      request.CuentaContableEgreso,
      request.CuentaContableIngreso
    };

    var affected = await connection.ExecuteAsync(updateSql, parameters).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();

    if (affected == 0)
    {
      return null;
    }

    return await GetAccountByIdAsync(connection, accountId, cancellationToken).ConfigureAwait(false);
  }

  public async Task DeleteAccountAsync(
      int accountId,
      string rfc,
      CancellationToken cancellationToken = default)
  {
    if (accountId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    if (string.IsNullOrWhiteSpace(rfc))
    {
      throw new ArgumentException("RFC is required.", nameof(rfc));
    }

    const string deleteSql = @"
DELETE FROM bancos.Cuentas_Banco
WHERE Cuenta_Banco_ID = @CuentaBancoId
  AND RFC = @Rfc;
";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await connection.ExecuteAsync(deleteSql, new { CuentaBancoId = accountId, Rfc = rfc }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
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
ORDER BY M.Secuencia_Clave desc;";

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

  public async Task<IReadOnlyList<BankMovementDto>> GetMovementsByTransactionAsync(
      int transaccionId,
      CancellationToken cancellationToken = default)
  {
    if (transaccionId <= 0)
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
WHERE M.Transaccion_ID = @TransaccionId
ORDER BY M.Dia DESC, M.Movimiento_ID DESC;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<BankMovementDto>(sql, new { TransaccionId = transaccionId }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<IReadOnlyList<PendingBankTransactionDto>> GetPendingTransactionsAsync(
      string rfc,
      int year,
      int month,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<PendingBankTransactionDto>();
    }

    const string sql = @"
SELECT
    t.ID AS TransaccionId,
    t.Fecha,
    t.Forma_Pago AS FormaPago,
    t.Concepto,
    CAST(ISNULL(t.Monto, 0) AS decimal(19,2)) AS Monto
FROM dbo.Transacciones AS t
WHERE t.RFC = @Rfc
  AND t.Forma_Pago = '03'
  AND YEAR(t.Fecha) = @Year
  AND MONTH(t.Fecha) = @Month
  AND NOT EXISTS (
      SELECT 1
      FROM bancos.Movimientos AS m
      WHERE m.Transaccion_ID = t.ID
  )
ORDER BY t.Fecha DESC, t.ID DESC;";

    var parameters = new
    {
      Rfc = rfc,
      Year = year,
      Month = month
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<PendingBankTransactionDto>(sql, parameters).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<ProcessBbvaResult?> ProcessBbvaFileAsync(
      string fileContents,
      int accountId,
      decimal initialBalance,
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

    const string storedProcedure = "bancos.Procesar_Movimientos_XML";

    var parameters = new DynamicParameters();
    parameters.Add("@ArchivoXML", fileContents, DbType.String);
    parameters.Add("@Cuenta_Banco_ID", accountId, DbType.Int32);
    parameters.Add("@SaldoInicial", initialBalance, DbType.Decimal);

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var row = await connection.QuerySingleOrDefaultAsync(
       storedProcedure,
       parameters,
       commandType: CommandType.StoredProcedure);

    if (row is null) return null;

    var record = (IDictionary<string, object?>)row;

    return new ProcessBbvaResult
    {
      Insertados = GetInt32(record, "Insertados"),
      Actualizados = GetInt32(record, "Actualizados"),
      CuentaBancoId = GetInt32(record, "Cuenta_Banco_ID"),
      NombreBanco = GetString(record, "Nombre_Banco"),
      NumeroCuenta = GetString(record, "Numero_Cuenta"),
      ArchivoHash = GetString(record, "ArchivoHash"),
      BalanceWarnings = GetInt32(record, "Balance_Warnings"),
    };
  }

  public async Task<int> CreateAutoPoliciesAsync(
      string rfc,
      int year,
      int month,
      int? accountId,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return 0;
    }

    const string selectSql = @"
SELECT
    M.Movimiento_ID AS MovimientoId,
    M.Dia,
    M.Concepto,
    M.Tipo,
    M.Cargo,
    M.Abono,
    M.Cuenta_Banco_ID AS CuentaBancoId
FROM bancos.Movimientos AS M
WHERE M.RFC = @Rfc
  AND YEAR(M.Dia) = @Year
  AND MONTH(M.Dia) = @Month
  AND (@AccountId IS NULL OR M.Cuenta_Banco_ID = @AccountId)
  AND M.Transaccion_ID IS NULL
ORDER BY M.Dia, M.Movimiento_ID;";

    var parameters = new
    {
      Rfc = rfc,
      Year = year,
      Month = month,
      AccountId = accountId
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var candidates = await connection
        .QueryAsync<AutoPolicyCandidate>(selectSql, parameters)
        .ConfigureAwait(false);

    cancellationToken.ThrowIfCancellationRequested();

    var processed = 0;

    foreach (var candidate in candidates)
    {
      cancellationToken.ThrowIfCancellationRequested();

      var monto = string.Equals(candidate.Tipo, "I", StringComparison.OrdinalIgnoreCase)
        ? candidate.Cargo
        : candidate.Abono;

      var spParameters = new DynamicParameters();
      spParameters.Add("@RFC", rfc, DbType.String);
      spParameters.Add("@Fecha", candidate.Dia, DbType.DateTime2);
      spParameters.Add("@Concepto", candidate.Concepto, DbType.String);
      spParameters.Add("@Tipo", candidate.Tipo, DbType.StringFixedLength, size: 1);
      spParameters.Add("@Monto", monto, DbType.Decimal);
      spParameters.Add("@CuentaBancoID", candidate.CuentaBancoId, DbType.Int32);
      spParameters.Add("@TransaccionID", dbType: DbType.Int32, direction: ParameterDirection.Output);

      await connection.ExecuteAsync(
              "dbo.Crear_Transaccion_Contable_Banco",
              spParameters,
              commandType: CommandType.StoredProcedure)
          .ConfigureAwait(false);

      var transactionId = spParameters.Get<int?>("@TransaccionID");

      if (transactionId.HasValue && transactionId.Value > 0)
      {
        await connection.ExecuteAsync(
                "UPDATE bancos.Movimientos SET Transaccion_ID = @TransaccionId WHERE Movimiento_ID = @MovimientoId;",
                new { TransaccionId = transactionId.Value, MovimientoId = candidate.MovimientoId })
            .ConfigureAwait(false);

        processed++;
      }
    }

    return processed;
  }

  public async Task UnlinkMovementAsync(
      long movimientoId,
      CancellationToken cancellationToken = default)
  {
    if (movimientoId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(movimientoId));
    }

    const string sql = @"UPDATE bancos.Movimientos
SET Transaccion_ID = NULL
WHERE Movimiento_ID = @MovimientoId;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await connection.ExecuteAsync(sql, new { MovimientoId = movimientoId }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
  }

  public async Task LinkMovementToTransactionAsync(
      long movimientoId,
      int transaccionId,
      CancellationToken cancellationToken = default)
  {
    if (movimientoId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(movimientoId));
    }

    if (transaccionId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(transaccionId));
    }

    const string sql = "UPDATE bancos.Movimientos SET Transaccion_ID = @TransaccionId WHERE Movimiento_ID = @MovimientoId;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await connection.ExecuteAsync(sql, new { MovimientoId = movimientoId, TransaccionId = transaccionId })
        .ConfigureAwait(false);
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

  private static int GetInt32(IDictionary<string, object?> record, string key)
  {
    if (!record.TryGetValue(key, out var value) || value is null || value is DBNull)
    {
      return 0;
    }

    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
  }

  private static string GetString(IDictionary<string, object?> record, string key)
  {
    if (!record.TryGetValue(key, out var value) || value is null || value is DBNull)
    {
      return string.Empty;
    }

    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
  }

  private static async Task<BankAccountDto?> GetAccountByIdAsync(
      IDbConnection connection,
      int accountId,
      CancellationToken cancellationToken)
  {
    var sql = AccountSelectSql + "WHERE Cuenta_Banco_ID = @CuentaBancoId;";
    var account = await connection.QuerySingleOrDefaultAsync<BankAccountDto>(sql, new { CuentaBancoId = accountId })
        .ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return account;
  }

  private sealed record AutoPolicyCandidate
  {
    public long MovimientoId { get; init; }
    public DateTime Dia { get; init; }
    public string Concepto { get; init; } = string.Empty;
    public string Tipo { get; init; } = string.Empty;
    public decimal Cargo { get; init; }
    public decimal Abono { get; init; }
    public int CuentaBancoId { get; init; }
  }
}
