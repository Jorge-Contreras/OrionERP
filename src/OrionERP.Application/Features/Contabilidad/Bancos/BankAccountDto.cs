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
  public int? CuentaContableId { get; init; }
  public int? CuentaContableEgreso { get; init; }
  public int? CuentaContableIngreso { get; init; }
  public string? CuentaContableNivel1 { get; init; }
  public string? CuentaContableNivel2 { get; init; }
  public string? CuentaContableNivel3 { get; init; }
  public string? CuentaContableDescripcion { get; init; }
}
