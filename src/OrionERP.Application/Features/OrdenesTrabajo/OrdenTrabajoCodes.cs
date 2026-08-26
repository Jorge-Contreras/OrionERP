namespace OrionERP.Application.Features.OrdenesTrabajo;

public static class OrdenTrabajoCodes
{
  public const string CategoriaLimpieza = "LIMPIEZA";
  public const string CategoriaMantenimiento = "MANTENIMIENTO";
  public const string CategoriaChecklist = "CHECKLIST";
  public const string CategoriaServicio = "SERVICIO";

  public const string EstadoBorrador = "BORRADOR";
  public const string EstadoAsignada = "ASIGNADA";
  public const string EstadoEnProceso = "EN_PROCESO";
  public const string EstadoEnRevision = "EN_REVISION";
  public const string EstadoCerrada = "CERRADA";
  public const string EstadoCancelada = "CANCELADA";
  public const string EstadoRechazada = "RECHAZADA";

  public const string PasoPendiente = "PENDIENTE";
  public const string PasoHecho = "HECHO";
  public const string PasoIncidencia = "INCIDENCIA";
  public const string PasoNoAplica = "NO_APLICA";

  public const string PlantillaBorrador = "BORRADOR";
  public const string PlantillaPublicada = "PUBLICADA";
  public const string PlantillaArchivada = "ARCHIVADA";

  public const string PrioridadBaja = "BAJA";
  public const string PrioridadNormal = "NORMAL";
  public const string PrioridadAlta = "ALTA";
  public const string PrioridadUrgente = "URGENTE";

  public const string FotoNoPermitida = "NO_PERMITIDA";
  public const string FotoOpcional = "OPCIONAL";
  public const string FotoRequerida = "REQUERIDA";

  public const string EvidenciaCamera = "CAMERA";
  public const string EvidenciaFile = "FILE";
  public const string EvidenciaUnknown = "UNKNOWN";

  public static string TogglePasoHecho(string? currentStatus)
    => string.Equals(currentStatus, PasoHecho, StringComparison.OrdinalIgnoreCase)
      ? PasoPendiente
      : PasoHecho;

  public static readonly string[] OpenStatuses =
  [
    EstadoBorrador,
    EstadoAsignada,
    EstadoEnProceso,
    EstadoEnRevision,
    EstadoRechazada
  ];
}
