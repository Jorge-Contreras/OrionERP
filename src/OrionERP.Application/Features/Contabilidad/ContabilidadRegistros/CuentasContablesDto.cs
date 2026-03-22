namespace OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;

public sealed record CuentasContablesDto
{
  public int Id { get; init; }
  public string Rfc { get; init; } = string.Empty;
  public string Nivel1 { get; init; } = string.Empty;
  public string Nivel2 { get; init; } = string.Empty;
  public string Nivel3 { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public int? Nivel1Id { get; init; }
  public int? Nivel2Id { get; init; }
  public string? Nivel1Descripcion { get; init; }
  public string? Nivel2Descripcion { get; init; }
  public string? Nivel3Descripcion { get; init; }
}
