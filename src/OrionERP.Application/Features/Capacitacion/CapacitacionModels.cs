using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Capacitacion;

public static class CapacitacionCodes
{
  public const string RfcGlobal = "*";

  public const string RoleAdmin = "CapacitacionAdmin";
  public const string RoleInstructor = "CapacitacionInstructor";
  public const string RoleAuditor = "CapacitacionAuditor";

  public const string AsignacionAsignada = "ASIGNADA";
  public const string AsignacionEnCurso = "EN_CURSO";
  public const string AsignacionEsperaFirma = "ESPERA_FIRMA";
  public const string AsignacionEsperaAcuse = "ESPERA_ACUSE";
  public const string AsignacionCompletada = "COMPLETADA";
  public const string AsignacionCancelada = "CANCELADA";

  public const string SesionProgramada = "PROGRAMADA";
  public const string SesionEnCurso = "EN_CURSO";
  public const string SesionFinalizada = "FINALIZADA";
  public const string SesionCancelada = "CANCELADA";

  public const string CursoVersionBorrador = "BORRADOR";
  public const string CursoVersionPublicada = "PUBLICADA";
  public const string CursoVersionRetirada = "RETIRADA";

  public const string BloqueTeoria = "TEORIA";
  public const string BloqueObjetivos = "OBJETIVOS";
  public const string BloqueImagen = "IMAGEN";
  public const string BloquePasos = "PASOS";
  public const string BloqueDemostracion = "DEMOSTRACION";
  public const string BloquePractica = "PRACTICA";
  public const string BloqueEvaluacion = "EVALUACION";
  public const string BloqueResumen = "RESUMEN";
  public const string BloqueAlerta = "ALERTA";
}

public sealed class CapacitacionActorContext
{
  [Required, StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  public int EmployeeId { get; set; }

  [Required, StringLength(256)]
  public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionCommandResult
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public long? EntityId { get; set; }
  public int AffectedCount { get; set; }

  public static CapacitacionCommandResult Ok(string message, long? entityId = null, int affectedCount = 0)
    => new() { Success = true, Message = message, EntityId = entityId, AffectedCount = affectedCount };

  public static CapacitacionCommandResult Fail(string message, long? entityId = null)
    => new() { Success = false, Message = message, EntityId = entityId };
}

public sealed class CapacitacionDashboardDto
{
  public int Pendientes { get; set; }
  public int EnCurso { get; set; }
  public int Completadas { get; set; }
  public int Vencidas { get; set; }
  public int SesionesActivas { get; set; }
  public decimal ProgresoPromedio { get; set; }
  public IReadOnlyList<CapacitacionAsignacionDto> MisAsignaciones { get; set; } = Array.Empty<CapacitacionAsignacionDto>();
  public IReadOnlyList<CapacitacionSesionResumenDto> Sesiones { get; set; } = Array.Empty<CapacitacionSesionResumenDto>();
}

public class CapacitacionCursoResumenDto
{
  public int CursoId { get; set; }
  public int CursoVersionId { get; set; }
  public int NumeroVersion { get; set; }
  public string Clave { get; set; } = string.Empty;
  public string Categoria { get; set; } = string.Empty;
  public string Nombre { get; set; } = string.Empty;
  public string Descripcion { get; set; } = string.Empty;
  public string Objetivos { get; set; } = string.Empty;
  public string? Prerequisitos { get; set; }
  public int DuracionMinutos { get; set; }
  public decimal CalificacionMinima { get; set; }
  public int LeccionCount { get; set; }
  public int BloqueCount { get; set; }
  public string EstadoVersion { get; set; } = string.Empty;
}

public class CapacitacionCursoDetalleDto : CapacitacionCursoResumenDto
{
  public IReadOnlyList<CapacitacionLeccionDto> Lecciones { get; set; } = Array.Empty<CapacitacionLeccionDto>();
  public IReadOnlyList<CapacitacionEvaluacionDto> Evaluaciones { get; set; } = Array.Empty<CapacitacionEvaluacionDto>();
  public IReadOnlyList<CapacitacionPracticaDto> Practicas { get; set; } = Array.Empty<CapacitacionPracticaDto>();
}

public sealed class CapacitacionLeccionDto
{
  public int LeccionId { get; set; }
  public int CursoVersionId { get; set; }
  public int Orden { get; set; }
  public string Clave { get; set; } = string.Empty;
  public string Titulo { get; set; } = string.Empty;
  public string Objetivo { get; set; } = string.Empty;
  public int DuracionMinutos { get; set; }
  public bool Requerida { get; set; }
  public IReadOnlyList<CapacitacionBloqueDto> Bloques { get; set; } = Array.Empty<CapacitacionBloqueDto>();
}

public sealed class CapacitacionBloqueDto
{
  public int BloqueId { get; set; }
  public int LeccionId { get; set; }
  public int Orden { get; set; }
  public string Tipo { get; set; } = CapacitacionCodes.BloqueTeoria;
  public string Titulo { get; set; } = string.Empty;
  public string Contenido { get; set; } = string.Empty;
  public string? ConfiguracionJson { get; set; }
  public bool Requerido { get; set; }
  public IReadOnlyList<CapacitacionRecursoDto> Recursos { get; set; } = Array.Empty<CapacitacionRecursoDto>();
}

public sealed class CapacitacionRecursoDto
{
  public int RecursoId { get; set; }
  public int BloqueId { get; set; }
  public int Orden { get; set; }
  public string Tipo { get; set; } = string.Empty;
  public string Titulo { get; set; } = string.Empty;
  public string Ruta { get; set; } = string.Empty;
  public string? TextoAlternativo { get; set; }
  public string? HashContenido { get; set; }
  public DateTime? CapturadoEn { get; set; }
  public string? VersionAplicacion { get; set; }
}

public sealed class CapacitacionEvaluacionDto
{
  public int EvaluacionId { get; set; }
  public int CursoVersionId { get; set; }
  public string Titulo { get; set; } = string.Empty;
  public string Instrucciones { get; set; } = string.Empty;
  public decimal CalificacionMinima { get; set; }
  public bool Requerida { get; set; }
  public IReadOnlyList<CapacitacionPreguntaDto> Preguntas { get; set; } = Array.Empty<CapacitacionPreguntaDto>();
}

public sealed class CapacitacionPreguntaDto
{
  public int PreguntaId { get; set; }
  public int EvaluacionId { get; set; }
  public int Orden { get; set; }
  public string Texto { get; set; } = string.Empty;
  public string? Explicacion { get; set; }
  public bool Critica { get; set; }
  public IReadOnlyList<CapacitacionOpcionDto> Opciones { get; set; } = Array.Empty<CapacitacionOpcionDto>();
}

public sealed class CapacitacionOpcionDto
{
  public int OpcionId { get; set; }
  public int PreguntaId { get; set; }
  public int Orden { get; set; }
  public string Texto { get; set; } = string.Empty;
  public bool EsCorrecta { get; set; }
}

public sealed class CapacitacionPracticaDto
{
  public int PracticaId { get; set; }
  public int CursoVersionId { get; set; }
  public string Titulo { get; set; } = string.Empty;
  public string Instrucciones { get; set; } = string.Empty;
  public string? RutaSandbox { get; set; }
  public bool Requerida { get; set; }
  public IReadOnlyList<CapacitacionPracticaPasoDto> Pasos { get; set; } = Array.Empty<CapacitacionPracticaPasoDto>();
}

public sealed class CapacitacionPracticaPasoDto
{
  public int PracticaPasoId { get; set; }
  public int PracticaId { get; set; }
  public int Orden { get; set; }
  public string Descripcion { get; set; } = string.Empty;
  public bool Critico { get; set; }
}

public sealed class CapacitacionAsignacionDto
{
  public long AsignacionId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int EmployeeId { get; set; }
  public string EmployeeName { get; set; } = string.Empty;
  public int CursoVersionId { get; set; }
  public string CursoClave { get; set; } = string.Empty;
  public string CursoNombre { get; set; } = string.Empty;
  public string Categoria { get; set; } = string.Empty;
  public int NumeroVersion { get; set; }
  public string Estado { get; set; } = CapacitacionCodes.AsignacionAsignada;
  public decimal Porcentaje { get; set; }
  public DateTime AsignadaEn { get; set; }
  public DateTime? FechaLimite { get; set; }
  public DateTime? IniciadaEn { get; set; }
  public DateTime? CompletadaEn { get; set; }
  public int? InstructorEmployeeId { get; set; }
  public string? InstructorName { get; set; }
  public decimal? Calificacion { get; set; }
  public int? UltimoBloqueCompletadoId { get; set; }
  public bool PracticaAprobada { get; set; }
  public bool FirmaInstructor { get; set; }
  public bool AcuseColaborador { get; set; }
}

public sealed class CapacitacionEmpleadoDto
{
  public int EmployeeId { get; set; }
  public string Nombre { get; set; } = string.Empty;
  public string? Puesto { get; set; }
  public string? Email { get; set; }
  public bool TieneUsuario { get; set; }
}

public class CapacitacionSesionResumenDto
{
  public long SesionId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int CursoVersionId { get; set; }
  public string CursoNombre { get; set; } = string.Empty;
  public string Nombre { get; set; } = string.Empty;
  public string CodigoAcceso { get; set; } = string.Empty;
  public string Estado { get; set; } = CapacitacionCodes.SesionProgramada;
  public int InstructorEmployeeId { get; set; }
  public string InstructorName { get; set; } = string.Empty;
  public int ParticipanteCount { get; set; }
  public int? BloqueActualId { get; set; }
  public DateTime ProgramadaEn { get; set; }
  public DateTime? IniciadaEn { get; set; }
  public DateTime? FinalizadaEn { get; set; }
}

public sealed class CapacitacionSesionDto : CapacitacionSesionResumenDto
{
  public CapacitacionBloqueDto? BloqueActual { get; set; }
  public CapacitacionCursoDetalleDto Curso { get; set; } = new();
  public IReadOnlyList<CapacitacionSesionParticipanteDto> Participantes { get; set; } = Array.Empty<CapacitacionSesionParticipanteDto>();
}

public sealed class CapacitacionSesionParticipanteDto
{
  public int EmployeeId { get; set; }
  public string Nombre { get; set; } = string.Empty;
  public string Rol { get; set; } = "COLABORADOR";
  public long? AsignacionId { get; set; }
  public DateTime? UnidoEn { get; set; }
  public decimal Porcentaje { get; set; }
  public string EstadoAsignacion { get; set; } = string.Empty;
  public bool BloqueActualCompletado { get; set; }
}

public sealed class CapacitacionEvaluacionResultadoDto
{
  public bool Success { get; set; }
  public string Message { get; set; } = string.Empty;
  public long? IntentoId { get; set; }
  public decimal Calificacion { get; set; }
  public bool Aprobada { get; set; }
  public bool FalloPreguntaCritica { get; set; }
  public IReadOnlyList<int> PreguntasIncorrectas { get; set; } = Array.Empty<int>();
}

public sealed class CapacitacionCrearAsignacionesRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public int CursoVersionId { get; set; }
  public IReadOnlyList<int> EmployeeIds { get; set; } = Array.Empty<int>();
  public int? InstructorEmployeeId { get; set; }
  public DateTime? FechaLimite { get; set; }
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionCrearSesionRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public int CursoVersionId { get; set; }
  [Required, StringLength(160)] public string Nombre { get; set; } = string.Empty;
  public int InstructorEmployeeId { get; set; }
  public IReadOnlyList<int> ParticipantEmployeeIds { get; set; } = Array.Empty<int>();
  public DateTime? ProgramadaEn { get; set; }
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionAvanzarSesionRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long SesionId { get; set; }
  public int? BloqueId { get; set; }
  public bool Finalizar { get; set; }
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionRegistrarBloqueRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long AsignacionId { get; set; }
  public long? SesionId { get; set; }
  public int BloqueId { get; set; }
  public int EmployeeId { get; set; }
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionRegistrarEvaluacionRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long AsignacionId { get; set; }
  public long? SesionId { get; set; }
  public int EvaluacionId { get; set; }
  public int EmployeeId { get; set; }
  public IReadOnlyList<int> OpcionIds { get; set; } = Array.Empty<int>();
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionPracticaPasoResultadoRequest
{
  public int PracticaPasoId { get; set; }
  public bool Aprobado { get; set; }
  [StringLength(500)] public string? Observaciones { get; set; }
}

public sealed class CapacitacionRegistrarPracticaRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long AsignacionId { get; set; }
  public long? SesionId { get; set; }
  public int PracticaId { get; set; }
  public int EmployeeId { get; set; }
  public IReadOnlyList<CapacitacionPracticaPasoResultadoRequest> Pasos { get; set; } = Array.Empty<CapacitacionPracticaPasoResultadoRequest>();
  [StringLength(1000)] public string? Observaciones { get; set; }
  public int ActorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionFirmarRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long AsignacionId { get; set; }
  public int InstructorEmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
  [StringLength(1000)] public string? Comentarios { get; set; }
}

public sealed class CapacitacionAcusarRequest
{
  [Required, StringLength(50)] public string Rfc { get; set; } = string.Empty;
  public long AsignacionId { get; set; }
  public int EmployeeId { get; set; }
  [Required, StringLength(256)] public string Actor { get; set; } = string.Empty;
}
