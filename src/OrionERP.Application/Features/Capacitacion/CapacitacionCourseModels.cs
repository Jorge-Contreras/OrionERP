using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Capacitacion;

public sealed class CapacitacionCursoAdministrableDto : CapacitacionCursoDetalleDto
{
  public string RfcPropietario { get; set; } = string.Empty;
  public bool Activo { get; set; }
  public DateTime? PublicadaEn { get; set; }
  public bool EsGlobal => string.Equals(RfcPropietario, CapacitacionCodes.RfcGlobal, StringComparison.OrdinalIgnoreCase);
  public bool EsBorrador => string.Equals(EstadoVersion, CapacitacionCodes.CursoVersionBorrador, StringComparison.OrdinalIgnoreCase);
}

public sealed class CapacitacionGuardarCursoRequest
{
  [Required, StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  public int CursoId { get; set; }
  public int CursoVersionId { get; set; }

  [Required, StringLength(64)]
  public string Clave { get; set; } = string.Empty;

  [Required, StringLength(80)]
  public string Categoria { get; set; } = string.Empty;

  [Required, StringLength(160)]
  public string Nombre { get; set; } = string.Empty;

  [Required, StringLength(1000)]
  public string Descripcion { get; set; } = string.Empty;

  [Range(1, 10080)]
  public int DuracionMinutos { get; set; } = 60;

  [Required, StringLength(2000)]
  public string Objetivos { get; set; } = string.Empty;

  [StringLength(1000)]
  public string? Prerequisitos { get; set; }

  [Range(0, 100)]
  public decimal CalificacionMinima { get; set; } = 80;

  public List<CapacitacionGuardarLeccionRequest> Lecciones { get; set; } = [];
  public List<CapacitacionGuardarEvaluacionRequest> Evaluaciones { get; set; } = [];
  public List<CapacitacionGuardarPracticaRequest> Practicas { get; set; } = [];
  public int ActorEmployeeId { get; set; }

  [Required, StringLength(256)]
  public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionGuardarLeccionRequest
{
  public int LeccionId { get; set; }
  public int Orden { get; set; }

  [Required, StringLength(64)]
  public string Clave { get; set; } = string.Empty;

  [Required, StringLength(160)]
  public string Titulo { get; set; } = string.Empty;

  [Required, StringLength(1000)]
  public string Objetivo { get; set; } = string.Empty;

  [Range(1, 1440)]
  public int DuracionMinutos { get; set; } = 15;

  public bool Requerida { get; set; } = true;
  public List<CapacitacionGuardarBloqueRequest> Bloques { get; set; } = [];
}

public sealed class CapacitacionGuardarBloqueRequest
{
  public int BloqueId { get; set; }
  public int Orden { get; set; }

  [Required, StringLength(24)]
  public string Tipo { get; set; } = CapacitacionCodes.BloqueTeoria;

  [Required, StringLength(160)]
  public string Titulo { get; set; } = string.Empty;

  [Required]
  public string Contenido { get; set; } = string.Empty;

  public string? ConfiguracionJson { get; set; }
  public bool Requerido { get; set; } = true;
}

public sealed class CapacitacionGuardarEvaluacionRequest
{
  public int EvaluacionId { get; set; }

  [Required, StringLength(160)]
  public string Titulo { get; set; } = string.Empty;

  [Required, StringLength(1000)]
  public string Instrucciones { get; set; } = string.Empty;

  [Range(0, 100)]
  public decimal CalificacionMinima { get; set; } = 80;

  public bool Requerida { get; set; } = true;
  public List<CapacitacionGuardarPreguntaRequest> Preguntas { get; set; } = [];
}

public sealed class CapacitacionGuardarPreguntaRequest
{
  public int PreguntaId { get; set; }
  public int Orden { get; set; }

  [Required, StringLength(1000)]
  public string Texto { get; set; } = string.Empty;

  [StringLength(1000)]
  public string? Explicacion { get; set; }

  public bool Critica { get; set; }
  public List<CapacitacionGuardarOpcionRequest> Opciones { get; set; } = [];
}

public sealed class CapacitacionGuardarOpcionRequest
{
  public int OpcionId { get; set; }
  public int Orden { get; set; }

  [Required, StringLength(1000)]
  public string Texto { get; set; } = string.Empty;

  public bool EsCorrecta { get; set; }
}

public sealed class CapacitacionGuardarPracticaRequest
{
  public int PracticaId { get; set; }

  [Required, StringLength(160)]
  public string Titulo { get; set; } = string.Empty;

  [Required, StringLength(2000)]
  public string Instrucciones { get; set; } = string.Empty;

  [StringLength(500)]
  public string? RutaSandbox { get; set; }

  public bool Requerida { get; set; } = true;
  public List<CapacitacionGuardarPracticaPasoRequest> Pasos { get; set; } = [];
}

public sealed class CapacitacionGuardarPracticaPasoRequest
{
  public int PracticaPasoId { get; set; }
  public int Orden { get; set; }

  [Required, StringLength(1000)]
  public string Descripcion { get; set; } = string.Empty;

  public bool Critico { get; set; }
}

public class CapacitacionCursoCommandRequest
{
  [Required, StringLength(50)]
  public string Rfc { get; set; } = string.Empty;

  public int CursoId { get; set; }
  public int ActorEmployeeId { get; set; }

  [Required, StringLength(256)]
  public string Actor { get; set; } = string.Empty;
}

public sealed class CapacitacionCambiarEstadoCursoRequest : CapacitacionCursoCommandRequest
{
  public bool Activo { get; set; }
}
