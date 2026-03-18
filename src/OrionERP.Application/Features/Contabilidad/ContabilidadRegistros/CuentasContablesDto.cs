namespace OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;

public sealed record CuentasContablesDto
{
  public int Id { get; init; }
  public string Rfc { get; init; } = string.Empty;
  public string Nivel1 { get; init; } = string.Empty;
  public string Nivel2 { get; init; } = string.Empty;
  public string Nivel3 { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
}
