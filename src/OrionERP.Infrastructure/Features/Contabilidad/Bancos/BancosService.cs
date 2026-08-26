using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Contabilidad.Bancos;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.Infrastructure.Features.Contabilidad.Bancos;

public sealed class BancosService : IBancosService
{
  private readonly IDbConnectionFactory _connectionFactory;

  private const string AccountSelectSql = @"
SELECT
    cb.Cuenta_Banco_ID AS CuentaBancoId,
    cb.Nombre_Banco AS NombreBanco,
    cb.Numero_Cuenta AS NumeroCuenta,
    cb.Tipo_Cuenta AS TipoCuenta,
    cb.Nombre_Titular AS NombreTitular,
    cb.CLABE_Cuenta AS ClabeCuenta,
    cb.RFC,
    cb.Activo,
    cb.Fecha_Alta AS FechaAlta,
    cb.Cuenta_Contable_ID AS CuentaContableId,
    cb.Cuenta_Contable_Egreso AS CuentaContableEgreso,
    cb.Cuenta_Contable_Ingreso AS CuentaContableIngreso,
    cc.Nivel1 AS CuentaContableNivel1,
    cc.Nivel2 AS CuentaContableNivel2,
    cc.Nivel3 AS CuentaContableNivel3,
    cc.Descripcion AS CuentaContableDescripcion
FROM bancos.Cuentas_Banco AS cb
LEFT JOIN dbo.CuentasContables AS cc
    ON cc.id = cb.Cuenta_Contable_ID
   AND cc.RFC = cb.RFC
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

    var sql = AccountSelectSql + "WHERE cb.RFC = @Rfc\nORDER BY cb.Fecha_Alta DESC;";

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
EXEC bancos.sp_Movimientos_Bancarios
  @RFC = @Rfc,
  @AccountId = @AccountId,
  @Year = @Year,
  @Month = @Month,
  @TextFilter = @TextFilter;";

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
    MT.Transaccion_ID AS Policy,
    CAST(ISNULL(LA.PolicyCount, 0) AS int) AS PolicyCount,
    ISNULL(LA.LinkedPolicyIds, '') AS LinkedPolicyIds,
    ISNULL(LA.LinkedPolicySummary, '') AS LinkedPolicySummary,
    CAST(ISNULL(LA.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
    CAST(ISNULL(LA.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
    T.Fecha AS PolicyDate,
    T.OrdenBalance,
    CAST(MT.Debe AS decimal(19,2)) AS BankRegistroDebe,
    CAST(MT.Haber AS decimal(19,2)) AS BankRegistroHaber,
    CAST(1 AS int) AS BankRegistroLineCount
FROM bancos.Movimientos AS M
INNER JOIN bancos.Movimiento_Transaccion AS MT
    ON MT.Movimiento_ID = M.Movimiento_ID
INNER JOIN dbo.Transacciones AS T
    ON T.ID = MT.Transaccion_ID
OUTER APPLY (
    SELECT
        COUNT(*) AS PolicyCount,
        STRING_AGG(CONVERT(varchar(20), MT2.Transaccion_ID), ', ') WITHIN GROUP (ORDER BY T2.Fecha, T2.OrdenBalance, T2.ID) AS LinkedPolicyIds,
        STRING_AGG(CONVERT(varchar(30), MT2.Transaccion_ID) + ':' + CONVERT(varchar(40), CAST(CASE WHEN MT2.Debe > 0 THEN MT2.Debe ELSE MT2.Haber END AS decimal(19,2))), ', ') WITHIN GROUP (ORDER BY T2.Fecha, T2.OrdenBalance, T2.ID) AS LinkedPolicySummary,
        SUM(MT2.Debe) AS LinkedDebe,
        SUM(MT2.Haber) AS LinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT2
    INNER JOIN dbo.Transacciones AS T2
        ON T2.ID = MT2.Transaccion_ID
    WHERE MT2.Movimiento_ID = M.Movimiento_ID
) AS LA
WHERE MT.Transaccion_ID = @TransaccionId
ORDER BY M.Dia DESC, M.Movimiento_ID DESC;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<BankMovementDto>(sql, new { TransaccionId = transaccionId }).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<IReadOnlyList<PendingBankTransactionDto>> GetPendingTransactionsAsync(
      string rfc,
      int? accountId,
      int year,
      int month,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return Array.Empty<PendingBankTransactionDto>();
    }

    var (startDate, endDate) = BuildMonthRange(year, month);

    const string sql = @"
WITH CuentasBancoFiltradas AS (
    SELECT DISTINCT
        cb.Cuenta_Banco_ID,
        cc.Nivel1,
        cc.Nivel2,
        cc.Nivel3
    FROM bancos.Cuentas_Banco AS cb
    INNER JOIN dbo.CuentasContables AS cc
        ON cc.id = cb.Cuenta_Contable_ID
       AND cc.RFC = cb.RFC
    WHERE cb.RFC = @Rfc
      AND (@AccountId IS NULL OR cb.Cuenta_Banco_ID = @AccountId)
),
RegistroBancoPendienteLineas AS (
    SELECT DISTINCT
        rc.ID,
        rc.TransaccionID,
        rc.Debe,
        rc.Haber
    FROM dbo.Registro_Contable AS rc
    INNER JOIN CuentasBancoFiltradas AS cbf
        ON rc.Nivel1 = cbf.Nivel1
       AND rc.Nivel2 = cbf.Nivel2
       AND rc.Nivel3 = cbf.Nivel3
    WHERE NOT EXISTS (
        SELECT 1
        FROM bancos.Movimiento_Transaccion AS mt
        INNER JOIN bancos.Movimientos AS m
            ON m.Movimiento_ID = mt.Movimiento_ID
        WHERE mt.Transaccion_ID = rc.TransaccionID
          AND m.Cuenta_Banco_ID = cbf.Cuenta_Banco_ID
    )
),
RegistroBancoPendiente AS (
    SELECT
        TransaccionID,
        CAST(COUNT(*) AS int) AS BankRegistroLineCount,
        CAST(ISNULL(SUM(Debe), 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(SUM(Haber), 0) AS decimal(19,2)) AS BankRegistroHaber
    FROM RegistroBancoPendienteLineas
    GROUP BY TransaccionID
)
SELECT
    t.ID AS TransaccionId,
    t.Fecha,
    t.Forma_Pago AS FormaPago,
    t.Concepto,
    CAST(ISNULL(t.Monto, 0) AS decimal(19,2)) AS Monto,
    rb.BankRegistroLineCount,
    rb.BankRegistroDebe,
    rb.BankRegistroHaber
FROM dbo.Transacciones AS t
INNER JOIN RegistroBancoPendiente AS rb
    ON rb.TransaccionID = t.ID
WHERE t.RFC = @Rfc
  AND t.Fecha >= @StartDate
  AND t.Fecha < @EndDate
ORDER BY t.Fecha DESC, t.ID DESC;";

    var parameters = new
    {
      Rfc = rfc,
      AccountId = accountId,
      StartDate = startDate,
      EndDate = endDate
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var rows = await connection.QueryAsync<PendingBankTransactionDto>(sql, parameters).ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return rows.AsList();
  }

  public async Task<BankMovementLinkingWorkspaceDto> GetMovementLinkingWorkspaceAsync(
      long movimientoId,
      string? search,
      bool includeOtherCandidates,
      int? focusTransaccionId = null,
      CancellationToken cancellationToken = default)
  {
    if (movimientoId <= 0)
    {
      return new BankMovementLinkingWorkspaceDto();
    }

    const string sql = @"
SET NOCOUNT ON;

DECLARE @SearchTerm nvarchar(200) = NULLIF(LTRIM(RTRIM(@Search)), N'');

;WITH MovementContext AS
(
    SELECT
        M.Movimiento_ID AS MovimientoId,
        ISNULL(M.RFC, '') AS Rfc,
        M.Cuenta_Banco_ID AS CuentaBancoId,
        ISNULL(M.Nombre_Banco, '') AS NombreBanco,
        ISNULL(M.Numero_Cuenta, '') AS NumeroCuenta,
        CAST(M.Dia AS datetime) AS Dia,
        ISNULL(M.Concepto, '') AS Concepto,
        ISNULL(M.Tipo, '') AS Tipo,
        CAST(ISNULL(M.Cargo, 0) AS decimal(19,2)) AS Cargo,
        CAST(ISNULL(M.Abono, 0) AS decimal(19,2)) AS Abono,
        CAST(ISNULL(M.Saldo, 0) AS decimal(19,2)) AS Saldo,
        CB.Cuenta_Contable_ID AS CuentaContableId,
        ISNULL(CC.Nivel1, '') AS BankAccountNivel1,
        ISNULL(CC.Nivel2, '') AS BankAccountNivel2,
        ISNULL(CC.Nivel3, '') AS BankAccountNivel3,
        ISNULL(CC.Descripcion, '') AS BankAccountDescription,
        CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END AS decimal(19,2)) AS ExpectedDebe,
        CAST(CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END AS decimal(19,2)) AS ExpectedHaber
    FROM bancos.Movimientos AS M
    INNER JOIN bancos.Cuentas_Banco AS CB
        ON CB.Cuenta_Banco_ID = M.Cuenta_Banco_ID
    LEFT JOIN dbo.CuentasContables AS CC
        ON CC.id = CB.Cuenta_Contable_ID
       AND CC.RFC = CB.RFC
    WHERE M.Movimiento_ID = @MovimientoId
),
LinkAgg AS
(
    SELECT
        MT.Movimiento_ID,
        CAST(ISNULL(SUM(MT.Debe), 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(SUM(MT.Haber), 0) AS decimal(19,2)) AS LinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT
    WHERE MT.Movimiento_ID = @MovimientoId
    GROUP BY MT.Movimiento_ID
)
SELECT
    MC.*,
    CAST(ISNULL(LA.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
    CAST(ISNULL(LA.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
    CAST(CASE WHEN MC.CuentaContableId IS NOT NULL
               AND MC.BankAccountNivel1 <> ''
               AND MC.BankAccountNivel2 <> ''
               AND MC.BankAccountNivel3 <> ''
         THEN 1 ELSE 0 END AS bit) AS MappingValid,
    CASE WHEN MC.CuentaContableId IS NULL THEN N'La cuenta bancaria no tiene Cuenta_Contable_ID.'
         WHEN MC.BankAccountNivel1 = '' OR MC.BankAccountNivel2 = '' OR MC.BankAccountNivel3 = '' THEN N'La Cuenta_Contable_ID de bancos.Cuentas_Banco no resuelve a CuentasContables para este RFC.'
         ELSE NULL END AS SetupIssue
FROM MovementContext AS MC
LEFT JOIN LinkAgg AS LA
    ON LA.Movimiento_ID = MC.MovimientoId;

;WITH MovementContext AS
(
    SELECT M.Movimiento_ID, M.RFC, CC.Nivel1, CC.Nivel2, CC.Nivel3
    FROM bancos.Movimientos AS M
    INNER JOIN bancos.Cuentas_Banco AS CB
        ON CB.Cuenta_Banco_ID = M.Cuenta_Banco_ID
    LEFT JOIN dbo.CuentasContables AS CC
        ON CC.id = CB.Cuenta_Contable_ID
       AND CC.RFC = CB.RFC
    WHERE M.Movimiento_ID = @MovimientoId
),
RcBank AS
(
    SELECT
        RC.TransaccionID,
        CAST(ISNULL(SUM(RC.Debe), 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(SUM(RC.Haber), 0) AS decimal(19,2)) AS BankRegistroHaber
    FROM dbo.Registro_Contable AS RC
    INNER JOIN MovementContext AS MC
        ON RC.Nivel1 = MC.Nivel1
       AND RC.Nivel2 = MC.Nivel2
       AND RC.Nivel3 = MC.Nivel3
    GROUP BY RC.TransaccionID
),
OtherLinks AS
(
    SELECT
        MT.Transaccion_ID AS TransaccionId,
        CAST(ISNULL(SUM(CASE WHEN MT.Movimiento_ID <> @MovimientoId THEN MT.Debe ELSE 0 END), 0) AS decimal(19,2)) AS OtherLinkedDebe,
        CAST(ISNULL(SUM(CASE WHEN MT.Movimiento_ID <> @MovimientoId THEN MT.Haber ELSE 0 END), 0) AS decimal(19,2)) AS OtherLinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT
    GROUP BY MT.Transaccion_ID
)
SELECT
    MT.Movimiento_ID AS MovimientoId,
    T.ID AS TransaccionId,
    T.Fecha,
    T.Concepto,
    CAST(ISNULL(T.Monto, 0) AS decimal(19,2)) AS TransaccionMonto,
    T.Tipo_Poliza AS TipoPoliza,
    T.Forma_Pago AS FormaPago,
    CAST(MT.Debe AS decimal(19,2)) AS Debe,
    CAST(MT.Haber AS decimal(19,2)) AS Haber,
    CAST(ISNULL(RB.BankRegistroDebe, 0) AS decimal(19,2)) AS BankRegistroDebe,
    CAST(ISNULL(RB.BankRegistroHaber, 0) AS decimal(19,2)) AS BankRegistroHaber,
    CAST(ISNULL(OL.OtherLinkedDebe, 0) AS decimal(19,2)) AS OtherLinkedDebe,
    CAST(ISNULL(OL.OtherLinkedHaber, 0) AS decimal(19,2)) AS OtherLinkedHaber,
    CAST(ISNULL(RB.BankRegistroDebe, 0) - ISNULL(OL.OtherLinkedDebe, 0) AS decimal(19,2)) AS AvailableDebe,
    CAST(ISNULL(RB.BankRegistroHaber, 0) - ISNULL(OL.OtherLinkedHaber, 0) AS decimal(19,2)) AS AvailableHaber,
    CASE WHEN (MT.Debe > 0 AND MT.Debe <= ISNULL(RB.BankRegistroDebe, 0) - ISNULL(OL.OtherLinkedDebe, 0) + 0.01)
           OR (MT.Haber > 0 AND MT.Haber <= ISNULL(RB.BankRegistroHaber, 0) - ISNULL(OL.OtherLinkedHaber, 0) + 0.01)
         THEN N'OK' ELSE N'REVISAR' END AS MatchStatus
FROM bancos.Movimiento_Transaccion AS MT
INNER JOIN dbo.Transacciones AS T
    ON T.ID = MT.Transaccion_ID
LEFT JOIN RcBank AS RB
    ON RB.TransaccionID = T.ID
LEFT JOIN OtherLinks AS OL
    ON OL.TransaccionId = T.ID
WHERE MT.Movimiento_ID = @MovimientoId
ORDER BY T.Fecha, T.OrdenBalance, T.ID;

;WITH MovementContext AS
(
    SELECT
        M.Movimiento_ID,
        M.RFC,
        M.Dia,
        CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END AS decimal(19,2)) AS ExpectedDebe,
        CAST(CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END AS decimal(19,2)) AS ExpectedHaber,
        CC.Nivel1,
        CC.Nivel2,
        CC.Nivel3
    FROM bancos.Movimientos AS M
    INNER JOIN bancos.Cuentas_Banco AS CB
        ON CB.Cuenta_Banco_ID = M.Cuenta_Banco_ID
    LEFT JOIN dbo.CuentasContables AS CC
        ON CC.id = CB.Cuenta_Contable_ID
       AND CC.RFC = CB.RFC
    WHERE M.Movimiento_ID = @MovimientoId
),
RcBank AS
(
    SELECT
        RC.TransaccionID,
        CAST(ISNULL(SUM(RC.Debe), 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(SUM(RC.Haber), 0) AS decimal(19,2)) AS BankRegistroHaber
    FROM dbo.Registro_Contable AS RC
    INNER JOIN MovementContext AS MC
        ON RC.Nivel1 = MC.Nivel1
       AND RC.Nivel2 = MC.Nivel2
       AND RC.Nivel3 = MC.Nivel3
    GROUP BY RC.TransaccionID
),
LinkTotals AS
(
    SELECT
        MT.Transaccion_ID AS TransaccionId,
        CAST(ISNULL(SUM(MT.Debe), 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(SUM(MT.Haber), 0) AS decimal(19,2)) AS LinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT
    WHERE MT.Movimiento_ID <> @MovimientoId
    GROUP BY MT.Transaccion_ID
),
Candidates AS
(
    SELECT TOP (120)
        T.ID AS TransaccionId,
        T.Fecha,
        T.Concepto,
        CAST(ISNULL(T.Monto, 0) AS decimal(19,2)) AS TransaccionMonto,
        T.Tipo_Poliza AS TipoPoliza,
        T.Forma_Pago AS FormaPago,
        CAST(ISNULL(RB.BankRegistroDebe, 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(RB.BankRegistroHaber, 0) AS decimal(19,2)) AS BankRegistroHaber,
        CAST(ISNULL(LT.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(LT.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
        CAST(ISNULL(RB.BankRegistroDebe, 0) - ISNULL(LT.LinkedDebe, 0) AS decimal(19,2)) AS AvailableDebe,
        CAST(ISNULL(RB.BankRegistroHaber, 0) - ISNULL(LT.LinkedHaber, 0) AS decimal(19,2)) AS AvailableHaber,
        CAST(CASE WHEN RB.TransaccionID IS NULL THEN 0 ELSE 1 END AS bit) AS HasBankLine,
        CAST(CASE WHEN RB.TransaccionID IS NULL THEN 1 ELSE 0 END AS bit) AS IsOtherCandidate,
        CASE
            WHEN @FocusTransaccionId IS NOT NULL AND T.ID = @FocusTransaccionId THEN 100
            WHEN RB.TransaccionID IS NOT NULL AND DATEDIFF(DAY, T.Fecha, MC.Dia) = 0 THEN 90
            WHEN RB.TransaccionID IS NOT NULL AND ABS(DATEDIFF(DAY, T.Fecha, MC.Dia)) <= 3 THEN 75
            WHEN RB.TransaccionID IS NOT NULL THEN 60
            ELSE 20
        END AS MatchScore,
        CASE
            WHEN RB.TransaccionID IS NULL THEN N'OTRA'
            WHEN MC.ExpectedDebe > 0 AND ISNULL(RB.BankRegistroDebe, 0) - ISNULL(LT.LinkedDebe, 0) <= 0.01 THEN N'REVISAR'
            WHEN MC.ExpectedHaber > 0 AND ISNULL(RB.BankRegistroHaber, 0) - ISNULL(LT.LinkedHaber, 0) <= 0.01 THEN N'REVISAR'
            WHEN MC.ExpectedDebe > 0 AND ISNULL(RB.BankRegistroDebe, 0) - ISNULL(LT.LinkedDebe, 0) >= MC.ExpectedDebe - 0.01 THEN N'FUERTE'
            WHEN MC.ExpectedHaber > 0 AND ISNULL(RB.BankRegistroHaber, 0) - ISNULL(LT.LinkedHaber, 0) >= MC.ExpectedHaber - 0.01 THEN N'FUERTE'
            ELSE N'POSIBLE'
        END AS MatchStatus
    FROM MovementContext AS MC
    INNER JOIN dbo.Transacciones AS T
        ON T.RFC = MC.RFC
    LEFT JOIN RcBank AS RB
        ON RB.TransaccionID = T.ID
    LEFT JOIN LinkTotals AS LT
        ON LT.TransaccionId = T.ID
    WHERE NOT EXISTS (
            SELECT 1
            FROM bancos.Movimiento_Transaccion AS Existing
            WHERE Existing.Movimiento_ID = @MovimientoId
              AND Existing.Transaccion_ID = T.ID
        )
      AND (@SearchTerm IS NULL
           OR CONVERT(varchar(20), T.ID) = @SearchTerm
           OR T.Concepto LIKE N'%' + @SearchTerm + N'%')
      AND (
           T.ID = @FocusTransaccionId
           OR RB.TransaccionID IS NOT NULL
           OR @IncludeOtherCandidates = 1
      )
    ORDER BY
        CASE WHEN @FocusTransaccionId IS NOT NULL AND T.ID = @FocusTransaccionId THEN 0 ELSE 1 END,
        CASE WHEN RB.TransaccionID IS NULL THEN 1 ELSE 0 END,
        ABS(DATEDIFF(DAY, T.Fecha, MC.Dia)),
        T.Fecha DESC,
        T.ID DESC
)
SELECT *
FROM Candidates
ORDER BY IsOtherCandidate, MatchScore DESC, Fecha DESC, TransaccionId DESC;";

    var parameters = new
    {
      MovimientoId = movimientoId,
      Search = search,
      IncludeOtherCandidates = includeOtherCandidates,
      FocusTransaccionId = focusTransaccionId
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    using var multi = await connection.QueryMultipleAsync(
        new CommandDefinition(sql, parameters, cancellationToken: cancellationToken, commandTimeout: 120))
      .ConfigureAwait(false);

    var workspace = new BankMovementLinkingWorkspaceDto
    {
      Summary = await multi.ReadFirstOrDefaultAsync<BankMovementLinkingSummaryDto>().ConfigureAwait(false)
    };

    workspace.Links.AddRange((await multi.ReadAsync<BankMovementTransactionLinkDto>().ConfigureAwait(false)).AsList());
    workspace.Candidates.AddRange((await multi.ReadAsync<BankMovementTransactionCandidateDto>().ConfigureAwait(false)).AsList());
    cancellationToken.ThrowIfCancellationRequested();

    return workspace;
  }

  public async Task<BankTransactionMovementWorkspaceDto> GetTransactionMovementLinkingWorkspaceAsync(
      int transaccionId,
      string? search,
      bool includeFullyLinkedMovements,
      CancellationToken cancellationToken = default)
  {
    if (transaccionId <= 0)
    {
      return new BankTransactionMovementWorkspaceDto();
    }

    const string sql = @"
SET NOCOUNT ON;

DECLARE @SearchTerm nvarchar(200) = NULLIF(LTRIM(RTRIM(@Search)), N'');

;WITH TransactionContext AS
(
    SELECT
        T.ID AS TransaccionId,
        ISNULL(T.RFC, '') AS Rfc,
        T.Fecha,
        ISNULL(T.Concepto, '') AS Concepto,
        CAST(ISNULL(T.Monto, 0) AS decimal(19,2)) AS Monto,
        ISNULL(T.Tipo_Poliza, '') AS TipoPoliza,
        ISNULL(T.Forma_Pago, '') AS FormaPago
    FROM dbo.Transacciones AS T
    WHERE T.ID = @TransaccionId
),
BankLines AS
(
    SELECT
        CB.Cuenta_Banco_ID AS CuentaBancoId,
        CAST(ISNULL(SUM(RC.Debe), 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(SUM(RC.Haber), 0) AS decimal(19,2)) AS BankRegistroHaber
    FROM TransactionContext AS TC
    INNER JOIN dbo.Registro_Contable AS RC
        ON RC.TransaccionID = TC.TransaccionId
    INNER JOIN bancos.Cuentas_Banco AS CB
        ON CB.RFC = TC.Rfc
    INNER JOIN dbo.CuentasContables AS CC
        ON CC.id = CB.Cuenta_Contable_ID
       AND CC.RFC = CB.RFC
       AND CC.Nivel1 = RC.Nivel1
       AND CC.Nivel2 = RC.Nivel2
       AND CC.Nivel3 = RC.Nivel3
    GROUP BY CB.Cuenta_Banco_ID
),
LinkedTotals AS
(
    SELECT
        CAST(ISNULL(SUM(MT.Debe), 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(SUM(MT.Haber), 0) AS decimal(19,2)) AS LinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT
    WHERE MT.Transaccion_ID = @TransaccionId
)
SELECT
    TC.TransaccionId,
    TC.Rfc,
    TC.Fecha,
    TC.Concepto,
    TC.Monto,
    TC.TipoPoliza,
    TC.FormaPago,
    CAST(ISNULL(BL.BankLineCount, 0) AS int) AS BankLineCount,
    CAST(ISNULL(BL.BankRegistroDebe, 0) AS decimal(19,2)) AS BankRegistroDebe,
    CAST(ISNULL(BL.BankRegistroHaber, 0) AS decimal(19,2)) AS BankRegistroHaber,
    CAST(ISNULL(LT.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
    CAST(ISNULL(LT.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
    CAST(CASE WHEN ISNULL(BL.BankLineCount, 0) > 0 THEN 1 ELSE 0 END AS bit) AS HasBankAccountMapping,
    CASE WHEN ISNULL(BL.BankLineCount, 0) = 0
         THEN N'La póliza no tiene una línea de Registro_Contable que coincida con una cuenta bancaria configurada.'
         ELSE NULL END AS SetupIssue
FROM TransactionContext AS TC
OUTER APPLY
(
    SELECT
        COUNT(*) AS BankLineCount,
        SUM(BankRegistroDebe) AS BankRegistroDebe,
        SUM(BankRegistroHaber) AS BankRegistroHaber
    FROM BankLines
) AS BL
CROSS JOIN LinkedTotals AS LT;

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
    MT.Transaccion_ID AS Policy,
    CAST(ISNULL(LA.PolicyCount, 0) AS int) AS PolicyCount,
    ISNULL(LA.LinkedPolicyIds, '') AS LinkedPolicyIds,
    ISNULL(LA.LinkedPolicySummary, '') AS LinkedPolicySummary,
    CAST(ISNULL(LA.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
    CAST(ISNULL(LA.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
    T.Fecha AS PolicyDate,
    T.OrdenBalance,
    CAST(MT.Debe AS decimal(19,2)) AS BankRegistroDebe,
    CAST(MT.Haber AS decimal(19,2)) AS BankRegistroHaber,
    CAST(1 AS int) AS BankRegistroLineCount
FROM bancos.Movimientos AS M
INNER JOIN bancos.Movimiento_Transaccion AS MT
    ON MT.Movimiento_ID = M.Movimiento_ID
INNER JOIN dbo.Transacciones AS T
    ON T.ID = MT.Transaccion_ID
OUTER APPLY (
    SELECT
        COUNT(*) AS PolicyCount,
        STRING_AGG(CONVERT(varchar(20), MT2.Transaccion_ID), ', ') WITHIN GROUP (ORDER BY T2.Fecha, T2.OrdenBalance, T2.ID) AS LinkedPolicyIds,
        STRING_AGG(CONVERT(varchar(30), MT2.Transaccion_ID) + ':' + CONVERT(varchar(40), CAST(CASE WHEN MT2.Debe > 0 THEN MT2.Debe ELSE MT2.Haber END AS decimal(19,2))), ', ') WITHIN GROUP (ORDER BY T2.Fecha, T2.OrdenBalance, T2.ID) AS LinkedPolicySummary,
        SUM(MT2.Debe) AS LinkedDebe,
        SUM(MT2.Haber) AS LinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT2
    INNER JOIN dbo.Transacciones AS T2
        ON T2.ID = MT2.Transaccion_ID
    WHERE MT2.Movimiento_ID = M.Movimiento_ID
) AS LA
WHERE MT.Transaccion_ID = @TransaccionId
ORDER BY M.Dia DESC, M.Movimiento_ID DESC;

;WITH TransactionContext AS
(
    SELECT
        T.ID AS TransaccionId,
        ISNULL(T.RFC, '') AS Rfc,
        T.Fecha
    FROM dbo.Transacciones AS T
    WHERE T.ID = @TransaccionId
),
BankLines AS
(
    SELECT
        CB.Cuenta_Banco_ID AS CuentaBancoId,
        CAST(ISNULL(SUM(RC.Debe), 0) AS decimal(19,2)) AS BankRegistroDebe,
        CAST(ISNULL(SUM(RC.Haber), 0) AS decimal(19,2)) AS BankRegistroHaber
    FROM TransactionContext AS TC
    INNER JOIN dbo.Registro_Contable AS RC
        ON RC.TransaccionID = TC.TransaccionId
    INNER JOIN bancos.Cuentas_Banco AS CB
        ON CB.RFC = TC.Rfc
    INNER JOIN dbo.CuentasContables AS CC
        ON CC.id = CB.Cuenta_Contable_ID
       AND CC.RFC = CB.RFC
       AND CC.Nivel1 = RC.Nivel1
       AND CC.Nivel2 = RC.Nivel2
       AND CC.Nivel3 = RC.Nivel3
    GROUP BY CB.Cuenta_Banco_ID
),
Candidates AS
(
    SELECT TOP (150)
        M.Movimiento_ID AS MovimientoId,
        M.Cuenta_Banco_ID AS CuentaBancoId,
        ISNULL(M.Nombre_Banco, '') AS NombreBanco,
        ISNULL(M.Numero_Cuenta, '') AS NumeroCuenta,
        M.Dia,
        ISNULL(M.Concepto, '') AS Concepto,
        ISNULL(M.Tipo, '') AS Tipo,
        CAST(ISNULL(M.Cargo, 0) AS decimal(19,2)) AS Cargo,
        CAST(ISNULL(M.Abono, 0) AS decimal(19,2)) AS Abono,
        CAST(ISNULL(M.Saldo, 0) AS decimal(19,2)) AS Saldo,
        CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END AS decimal(19,2)) AS ExpectedDebe,
        CAST(CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END AS decimal(19,2)) AS ExpectedHaber,
        CAST(ISNULL(ML.LinkedDebe, 0) AS decimal(19,2)) AS LinkedDebe,
        CAST(ISNULL(ML.LinkedHaber, 0) AS decimal(19,2)) AS LinkedHaber,
        CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END - ISNULL(ML.LinkedDebe, 0) AS decimal(19,2)) AS RemainingDebe,
        CAST(CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END - ISNULL(ML.LinkedHaber, 0) AS decimal(19,2)) AS RemainingHaber,
        CAST(ISNULL(BL.BankRegistroDebe, 0) - ISNULL(TU.OtherLinkedDebe, 0) AS decimal(19,2)) AS TransactionAvailableDebe,
        CAST(ISNULL(BL.BankRegistroHaber, 0) - ISNULL(TU.OtherLinkedHaber, 0) AS decimal(19,2)) AS TransactionAvailableHaber,
        CAST(CASE WHEN ET.Transaccion_ID IS NULL THEN 0 ELSE 1 END AS bit) AS AlreadyLinkedToTransaction,
        CAST(CASE
            WHEN ISNULL(M.Cargo, 0) > 0 AND ABS(ISNULL(M.Cargo, 0) - ISNULL(ML.LinkedDebe, 0)) <= 0.01 THEN 1
            WHEN ISNULL(M.Abono, 0) > 0 AND ABS(ISNULL(M.Abono, 0) - ISNULL(ML.LinkedHaber, 0)) <= 0.01 THEN 1
            ELSE 0
        END AS bit) AS IsFullyLinked,
        CASE
            WHEN ET.Transaccion_ID IS NOT NULL THEN 100
            WHEN DATEDIFF(DAY, M.Dia, TC.Fecha) = 0 THEN 90
            WHEN ABS(DATEDIFF(DAY, M.Dia, TC.Fecha)) <= 3 THEN 75
            ELSE 45
        END AS MatchScore,
        CASE
            WHEN ET.Transaccion_ID IS NOT NULL THEN N'OK'
            WHEN ISNULL(M.Cargo, 0) > 0
             AND ABS(ISNULL(M.Cargo, 0) - ISNULL(ML.LinkedDebe, 0)) <= ISNULL(BL.BankRegistroDebe, 0) - ISNULL(TU.OtherLinkedDebe, 0) + 0.01
             AND ABS(DATEDIFF(DAY, M.Dia, TC.Fecha)) <= 3 THEN N'FUERTE'
            WHEN ISNULL(M.Abono, 0) > 0
             AND ABS(ISNULL(M.Abono, 0) - ISNULL(ML.LinkedHaber, 0)) <= ISNULL(BL.BankRegistroHaber, 0) - ISNULL(TU.OtherLinkedHaber, 0) + 0.01
             AND ABS(DATEDIFF(DAY, M.Dia, TC.Fecha)) <= 3 THEN N'FUERTE'
            WHEN ISNULL(M.Cargo, 0) > 0
             AND ABS(ISNULL(M.Cargo, 0) - ISNULL(ML.LinkedDebe, 0)) <= ISNULL(BL.BankRegistroDebe, 0) - ISNULL(TU.OtherLinkedDebe, 0) + 0.01 THEN N'POSIBLE'
            WHEN ISNULL(M.Abono, 0) > 0
             AND ABS(ISNULL(M.Abono, 0) - ISNULL(ML.LinkedHaber, 0)) <= ISNULL(BL.BankRegistroHaber, 0) - ISNULL(TU.OtherLinkedHaber, 0) + 0.01 THEN N'POSIBLE'
            ELSE N'REVISAR'
        END AS MatchStatus
    FROM TransactionContext AS TC
    INNER JOIN BankLines AS BL
        ON 1 = 1
    INNER JOIN bancos.Movimientos AS M
        ON M.RFC = TC.Rfc
       AND M.Cuenta_Banco_ID = BL.CuentaBancoId
    OUTER APPLY
    (
        SELECT
            SUM(MT.Debe) AS LinkedDebe,
            SUM(MT.Haber) AS LinkedHaber
        FROM bancos.Movimiento_Transaccion AS MT
        WHERE MT.Movimiento_ID = M.Movimiento_ID
    ) AS ML
    OUTER APPLY
    (
        SELECT
            SUM(MT.Debe) AS OtherLinkedDebe,
            SUM(MT.Haber) AS OtherLinkedHaber
        FROM bancos.Movimiento_Transaccion AS MT
        INNER JOIN bancos.Movimientos AS LM
            ON LM.Movimiento_ID = MT.Movimiento_ID
        WHERE MT.Transaccion_ID = TC.TransaccionId
          AND LM.Cuenta_Banco_ID = M.Cuenta_Banco_ID
          AND MT.Movimiento_ID <> M.Movimiento_ID
    ) AS TU
    LEFT JOIN bancos.Movimiento_Transaccion AS ET
        ON ET.Movimiento_ID = M.Movimiento_ID
       AND ET.Transaccion_ID = TC.TransaccionId
    WHERE (@SearchTerm IS NULL
           OR CONVERT(varchar(20), M.Movimiento_ID) = @SearchTerm
           OR M.Concepto LIKE N'%' + @SearchTerm + N'%')
      AND ET.Transaccion_ID IS NULL
      AND (
          @IncludeFullyLinkedMovements = 1
          OR (ISNULL(M.Cargo, 0) > 0 AND ABS(ISNULL(M.Cargo, 0) - ISNULL(ML.LinkedDebe, 0)) > 0.01)
          OR (ISNULL(M.Abono, 0) > 0 AND ABS(ISNULL(M.Abono, 0) - ISNULL(ML.LinkedHaber, 0)) > 0.01)
      )
    ORDER BY
        CASE WHEN ET.Transaccion_ID IS NOT NULL THEN 0 ELSE 1 END,
        ABS(DATEDIFF(DAY, M.Dia, TC.Fecha)),
        M.Dia DESC,
        M.Movimiento_ID DESC
)
SELECT *
FROM Candidates
ORDER BY AlreadyLinkedToTransaction DESC, MatchScore DESC, Dia DESC, MovimientoId DESC;";

    var parameters = new
    {
      TransaccionId = transaccionId,
      Search = search,
      IncludeFullyLinkedMovements = includeFullyLinkedMovements
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    using var multi = await connection.QueryMultipleAsync(
        new CommandDefinition(sql, parameters, cancellationToken: cancellationToken, commandTimeout: 120))
      .ConfigureAwait(false);

    var workspace = new BankTransactionMovementWorkspaceDto
    {
      Summary = await multi.ReadFirstOrDefaultAsync<BankTransactionMovementSummaryDto>().ConfigureAwait(false)
    };

    workspace.Links.AddRange((await multi.ReadAsync<BankMovementDto>().ConfigureAwait(false)).AsList());
    workspace.Candidates.AddRange((await multi.ReadAsync<BankTransactionMovementCandidateDto>().ConfigureAwait(false)).AsList());
    cancellationToken.ThrowIfCancellationRequested();

    return workspace;
  }

  public async Task<TransaccionCommandResult> SaveMovementLinksAsync(
      BankMovementLinkSaveRequest request,
      CancellationToken cancellationToken = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.MovimientoId <= 0)
    {
      return TransaccionCommandResult.Fail("Movimiento bancario inválido.");
    }

    if (request.Links.Count == 0)
    {
      return TransaccionCommandResult.Fail("Agrega al menos una póliza antes de guardar.");
    }

    var duplicate = request.Links
      .GroupBy(link => link.TransaccionId)
      .FirstOrDefault(group => group.Count() > 1);

    if (duplicate is not null)
    {
      return TransaccionCommandResult.Fail($"La póliza {duplicate.Key} está repetida en la asignación.");
    }

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    using var transaction = connection.BeginTransaction();

    try
    {
      var context = await LoadMovementValidationContextAsync(
          connection,
          transaction,
          request.MovimientoId,
          cancellationToken)
        .ConfigureAwait(false);

      if (context is null)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("Movimiento bancario no encontrado.");
      }

      if (!context.MappingValid)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail(context.SetupIssue ?? "La cuenta bancaria no tiene una cuenta contable válida.");
      }

      var validation = ValidateRequestedLinks(request.Links, context);
      if (!validation.Success)
      {
        transaction.Rollback();
        return validation;
      }

      var capacities = await LoadTransactionBankCapacitiesAsync(
          connection,
          transaction,
          context,
          request.Links.Select(link => link.TransaccionId).ToArray(),
          cancellationToken)
        .ConfigureAwait(false);

      foreach (var link in request.Links)
      {
        if (!capacities.TryGetValue(link.TransaccionId, out var capacity))
        {
          transaction.Rollback();
          return TransaccionCommandResult.Fail($"No se encontró la póliza {link.TransaccionId}.");
        }

        if (!string.Equals(capacity.Rfc, context.Rfc, StringComparison.OrdinalIgnoreCase))
        {
          transaction.Rollback();
          return TransaccionCommandResult.Fail($"La póliza {link.TransaccionId} pertenece a otro RFC.");
        }

        var requestedDebe = decimal.Round(link.Debe, 2);
        var requestedHaber = decimal.Round(link.Haber, 2);
        var availableDebe = capacity.BankRegistroDebe - capacity.OtherLinkedDebe;
        var availableHaber = capacity.BankRegistroHaber - capacity.OtherLinkedHaber;

        if (requestedDebe > 0m && requestedDebe > availableDebe + 0.01m)
        {
          transaction.Rollback();
          return TransaccionCommandResult.Fail($"La póliza {link.TransaccionId} solo tiene {availableDebe:C2} disponible en Debe para la cuenta bancaria.");
        }

        if (requestedHaber > 0m && requestedHaber > availableHaber + 0.01m)
        {
          transaction.Rollback();
          return TransaccionCommandResult.Fail($"La póliza {link.TransaccionId} solo tiene {availableHaber:C2} disponible en Haber para la cuenta bancaria.");
        }
      }

      const string deleteSql = @"
DELETE FROM bancos.Movimiento_Transaccion
WHERE Movimiento_ID = @MovimientoId;";

      await connection.ExecuteAsync(
          new CommandDefinition(
            deleteSql,
            new { request.MovimientoId },
            transaction,
            cancellationToken: cancellationToken))
        .ConfigureAwait(false);

      const string insertSql = @"
INSERT INTO bancos.Movimiento_Transaccion
    (Movimiento_ID, Transaccion_ID, Debe, Haber, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
VALUES
    (@MovimientoId, @TransaccionId, @Debe, @Haber, SYSUTCDATETIME(), @Actor, SYSUTCDATETIME(), @Actor);";

      var insertRows = request.Links.Select(link => new
      {
        request.MovimientoId,
        link.TransaccionId,
        Debe = decimal.Round(link.Debe, 2),
        Haber = decimal.Round(link.Haber, 2),
        Actor = NormalizeActor(request.Actor)
      });

      await connection.ExecuteAsync(
          new CommandDefinition(
            insertSql,
            insertRows,
            transaction,
            cancellationToken: cancellationToken))
        .ConfigureAwait(false);

      transaction.Commit();
      return TransaccionCommandResult.Ok("Movimiento bancario ligado correctamente.");
    }
    catch (Exception ex)
    {
      try { transaction.Rollback(); } catch { /* ignored */ }
      return TransaccionCommandResult.Fail($"No se pudo guardar la asignación bancaria: {ex.Message}");
    }
  }

  public async Task<TransaccionCommandResult> UnlinkMovementTransactionAsync(
      long movimientoId,
      int transaccionId,
      CancellationToken cancellationToken = default)
  {
    if (movimientoId <= 0 || transaccionId <= 0)
    {
      return TransaccionCommandResult.Fail("Movimiento o póliza inválidos.");
    }

    const string sql = @"
DELETE FROM bancos.Movimiento_Transaccion
WHERE Movimiento_ID = @MovimientoId
  AND Transaccion_ID = @TransaccionId;";

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var affected = await connection.ExecuteAsync(
        new CommandDefinition(sql, new { MovimientoId = movimientoId, TransaccionId = transaccionId }, cancellationToken: cancellationToken))
      .ConfigureAwait(false);

    return affected > 0
      ? TransaccionCommandResult.Ok("Póliza desligada del movimiento bancario.")
      : TransaccionCommandResult.Fail("No se encontró el vínculo bancario.");
  }

  public async Task<TransaccionCommandResult> FixMovementTransactionBankLineAsync(
      BankMovementAccountingFixRequest request,
      CancellationToken cancellationToken = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    using var transaction = connection.BeginTransaction();

    try
    {
      var context = await LoadMovementValidationContextAsync(
          connection,
          transaction,
          request.MovimientoId,
          cancellationToken)
        .ConfigureAwait(false);

      if (context is null)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("Movimiento bancario no encontrado.");
      }

      if (!context.MappingValid)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail(context.SetupIssue ?? "La cuenta bancaria no tiene una cuenta contable válida.");
      }

      var requestedDebe = decimal.Round(request.Debe, 2);
      var requestedHaber = decimal.Round(request.Haber, 2);
      var itemSideIsInvalid = context.ExpectedDebe > 0m
        ? requestedDebe <= 0m || requestedHaber != 0m || requestedDebe > context.ExpectedDebe + 0.01m
        : requestedHaber <= 0m || requestedDebe != 0m || requestedHaber > context.ExpectedHaber + 0.01m;

      if (itemSideIsInvalid)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("El importe para ajustar la línea bancaria no corresponde al lado contable del movimiento.");
      }

      const string stateSql = @"
SELECT
    T.ID AS TransaccionId,
    T.RFC,
    T.Concepto,
    CAST(ISNULL(SUM(RC.Debe), 0) AS decimal(19,2)) AS TotalDebe,
    CAST(ISNULL(SUM(RC.Haber), 0) AS decimal(19,2)) AS TotalHaber
FROM dbo.Transacciones AS T
LEFT JOIN dbo.Registro_Contable AS RC
    ON RC.TransaccionID = T.ID
WHERE T.ID = @TransaccionId
GROUP BY T.ID, T.RFC, T.Concepto;

SELECT
    RC.id AS RegistroId,
    CAST(RC.Debe AS decimal(19,2)) AS Debe,
    CAST(RC.Haber AS decimal(19,2)) AS Haber
FROM dbo.Registro_Contable AS RC
WHERE RC.TransaccionID = @TransaccionId
  AND RC.Nivel1 = @Nivel1
  AND RC.Nivel2 = @Nivel2
  AND RC.Nivel3 = @Nivel3
ORDER BY RC.id;";

      using var multi = await connection.QueryMultipleAsync(
          new CommandDefinition(
            stateSql,
            new
            {
              request.TransaccionId,
              Nivel1 = context.Nivel1,
              Nivel2 = context.Nivel2,
              Nivel3 = context.Nivel3
            },
            transaction,
            cancellationToken: cancellationToken))
        .ConfigureAwait(false);

      var state = await multi.ReadFirstOrDefaultAsync<TransactionAccountingState>().ConfigureAwait(false);
      var bankRows = (await multi.ReadAsync<BankRegistroRow>().ConfigureAwait(false)).AsList();

      if (state is null)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("Póliza no encontrada.");
      }

      if (!string.Equals(state.Rfc, context.Rfc, StringComparison.OrdinalIgnoreCase))
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("La póliza pertenece a otro RFC.");
      }

      if (bankRows.Count > 1)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail("La póliza tiene múltiples líneas para la cuenta bancaria; ajusta el registro contable manualmente.");
      }

      var currentDebe = bankRows.Count == 0 ? 0m : bankRows[0].Debe;
      var currentHaber = bankRows.Count == 0 ? 0m : bankRows[0].Haber;
      var nextDebeTotal = state.TotalDebe - currentDebe + requestedDebe;
      var nextHaberTotal = state.TotalHaber - currentHaber + requestedHaber;

      if (Math.Abs(nextDebeTotal - nextHaberTotal) > 0.01m)
      {
        transaction.Rollback();
        return TransaccionCommandResult.Fail($"El ajuste dejaría la póliza descuadrada por {Math.Abs(nextDebeTotal - nextHaberTotal):C2}. Abre la póliza y ajusta la contraparte.");
      }

      if (bankRows.Count == 0)
      {
        const string insertSql = @"
INSERT INTO dbo.Registro_Contable
    (TransaccionID, Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber)
VALUES
    (@TransaccionId, @Nivel1, @Nivel2, @Nivel3, @NombreCuenta, @Concepto, @Debe, @Haber);";

        await connection.ExecuteAsync(
            new CommandDefinition(
              insertSql,
              new
              {
                request.TransaccionId,
                Nivel1 = context.Nivel1,
                Nivel2 = context.Nivel2,
                Nivel3 = context.Nivel3,
                NombreCuenta = context.BankAccountDescription,
                Concepto = state.Concepto,
                Debe = requestedDebe,
                Haber = requestedHaber
              },
              transaction,
              cancellationToken: cancellationToken))
          .ConfigureAwait(false);
      }
      else
      {
        const string updateSql = @"
UPDATE dbo.Registro_Contable
SET Debe = @Debe,
    Haber = @Haber,
    Nombre_Cuenta = @NombreCuenta
WHERE id = @RegistroId;";

        await connection.ExecuteAsync(
            new CommandDefinition(
              updateSql,
              new
              {
                bankRows[0].RegistroId,
                Debe = requestedDebe,
                Haber = requestedHaber,
                NombreCuenta = context.BankAccountDescription
              },
              transaction,
              cancellationToken: cancellationToken))
          .ConfigureAwait(false);
      }

      transaction.Commit();
      return TransaccionCommandResult.Ok("Línea bancaria del registro contable actualizada.");
    }
    catch (Exception ex)
    {
      try { transaction.Rollback(); } catch { /* ignored */ }
      return TransaccionCommandResult.Fail($"No se pudo ajustar el registro contable: {ex.Message}");
    }
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
      CoincidenciasExistentes = GetInt32(record, "Coincidencias_Existentes"),
      CambiosSaldoHistorico = GetInt32(record, "Cambios_Saldo_Historico"),
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

    var (startDate, endDate) = BuildMonthRange(year, month);

    const string batchSql = @"
SET NOCOUNT ON;

DECLARE @Candidates TABLE
(
    RowNumber int NOT NULL PRIMARY KEY,
    MovimientoId bigint NOT NULL,
    Dia datetime2(7) NOT NULL,
    Concepto nvarchar(max) NULL,
    Tipo varchar(10) NULL,
    Cargo decimal(18,2) NOT NULL,
    Abono decimal(18,2) NOT NULL,
    CuentaBancoId int NOT NULL
);

INSERT INTO @Candidates (RowNumber, MovimientoId, Dia, Concepto, Tipo, Cargo, Abono, CuentaBancoId)
SELECT
    Candidates.RowNumber,
    Candidates.MovimientoId,
    Candidates.Dia,
    Candidates.Concepto,
    Candidates.Tipo,
    Candidates.Cargo,
    Candidates.Abono,
    Candidates.CuentaBancoId
FROM (
    SELECT
        ROW_NUMBER() OVER (ORDER BY M.Dia, M.Movimiento_ID) AS RowNumber,
        M.Movimiento_ID AS MovimientoId,
        M.Dia,
        M.Concepto,
        M.Tipo,
        M.Cargo,
        M.Abono,
        M.Cuenta_Banco_ID AS CuentaBancoId
    FROM bancos.Movimientos AS M
    WHERE M.RFC = @Rfc
      AND M.Dia >= @StartDate
      AND M.Dia < @EndDate
      AND (@AccountId IS NULL OR M.Cuenta_Banco_ID = @AccountId)
      AND NOT EXISTS (
          SELECT 1
          FROM bancos.Movimiento_Transaccion AS MT
          WHERE MT.Movimiento_ID = M.Movimiento_ID
      )
) AS Candidates;

DECLARE @CurrentRow int = 1;
DECLARE @MaxRow int;
DECLARE @Processed int = 0;

SELECT @MaxRow = COUNT(*)
FROM @Candidates;

IF EXISTS
(
    SELECT 1
    FROM @Candidates AS candidates
    LEFT JOIN bancos.Cuentas_Banco AS bankAccount
        ON bankAccount.Cuenta_Banco_ID = candidates.CuentaBancoId
       AND bankAccount.RFC = @Rfc
    LEFT JOIN dbo.CuentasContables AS bankLedger
        ON bankLedger.id = bankAccount.Cuenta_Contable_ID
       AND bankLedger.RFC = @Rfc
    WHERE bankLedger.id IS NULL
)
    THROW 50020, 'Configura la cuenta contable general de cada banco antes de crear polizas automaticas.', 1;

IF EXISTS (SELECT 1 FROM @Candidates WHERE UPPER(ISNULL(Tipo, '')) = 'E')
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.CfdiPolizaCuentaDefault AS defaults
       JOIN dbo.CuentasContables AS account
         ON account.id = defaults.CuentaContableId
        AND account.RFC = @Rfc
       WHERE defaults.Rfc = @Rfc
         AND defaults.CuentaClave = 'SUBTOTAL_GASTO'
   )
    THROW 50021, 'Configura Subtotal gasto en Ajustes > Cuentas contables CFDI antes de crear polizas automaticas.', 1;

IF EXISTS (SELECT 1 FROM @Candidates WHERE UPPER(ISNULL(Tipo, '')) = 'I')
   AND NOT EXISTS
   (
       SELECT 1
       FROM dbo.CfdiPolizaCuentaDefault AS defaults
       JOIN dbo.CuentasContables AS account
         ON account.id = defaults.CuentaContableId
        AND account.RFC = @Rfc
       WHERE defaults.Rfc = @Rfc
         AND defaults.CuentaClave = 'SUBTOTAL_INGRESO'
   )
    THROW 50022, 'Configura Subtotal ingreso en Ajustes > Cuentas contables CFDI antes de crear polizas automaticas.', 1;

DECLARE @MovimientoId bigint;
DECLARE @Dia datetime2(7);
DECLARE @Concepto nvarchar(max);
DECLARE @Tipo varchar(10);
DECLARE @Cargo decimal(18,2);
DECLARE @Abono decimal(18,2);
DECLARE @CuentaBancoId int;
DECLARE @Monto decimal(18,2);
DECLARE @TransaccionId int;

WHILE @CurrentRow <= @MaxRow
BEGIN
    SELECT
        @MovimientoId = MovimientoId,
        @Dia = Dia,
        @Concepto = Concepto,
        @Tipo = Tipo,
        @Cargo = Cargo,
        @Abono = Abono,
        @CuentaBancoId = CuentaBancoId
    FROM @Candidates
    WHERE RowNumber = @CurrentRow;

    SET @Monto = CASE
        WHEN UPPER(ISNULL(@Tipo, '')) = 'I' THEN ISNULL(@Cargo, 0)
        ELSE ISNULL(@Abono, 0)
    END;
    SET @TransaccionId = NULL;

    EXEC dbo.Crear_Transaccion_Contable_Banco
        @RFC = @Rfc,
        @Fecha = @Dia,
        @Concepto = @Concepto,
        @Tipo = @Tipo,
        @Monto = @Monto,
        @CuentaBancoID = @CuentaBancoId,
        @TransaccionID = @TransaccionId OUTPUT;

    IF @TransaccionId IS NOT NULL AND @TransaccionId > 0
    BEGIN
        INSERT INTO bancos.Movimiento_Transaccion
            (Movimiento_ID, Transaccion_ID, Debe, Haber, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
        SELECT
            @MovimientoId,
            @TransaccionId,
            CASE WHEN UPPER(ISNULL(@Tipo, '')) = 'I' THEN ISNULL(@Cargo, 0) ELSE 0 END,
            CASE WHEN UPPER(ISNULL(@Tipo, '')) = 'I' THEN 0 ELSE ISNULL(@Abono, 0) END,
            SYSUTCDATETIME(),
            N'auto-polizas',
            SYSUTCDATETIME(),
            N'auto-polizas'
        WHERE NOT EXISTS (
            SELECT 1
            FROM bancos.Movimiento_Transaccion AS MT
            WHERE MT.Movimiento_ID = @MovimientoId
              AND MT.Transaccion_ID = @TransaccionId
        );

        SET @Processed += @@ROWCOUNT;
    END

    SET @CurrentRow += 1;
END

SELECT @Processed;";

    var parameters = new
    {
      Rfc = rfc,
      StartDate = startDate,
      EndDate = endDate,
      AccountId = accountId
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var processed = await connection.QuerySingleAsync<int>(
        new CommandDefinition(batchSql, parameters, cancellationToken: cancellationToken, commandTimeout: 120))
      .ConfigureAwait(false);

    return processed;
  }

  public async Task<int> AlignTransactionsToBankMovementsAsync(
      string rfc,
      int year,
      int month,
      int accountId,
      CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace(rfc))
    {
      return 0;
    }

    if (accountId <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    var (startDate, endDate) = BuildMonthRange(year, month);

    const string sql = @"
SET NOCOUNT ON;

;WITH LinkedMovements AS
(
    SELECT
        MT.Transaccion_ID AS TransaccionId,
        CAST(M.Dia AS date) AS BankDate,
        COALESCE(
            M.Secuencia_Clave,
            CAST(
                CONVERT(char(8), M.Dia, 112)
                + RIGHT(
                    '0000' + CAST(
                        COALESCE(
                            M.Secuencia_Diaria,
                            ROW_NUMBER() OVER (
                                PARTITION BY M.Dia
                                ORDER BY M.Movimiento_ID
                            )
                        ) AS varchar(10)
                    ),
                    4
                )
            AS bigint)
        ) AS BankOrdenBalance,
        ROW_NUMBER() OVER (
            PARTITION BY MT.Transaccion_ID
            ORDER BY
                COALESCE(M.Secuencia_Clave, CAST(CONVERT(char(8), M.Dia, 112) + '0000' AS bigint)),
                M.Movimiento_ID
        ) AS MovementRank
    FROM bancos.Movimientos AS M
    INNER JOIN bancos.Movimiento_Transaccion AS MT
        ON MT.Movimiento_ID = M.Movimiento_ID
    INNER JOIN dbo.Transacciones AS T
        ON T.ID = MT.Transaccion_ID
       AND T.RFC = @Rfc
    WHERE M.RFC = @Rfc
      AND M.Cuenta_Banco_ID = @AccountId
      AND M.Dia >= @StartDate
      AND M.Dia < @EndDate
),
Alignment AS
(
    SELECT
        TransaccionId,
        BankDate,
        BankOrdenBalance
    FROM LinkedMovements
    WHERE MovementRank = 1
)
UPDATE T
SET
    T.Fecha = CAST(A.BankDate AS datetime),
    T.OrdenBalance = A.BankOrdenBalance
FROM dbo.Transacciones AS T
INNER JOIN Alignment AS A
    ON A.TransaccionId = T.ID
WHERE T.RFC = @Rfc
  AND (
      T.Fecha <> CAST(A.BankDate AS datetime)
      OR T.OrdenBalance <> A.BankOrdenBalance
  );

SELECT @@ROWCOUNT;";

    var parameters = new
    {
      Rfc = rfc,
      StartDate = startDate,
      EndDate = endDate,
      AccountId = accountId
    };

    using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    var aligned = await connection.QuerySingleAsync<int>(
        new CommandDefinition(sql, parameters, cancellationToken: cancellationToken, commandTimeout: 120))
      .ConfigureAwait(false);

    return aligned;
  }

  private static TransaccionCommandResult ValidateRequestedLinks(
      IEnumerable<BankMovementLinkSaveItem> links,
      BankMovementValidationContext context)
  {
    var roundedLinks = links
      .Select(link => new
      {
        link.TransaccionId,
        Debe = decimal.Round(link.Debe, 2),
        Haber = decimal.Round(link.Haber, 2)
      })
      .ToList();

    if (context.ExpectedDebe > 0m)
    {
      if (roundedLinks.Any(link => link.Haber != 0m || link.Debe <= 0m))
      {
        return TransaccionCommandResult.Fail("Este movimiento es un cargo bancario; asigna importes solo en Debe.");
      }

      var requestedDebe = roundedLinks.Sum(link => link.Debe);
      if (Math.Abs(requestedDebe - context.ExpectedDebe) > 0.01m)
      {
        return TransaccionCommandResult.Fail($"La suma asignada en Debe debe ser exactamente {context.ExpectedDebe:C2}.");
      }
    }
    else if (context.ExpectedHaber > 0m)
    {
      if (roundedLinks.Any(link => link.Debe != 0m || link.Haber <= 0m))
      {
        return TransaccionCommandResult.Fail("Este movimiento es un abono/salida bancaria; asigna importes solo en Haber.");
      }

      var requestedHaber = roundedLinks.Sum(link => link.Haber);
      if (Math.Abs(requestedHaber - context.ExpectedHaber) > 0.01m)
      {
        return TransaccionCommandResult.Fail($"La suma asignada en Haber debe ser exactamente {context.ExpectedHaber:C2}.");
      }
    }
    else
    {
      return TransaccionCommandResult.Fail("El movimiento bancario no tiene importe en cargo ni abono.");
    }

    return TransaccionCommandResult.Ok("Validación correcta.");
  }

  private static string? NormalizeActor(string? actor)
    => string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();

  private static async Task<BankMovementValidationContext?> LoadMovementValidationContextAsync(
      IDbConnection connection,
      IDbTransaction transaction,
      long movimientoId,
      CancellationToken cancellationToken)
  {
    const string sql = @"
SELECT
    M.Movimiento_ID AS MovimientoId,
    ISNULL(M.RFC, '') AS Rfc,
    M.Cuenta_Banco_ID AS CuentaBancoId,
    CAST(CASE WHEN ISNULL(M.Cargo, 0) > 0 THEN ABS(M.Cargo) ELSE 0 END AS decimal(19,2)) AS ExpectedDebe,
    CAST(CASE WHEN ISNULL(M.Abono, 0) > 0 THEN ABS(M.Abono) ELSE 0 END AS decimal(19,2)) AS ExpectedHaber,
    CB.Cuenta_Contable_ID AS CuentaContableId,
    ISNULL(CC.Nivel1, '') AS Nivel1,
    ISNULL(CC.Nivel2, '') AS Nivel2,
    ISNULL(CC.Nivel3, '') AS Nivel3,
    ISNULL(CC.Descripcion, '') AS BankAccountDescription,
    CAST(CASE WHEN CB.Cuenta_Contable_ID IS NOT NULL
               AND ISNULL(CC.Nivel1, '') <> ''
               AND ISNULL(CC.Nivel2, '') <> ''
               AND ISNULL(CC.Nivel3, '') <> ''
         THEN 1 ELSE 0 END AS bit) AS MappingValid,
    CASE WHEN CB.Cuenta_Contable_ID IS NULL THEN N'La cuenta bancaria no tiene Cuenta_Contable_ID.'
         WHEN ISNULL(CC.Nivel1, '') = '' OR ISNULL(CC.Nivel2, '') = '' OR ISNULL(CC.Nivel3, '') = '' THEN N'La Cuenta_Contable_ID de bancos.Cuentas_Banco no resuelve a CuentasContables para este RFC.'
         ELSE NULL END AS SetupIssue
FROM bancos.Movimientos AS M
INNER JOIN bancos.Cuentas_Banco AS CB
    ON CB.Cuenta_Banco_ID = M.Cuenta_Banco_ID
LEFT JOIN dbo.CuentasContables AS CC
    ON CC.id = CB.Cuenta_Contable_ID
   AND CC.RFC = CB.RFC
WHERE M.Movimiento_ID = @MovimientoId;";

    return await connection.QueryFirstOrDefaultAsync<BankMovementValidationContext>(
        new CommandDefinition(sql, new { MovimientoId = movimientoId }, transaction, cancellationToken: cancellationToken))
      .ConfigureAwait(false);
  }

  private static async Task<Dictionary<int, TransactionBankCapacity>> LoadTransactionBankCapacitiesAsync(
      IDbConnection connection,
      IDbTransaction transaction,
      BankMovementValidationContext context,
      IReadOnlyCollection<int> transaccionIds,
      CancellationToken cancellationToken)
  {
    if (transaccionIds.Count == 0)
    {
      return new Dictionary<int, TransactionBankCapacity>();
    }

    const string sql = @"
SELECT
    T.ID AS TransaccionId,
    T.RFC,
    CAST(ISNULL(RB.BankRegistroDebe, 0) AS decimal(19,2)) AS BankRegistroDebe,
    CAST(ISNULL(RB.BankRegistroHaber, 0) AS decimal(19,2)) AS BankRegistroHaber,
    CAST(ISNULL(OL.OtherLinkedDebe, 0) AS decimal(19,2)) AS OtherLinkedDebe,
    CAST(ISNULL(OL.OtherLinkedHaber, 0) AS decimal(19,2)) AS OtherLinkedHaber
FROM dbo.Transacciones AS T
OUTER APPLY (
    SELECT
        SUM(RC.Debe) AS BankRegistroDebe,
        SUM(RC.Haber) AS BankRegistroHaber
    FROM dbo.Registro_Contable AS RC
    WHERE RC.TransaccionID = T.ID
      AND RC.Nivel1 = @Nivel1
      AND RC.Nivel2 = @Nivel2
      AND RC.Nivel3 = @Nivel3
) AS RB
OUTER APPLY (
    SELECT
        SUM(MT.Debe) AS OtherLinkedDebe,
        SUM(MT.Haber) AS OtherLinkedHaber
    FROM bancos.Movimiento_Transaccion AS MT
    WHERE MT.Transaccion_ID = T.ID
      AND MT.Movimiento_ID <> @MovimientoId
) AS OL
WHERE T.ID IN @TransaccionIds;";

    var rows = await connection.QueryAsync<TransactionBankCapacity>(
        new CommandDefinition(
          sql,
          new
          {
            Nivel1 = context.Nivel1,
            Nivel2 = context.Nivel2,
            Nivel3 = context.Nivel3,
            MovimientoId = context.MovimientoId,
            TransaccionIds = transaccionIds
          },
          transaction,
          cancellationToken: cancellationToken))
      .ConfigureAwait(false);

    return rows.ToDictionary(row => row.TransaccionId);
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
    var sql = AccountSelectSql + "WHERE cb.Cuenta_Banco_ID = @CuentaBancoId;";
    var account = await connection.QuerySingleOrDefaultAsync<BankAccountDto>(sql, new { CuentaBancoId = accountId })
        .ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return account;
  }

  private static (DateTime StartDate, DateTime EndDate) BuildMonthRange(int year, int month)
  {
    var startDate = new DateTime(year, month, 1);
    return (startDate, startDate.AddMonths(1));
  }

  private sealed class BankMovementValidationContext
  {
    public long MovimientoId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public int CuentaBancoId { get; set; }
    public decimal ExpectedDebe { get; set; }
    public decimal ExpectedHaber { get; set; }
    public int? CuentaContableId { get; set; }
    public string Nivel1 { get; set; } = string.Empty;
    public string Nivel2 { get; set; } = string.Empty;
    public string Nivel3 { get; set; } = string.Empty;
    public string BankAccountDescription { get; set; } = string.Empty;
    public bool MappingValid { get; set; }
    public string? SetupIssue { get; set; }
  }

  private sealed class TransactionBankCapacity
  {
    public int TransaccionId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public decimal BankRegistroDebe { get; set; }
    public decimal BankRegistroHaber { get; set; }
    public decimal OtherLinkedDebe { get; set; }
    public decimal OtherLinkedHaber { get; set; }
  }

  private sealed class TransactionAccountingState
  {
    public int TransaccionId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string Concepto { get; set; } = string.Empty;
    public decimal TotalDebe { get; set; }
    public decimal TotalHaber { get; set; }
  }

  private sealed class BankRegistroRow
  {
    public int RegistroId { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
  }
}
