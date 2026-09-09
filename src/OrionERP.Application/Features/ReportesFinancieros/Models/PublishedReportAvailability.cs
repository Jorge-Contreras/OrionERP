namespace OrionERP.Application.Features.ReportesFinancieros.Models;

/// <summary>
/// Qué tan adoptable es el reporte de pólizas publicadas para una empresa. Mientras
/// el ciclo de E4 esté apagado, la variante publicada saldría en cero: ofrecerla sin
/// decirlo sería presentar la historia como si no existiera.
/// </summary>
/// <param name="IsAvailable">La empresa tiene el ciclo encendido y hay asientos publicados.</param>
/// <param name="CycleInstalled">El ciclo existe en esta base de datos.</param>
/// <param name="CycleEnabled">La empresa tiene su activación encendida.</param>
/// <param name="PublishedEntryCount">Asientos publicados o reversados de la empresa.</param>
/// <param name="TotalEntryCount">Asientos totales de la empresa, publicados o no.</param>
/// <param name="MissingToAdopt">Qué falta para adoptar el reporte nuevo como oficial.</param>
public sealed record PublishedReportAvailability(
  bool IsAvailable,
  bool CycleInstalled,
  bool CycleEnabled,
  long PublishedEntryCount,
  long TotalEntryCount,
  string MissingToAdopt);
