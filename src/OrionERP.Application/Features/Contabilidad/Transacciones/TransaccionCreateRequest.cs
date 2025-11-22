using System;
using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public class TransaccionCreateRequest
{
    [Required]
    public string Rfc { get; set; }

    [Required]
    public DateTime Fecha { get; set; }

    [Required]
    public string Concepto { get; set; }

    public decimal Monto { get; set; }

    [Required]
    public string TipoPoliza { get; set; }

    [Required]
    public string FormaPago { get; set; }

    [Required]
    public int CategoriaId { get; set; }

    public bool Facturado { get; set; }
    public string? Memo { get; set; }
    public int? ProyectoId { get; set; }
    public int? CompraId { get; set; }
    public int? ServicioId { get; set; }
    public int? NominaId { get; set; }
    public string? Cuenta { get; set; }
}
