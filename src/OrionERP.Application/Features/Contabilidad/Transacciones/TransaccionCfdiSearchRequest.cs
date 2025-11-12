namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionCfdiSearchRequest
{
  public string Rfc { get; init; } = string.Empty;
  public decimal? Monto { get; init; }
  public string? Concepto { get; init; }
  public long? ComprobanteId { get; init; }
  public string? Tipo { get; init; }
  public int Renglones { get; init; } = 25;
  public string? ComprobantesCsv { get; init; }
}
