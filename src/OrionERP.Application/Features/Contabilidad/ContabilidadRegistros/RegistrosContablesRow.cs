namespace OrionERP.Application.Features.Contabilidad.ContabilidadRegistros;

public sealed record RegistrosContablesRow
{
  public int Id { get; init; }
  public DateTime FechaOrden { get; init; }
  public long OrdenBalance { get; init; }
  public string Fecha { get; init; } = string.Empty;
  public string Cuenta { get; init; } = string.Empty;
  public string Nombre_Cuenta { get; init; } = string.Empty;
  public string Concepto { get; init; } = string.Empty;
  public string Debe { get; init; } = string.Empty;
  public string Haber { get; init; } = string.Empty;
  public string Balance { get; init; } = string.Empty;
  public int Poliza { get; init; }
  public int CfdiCount { get; init; }
  public string Revisado { get; init; } = string.Empty;
  public string Referencia { get; init; } = string.Empty;
}
