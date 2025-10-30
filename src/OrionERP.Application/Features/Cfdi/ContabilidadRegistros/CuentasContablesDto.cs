namespace OrionERP.Application.Features.Cfdi.ContabilidadRegistros;

public sealed record CuentasContablesDto
{
  public int Id { get; init; }
  public string RazonSocial { get; init; } = string.Empty;
  public string Nivel1 { get; init; } = string.Empty;
  public string Nivel2 { get; init; } = string.Empty;
  public string Nivel3 { get; init; } = string.Empty;
  public string Descripcion { get; init; } = string.Empty;
}
