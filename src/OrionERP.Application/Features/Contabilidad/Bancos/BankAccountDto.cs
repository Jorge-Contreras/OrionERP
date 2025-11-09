using System;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed record BankAccountDto
{
  public int CuentaBancoId { get; init; }
  public string NombreBanco { get; init; } = string.Empty;
  public string NumeroCuenta { get; init; } = string.Empty;
  public string TipoCuenta { get; init; } = string.Empty;
  public string NombreTitular { get; init; } = string.Empty;
  public string? ClabeCuenta { get; init; }
  public string Rfc { get; init; } = string.Empty;
  public bool Activo { get; init; }
  public DateTime FechaAlta { get; init; }
}
