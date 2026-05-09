using System;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed record BankMovementDto
{
  public long MovimientoId { get; init; }
  public DateTime Dia { get; init; }
  public int Line { get; init; }
  public string Concepto { get; init; } = string.Empty;
  public string Tipo { get; init; } = string.Empty;
  public decimal Cargo { get; init; }
  public decimal Abono { get; init; }
  public decimal Saldo { get; init; }
  public DateTime FechaCarga { get; init; }
  public string NombreBanco { get; init; } = string.Empty;
  public string NumeroCuenta { get; init; } = string.Empty;
  public long SecuenciaClave { get; init; }
  public int? Policy { get; init; }
  public string Issues { get; init; } = "OK";
  public DateTime? PolicyDate { get; init; }
  public long? OrdenBalance { get; init; }
  public int? AccountingSequence { get; init; }
  public string BankAccountNivel1 { get; init; } = string.Empty;
  public string BankAccountNivel2 { get; init; } = string.Empty;
  public string BankAccountNivel3 { get; init; } = string.Empty;
  public int BankRegistroLineCount { get; init; }
  public decimal BankRegistroDebe { get; init; }
  public decimal BankRegistroHaber { get; init; }
  public decimal? AccountingRunningBalance { get; init; }
  public decimal? BankAccountingVariance { get; init; }
  public bool HasBankAccountingDifference { get; init; }
  public bool? BalanceOk { get; init; }
  public string AuditSeverity { get; init; } = "OK";
}
