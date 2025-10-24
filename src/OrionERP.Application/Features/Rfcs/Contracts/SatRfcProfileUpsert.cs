using System;

namespace OrionERP.Application.Features.Rfcs.Contracts
{
  public sealed class SatRfcProfileUpsert
  {
    public string Rfc { get; set; } = default!;
    public string? RazonSocial { get; set; }
    public string? NombreComercial { get; set; }
    public string? RegimenCapital { get; set; }

    public DateTime? FechaInicioOperaciones { get; set; }
    public string? EstatusPadron { get; set; }
    public DateTime? FechaUltCambioEstatus { get; set; }
    public DateTime? EmisionFecha { get; set; }

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? Municipio { get; set; }
    public string? EntidadFederativa { get; set; }
    public string? CodigoPostal { get; set; }

    public string? CsfDataJson { get; set; }

    public byte[]? SATFielCertificate { get; set; }
    public byte[]? SATFielKey { get; set; }
    public byte[]? SATFielPfx { get; set; }
    public byte[]? SATFielPasswordEnc { get; set; }

    public string? Email { get; set; }
  }
}
