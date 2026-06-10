using System;
using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public class TransaccionCreateRequest
{
    [Required]
    public string Rfc { get; set; } = string.Empty;

    [Required]
    public DateTime Fecha { get; set; }

    [Required]
    public string Concepto { get; set; } = string.Empty;

    public decimal Monto { get; set; }

    [Required]
    public string TipoPoliza { get; set; } = string.Empty;

    [Required]
    public string FormaPago { get; set; } = string.Empty;

    public bool Facturado { get; set; }
    public string? Memo { get; set; }
    public int? ProyectoId { get; set; }
    public int? CompraId { get; set; }
    public int? ServicioId { get; set; }
    public int? NominaId { get; set; }
    public string? Cuenta { get; set; }
}
