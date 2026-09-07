using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Application.Features.ReportesFinancieros;

public interface IDeclaracionMensualService
{
  /// <summary>Cifras, conciliacion y hallazgos de un periodo.</summary>
  Task<DeclaracionMensualReport> GetMensualAsync(
    string rfc, int ejercicio, int periodo, CancellationToken cancellationToken = default);

  /// <summary>Los doce meses del ejercicio: calculado contra declarado.</summary>
  Task<IReadOnlyList<DeclaracionEjercicioRow>> GetEjercicioAsync(
    string rfc, int ejercicio, CancellationToken cancellationToken = default);

  /// <summary>
  /// Asiento de cierre propuesto. No escribe nada; equivale a
  /// fiscal.Generar_Poliza_Cierre con @Aplicar = 0.
  /// </summary>
  Task<IReadOnlyList<DeclaracionCierreRow>> PreviewCierreAsync(
    string rfc, int ejercicio, int periodo, CancellationToken cancellationToken = default);

  /// <summary>
  /// Genera la poliza de cierre. Falla si ya existe una para el periodo, salvo
  /// que se pase <paramref name="regenerar"/>, en cuyo caso la anterior se
  /// cancela con un asiento inverso.
  /// </summary>
  Task<(int TransaccionId, IReadOnlyList<DeclaracionCierreRow> Lineas)> GenerarCierreAsync(
    string rfc, int ejercicio, int periodo, bool regenerar, string usuario,
    CancellationToken cancellationToken = default);

  /// <summary>Lee un acuse del SAT sin guardar nada, para vista previa.</summary>
  DeclaracionImportada LeerAcuse(Stream pdf, string nombreArchivo);

  /// <summary>Guarda los acuses ya revisados.</summary>
  Task<DeclaracionImportResultado> GuardarImportacionAsync(
    string rfc, IReadOnlyList<DeclaracionImportada> declaraciones, string usuario,
    CancellationToken cancellationToken = default);
}
