namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones;

/// <summary>
/// Predicados del ciclo, en un solo lugar para que la lectura y la publicación
/// atómica no puedan discrepar. El cierre que evalúan es
/// <c>contabilidad.AccountingPeriod</c>, distinto de <c>fiscal.DeclaracionCierre</c>,
/// que es cierre declarativo de impuestos y no interviene aquí.
/// </summary>
internal static class AccountingCycleSql
{
  /// <summary>
  /// El ciclo puede no estar instalado todavía en una base dada. Su ausencia es el
  /// estado más apagado que existe, y E4 exige que apagado no cambie la operación:
  /// los escritores lo consultan y siguen como hoy si falta.
  /// </summary>
  public const string InstalledSql = """
    SELECT CONVERT(bit, CASE
      WHEN OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NULL
        OR OBJECT_ID(N'contabilidad.AccountingPeriod', N'U') IS NULL
        OR COL_LENGTH(N'dbo.Transacciones', N'CycleState') IS NULL
      THEN 0 ELSE 1 END);
    """;

  /// <summary>Lectura sin candados, para mostrar estado.</summary>
  public static string Status() => Build(locking: false);

  /// <summary>
  /// La misma verdad, tomando los candados que una escritura necesita: la póliza
  /// bajo <c>UPDLOCK</c> y el periodo bajo <c>HOLDLOCK</c>, para que un cierre
  /// concurrente espere al commit en vez de colarse entre la lectura y la escritura.
  /// </summary>
  public static string LockedStatus() => Build(locking: true);

  private static string Build(bool locking)
  {
    var polizaHint = locking ? " WITH (UPDLOCK, HOLDLOCK)" : string.Empty;
    var readHint = locking ? " WITH (HOLDLOCK)" : string.Empty;
    return $"""
      SELECT
        poliza.ID AS TransaccionId,
        poliza.CompanyId,
        poliza.CycleState AS [State],
        poliza.Fecha,
        poliza.PostedAtUtc,
        poliza.PostedBy,
        poliza.ReversalOfTransaccionId,
        (SELECT TOP (1) reversa.ID FROM dbo.Transacciones AS reversa{readHint}
         WHERE reversa.ReversalOfTransaccionId = poliza.ID) AS ReversedByTransaccionId,
        poliza.ReversalReason,
        CONVERT(bit, CASE WHEN activacion.IsEnabled = 1 THEN 1 ELSE 0 END) AS CycleEnabled,
        CONVERT(bit, CASE WHEN activacion.LegacyCompatibleUntilUtc IS NOT NULL
                           AND poliza.Fecha < activacion.LegacyCompatibleUntilUtc
                          THEN 1 ELSE 0 END) AS LegacyCompatible,
        CONVERT(bit, CASE WHEN EXISTS
        (
          SELECT 1 FROM contabilidad.AccountingPeriod AS periodo{readHint}
          WHERE periodo.CompanyId = poliza.CompanyId
            AND periodo.PeriodYear = YEAR(poliza.Fecha)
            AND periodo.PeriodMonth = MONTH(poliza.Fecha)
            AND periodo.[State] = 'Closed'
        ) THEN 1 ELSE 0 END) AS PeriodClosed
      FROM dbo.Transacciones AS poliza{polizaHint}
      LEFT JOIN contabilidad.CompanyCycleActivation AS activacion{readHint}
        ON activacion.CompanyId = poliza.CompanyId
      WHERE poliza.ID = @TransaccionId;
      """;
  }
}
