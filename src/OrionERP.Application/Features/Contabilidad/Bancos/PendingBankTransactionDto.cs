using System;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed class PendingBankTransactionDto
{
  public int TransaccionId { get; init; }
  public DateTime Fecha { get; init; }
  public string FormaPago { get; init; } = string.Empty;
  public string Concepto { get; init; } = string.Empty;
  public decimal Monto { get; init; }
}
