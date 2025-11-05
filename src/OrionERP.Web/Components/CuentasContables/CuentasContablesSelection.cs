namespace OrionERP.Web.Components.CuentasContables;

public sealed record CuentasContablesSelection
{
  public int? Id { get; init; }
  public string? Rfc { get; init; }
  public string? Nivel1 { get; init; }
  public string? Nivel2 { get; init; }
  public string? Nivel3 { get; init; }
  public string? Descripcion { get; init; }
}
