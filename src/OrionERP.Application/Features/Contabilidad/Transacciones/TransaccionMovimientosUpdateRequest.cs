using System.Collections.Generic;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionMovimientosUpdateRequest
{
    public int TransaccionId { get; set; }
    public List<TransaccionMovimientoUpdateItem> Movimientos { get; set; } = new();
}

public sealed class TransaccionMovimientoUpdateItem
{
    public int Id { get; set; }
    public int? CuentaId { get; set; }
    public string? NombreCuenta { get; set; }
    public string? Concepto { get; set; }
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
}
