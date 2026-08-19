namespace OrionERP.Application.Features.Capacitacion;

public interface ICapacitacionService
{
  Task<CapacitacionDashboardDto> GetDashboardAsync(CapacitacionActorContext context, CancellationToken ct = default);
  Task<IReadOnlyList<CapacitacionCursoResumenDto>> GetCatalogoAsync(string rfc, CancellationToken ct = default);
  Task<CapacitacionCursoDetalleDto?> GetCursoAsync(int cursoVersionId, string rfc, CancellationToken ct = default);
  Task<CapacitacionCursoDetalleDto?> GetCursoAsignadoAsync(long asignacionId, string rfc, int employeeId, CancellationToken ct = default);
  Task<IReadOnlyList<CapacitacionAsignacionDto>> GetMiPlanAsync(string rfc, int employeeId, CancellationToken ct = default);
  Task<IReadOnlyList<CapacitacionEmpleadoDto>> GetEmpleadosAsignablesAsync(string rfc, string? search = null, CancellationToken ct = default);
  Task<CapacitacionCommandResult> CrearAsignacionesAsync(CapacitacionCrearAsignacionesRequest request, CancellationToken ct = default);
  Task<CapacitacionCommandResult> CrearSesionAsync(CapacitacionCrearSesionRequest request, CancellationToken ct = default);
  Task<CapacitacionSesionDto?> GetSesionAsync(long sesionId, string rfc, int actorEmployeeId, CancellationToken ct = default);
  Task<CapacitacionCommandResult> AvanzarSesionAsync(CapacitacionAvanzarSesionRequest request, CancellationToken ct = default);
  Task<CapacitacionCommandResult> RegistrarProgresoBloqueAsync(CapacitacionRegistrarBloqueRequest request, CancellationToken ct = default);
  Task<CapacitacionEvaluacionResultadoDto> RegistrarEvaluacionAsync(CapacitacionRegistrarEvaluacionRequest request, CancellationToken ct = default);
  Task<CapacitacionCommandResult> RegistrarResultadoPracticoAsync(CapacitacionRegistrarPracticaRequest request, CancellationToken ct = default);
  Task<CapacitacionCommandResult> FirmarFinalizacionAsync(CapacitacionFirmarRequest request, CancellationToken ct = default);
  Task<CapacitacionCommandResult> AcusarFinalizacionAsync(CapacitacionAcusarRequest request, CancellationToken ct = default);
}
