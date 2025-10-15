namespace OrionERP.Application.Features.Cfdi.CargarXmlSat.Contracts
{
  public sealed class TransaccionListItem
  {
    public int Id { get; init; }
    public string? Concepto { get; init; }
    public DateTime Fecha { get; init; }
    public decimal Monto1 { get; init; }          // ABS(Monto)
    public string? Cuenta { get; init; }
    public int Adjuntos { get; init; }            // COUNT(TRANSACTION_ATTACHMENT.ID)
    public int? ComprobanteId { get; init; }      // joined Comprobante.Comprobante_Id if already linked
  }
}
