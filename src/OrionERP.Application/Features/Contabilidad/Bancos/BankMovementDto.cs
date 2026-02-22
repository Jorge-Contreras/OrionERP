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
}
