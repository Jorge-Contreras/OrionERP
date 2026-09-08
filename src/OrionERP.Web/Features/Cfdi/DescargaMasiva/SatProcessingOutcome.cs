using OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts;

namespace OrionERP.Web.Features.Cfdi.DescargaMasiva;

/// <summary>
/// Decide si una corrida de descarga/procesamiento del SAT quedó completa. Solo cuando lo está
/// se cierra la solicitud (Procesada / terminada); de lo contrario debe permanecer abierta para
/// no dejar CFDIs fiscales fuera del sistema sin posibilidad de reintento.
/// </summary>
public static class SatProcessingOutcome
{
  public static int ProcessedFiles(ProcessSummary summary)
    => summary.Xmls + summary.MetaFiles;

  public static int Failures(ProcessSummary summary)
    => summary.Fail + summary.MetaFail;

  /// <summary>El SAT aún no libera paquetes: hay que reintentar más tarde.</summary>
  public static bool NoPackagesYet(ProcessSummary summary)
    => summary.Packages == 0;

  /// <summary>Todos los paquetes se descargaron y cada comprobante se procesó sin error.</summary>
  public static bool CompletedCleanly(ProcessSummary summary)
    => summary.Packages > 0 && Failures(summary) == 0 && ProcessedFiles(summary) > 0;
}
