namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed record ProcessBbvaResult
{
  public int Insertados { get; init; }
  public int Actualizados { get; init; }
  public int CuentaBancoId { get; init; }
  public string NombreBanco { get; init; } = string.Empty;
  public string NumeroCuenta { get; init; } = string.Empty;
  public string ArchivoHash { get; init; } = string.Empty;
  public int BalanceWarnings { get; init; }
  public int CoincidenciasExistentes { get; init; }
  public int CambiosSaldoHistorico { get; init; }
}
