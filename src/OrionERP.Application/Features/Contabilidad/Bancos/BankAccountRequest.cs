namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed record BankAccountRequest
{
  public string NombreBanco { get; init; } = string.Empty;
  public string NumeroCuenta { get; init; } = string.Empty;
  public string? TipoCuenta { get; init; }
  public string? NombreTitular { get; init; }
  public string? ClabeCuenta { get; init; }
  public string Rfc { get; init; } = string.Empty;
  public bool Activo { get; init; }
  public int? CuentaContableId { get; init; }
  public int? CuentaContableEgreso { get; init; }
  public int? CuentaContableIngreso { get; init; }
}
