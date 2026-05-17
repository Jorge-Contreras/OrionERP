using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Web.Features.ReportesFinancieros.SaludEmpresa;

public sealed record SaludEmpresaPdfDocumentModel(
  string Rfc,
  DateTime PeriodStart,
  DateTime PeriodEnd,
  DateTime GeneratedAt,
  SaludEmpresaReport Report);
