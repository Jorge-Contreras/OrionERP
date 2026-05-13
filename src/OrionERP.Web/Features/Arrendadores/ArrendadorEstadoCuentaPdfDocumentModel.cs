namespace OrionERP.Web.Features.Arrendadores;

public sealed record ArrendadorEstadoCuentaPdfDocumentModel(
  string Arrendador,
  string Propiedad,
  string Periodo,
  string GeneratedAt,
  string NochesOcupadas,
  string Cobrado,
  string Arrendador30,
  string Isr10,
  string PagoFinal,
  IReadOnlyList<ArrendadorEstadoCuentaPdfDetalleRow> Details,
  IReadOnlyList<ArrendadorEstadoCuentaPdfExclusionRow> Exclusions);

public sealed record ArrendadorEstadoCuentaPdfDetalleRow(
  string Noche,
  string Huesped,
  string ReservationId,
  string CheckIn,
  string CheckOut,
  string Cobrado,
  string Arrendador30,
  string Isr10,
  string PagoFinal);

public sealed record ArrendadorEstadoCuentaPdfExclusionRow(
  string Noche,
  string Huesped,
  string ReservationId,
  string Cobrado,
  string Motivo);
