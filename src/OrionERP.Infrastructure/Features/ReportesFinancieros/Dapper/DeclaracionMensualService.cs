using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.Infrastructure.Features.ReportesFinancieros.Sat;

namespace OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper;

public class DeclaracionMensualService : IDeclaracionMensualService
{
  private readonly IDbConnectionFactory _connectionFactory;
  private readonly SatDeclaracionPdfParser _parser = new();

  /// <summary>
  /// Columnas de fiscal.DeclaracionPresentada que el importador puede escribir.
  /// El SQL de guardado se arma con los nombres del diccionario que devuelve el
  /// parser, asi que esta lista blanca es lo que impide que un nombre de campo
  /// inesperado termine concatenado en la sentencia.
  /// </summary>
  private static readonly HashSet<string> ColumnasImportables = new(StringComparer.Ordinal)
  {
    "IngresosNominalesPeriodo", "IngresosNominalesAnteriores", "TotalIngresosNominales",
    "CoeficienteUtilidad", "UtilidadFiscal", "DeduccionInmediata", "Ptu", "PerdidasFiscales",
    "BaseGravableIsr", "ImpuestoCausado", "PagosProvisionalesAnteriores", "IsrRetenido",
    "IsrACargo", "IsrRecargos", "IsrTotalAPagar",
    "ActosGravados16", "IvaTrasladado16", "ActosGravados0", "ActosExentos", "ActosNoObjeto",
    "TotalIvaACargo", "ActosPagados16", "IvaAcreditable16", "ActosPagados0", "ProporcionIva",
    "TotalIvaAcreditable", "IvaRetenido", "IvaSaldoAFavor", "IvaACargo", "IvaTotalAPagar"
  };

  public DeclaracionMensualService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<DeclaracionMensualReport> GetMensualAsync(
    string rfc, int ejercicio, int periodo, CancellationToken cancellationToken = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    var parametros = new { Rfc = rfc, Ejercicio = ejercicio, Periodo = periodo };

    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      "fiscal.Rpt_Declaracion_Mensual", parametros,
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    var reporte = new DeclaracionMensualReport
    {
      Encabezado = await multi.ReadFirstOrDefaultAsync<DeclaracionEncabezadoRow>().ConfigureAwait(false),
      Isr = (await multi.ReadAsync<DeclaracionRenglonRow>().ConfigureAwait(false)).AsList(),
      Iva = (await multi.ReadAsync<DeclaracionRenglonRow>().ConfigureAwait(false)).AsList(),
      Conciliacion = (await multi.ReadAsync<DeclaracionConciliacionRow>().ConfigureAwait(false)).AsList(),
      Retenciones = (await multi.ReadAsync<DeclaracionRetencionRow>().ConfigureAwait(false)).AsList(),
      Cierre = (await multi.ReadAsync<DeclaracionCierreRow>().ConfigureAwait(false)).AsList(),
      CierreIsr = (await multi.ReadAsync<DeclaracionCierreRow>().ConfigureAwait(false)).AsList()
    };

    using var hallazgos = await connection.QueryMultipleAsync(new CommandDefinition(
      "fiscal.Rpt_Declaracion_Hallazgos", parametros,
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    reporte.Hallazgos = (await hallazgos.ReadAsync<DeclaracionHallazgoRow>().ConfigureAwait(false)).AsList();
    reporte.HallazgosResumen = await hallazgos.ReadFirstOrDefaultAsync<DeclaracionHallazgoResumen>().ConfigureAwait(false);

    return reporte;
  }

  public async Task<IReadOnlyList<DeclaracionEjercicioRow>> GetEjercicioAsync(
    string rfc, int ejercicio, CancellationToken cancellationToken = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    var filas = await connection.QueryAsync<DeclaracionEjercicioRow>(new CommandDefinition(
      "fiscal.Rpt_Declaracion_Ejercicio", new { Rfc = rfc, Ejercicio = ejercicio },
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    return filas.AsList();
  }

  public async Task<IReadOnlyList<DeclaracionCierreRow>> PreviewCierreAsync(
    string rfc, int ejercicio, int periodo, CancellationToken cancellationToken = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    var filas = await connection.QueryAsync<DeclaracionCierreRow>(new CommandDefinition(
      "fiscal.Generar_Poliza_Cierre",
      new { Rfc = rfc, Ejercicio = ejercicio, Periodo = periodo, Aplicar = false },
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    return filas.AsList();
  }

  public async Task<(int TransaccionId, IReadOnlyList<DeclaracionCierreRow> Lineas)> GenerarCierreAsync(
    string rfc, int ejercicio, int periodo, bool regenerar, string usuario,
    CancellationToken cancellationToken = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    var parametros = new DynamicParameters();
    parametros.Add("@Rfc", rfc);
    parametros.Add("@Ejercicio", ejercicio);
    parametros.Add("@Periodo", periodo);
    parametros.Add("@Aplicar", true);
    parametros.Add("@Regenerar", regenerar);
    parametros.Add("@Usuario", usuario);
    parametros.Add("@TransaccionID", dbType: DbType.Int32, direction: ParameterDirection.InputOutput);

    var filas = await connection.QueryAsync<DeclaracionCierreRow>(new CommandDefinition(
      "fiscal.Generar_Poliza_Cierre", parametros,
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    return (parametros.Get<int?>("@TransaccionID") ?? 0, filas.AsList());
  }

  public async Task<(int TransaccionId, IReadOnlyList<DeclaracionCierreRow> Lineas)> GenerarIsrAsync(
    string rfc, int ejercicio, int periodo, bool regenerar, string usuario,
    CancellationToken cancellationToken = default)
  {
    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    var parametros = new DynamicParameters();
    parametros.Add("@Rfc", rfc);
    parametros.Add("@Ejercicio", ejercicio);
    parametros.Add("@Periodo", periodo);
    parametros.Add("@Aplicar", true);
    parametros.Add("@Regenerar", regenerar);
    parametros.Add("@Usuario", usuario);
    parametros.Add("@TransaccionID", dbType: DbType.Int32, direction: ParameterDirection.InputOutput);

    var filas = await connection.QueryAsync<DeclaracionCierreRow>(new CommandDefinition(
      "fiscal.Generar_Poliza_Isr", parametros,
      commandType: CommandType.StoredProcedure, commandTimeout: 60,
      cancellationToken: cancellationToken)).ConfigureAwait(false);

    return (parametros.Get<int?>("@TransaccionID") ?? 0, filas.AsList());
  }

  public DeclaracionImportada LeerAcuse(Stream pdf, string nombreArchivo)
    => _parser.Parse(pdf, nombreArchivo);

  public async Task<DeclaracionImportResultado> GuardarImportacionAsync(
    string rfc, IReadOnlyList<DeclaracionImportada> declaraciones, string usuario,
    CancellationToken cancellationToken = default)
  {
    var resultado = new DeclaracionImportResultado();

    // Se aplanan las filas propias junto con los meses anteriores que cada
    // acuse reconstruye. Si dos archivos traen el mismo mes, gana el completo:
    // un renglon parcial solo aporta ingresos nominales e ISR a cargo.
    var pendientes = declaraciones
      .SelectMany(d => new[] { d }.Concat(d.Anteriores))
      .Where(d => d.EsValida)
      .OrderBy(d => d.EsParcial)
      .ToList();

    using var connection = _connectionFactory.Create();
    await OpenAsync(connection, cancellationToken).ConfigureAwait(false);

    foreach (var fila in pendientes)
    {
      // El acuse trae su propio RFC. Si no es el de la sesion, no se guarda:
      // subir por error la declaracion de otra empresa contaminaria el
      // historial con el que se decide presentar complementarias.
      if (!string.Equals(fila.Rfc, rfc, StringComparison.OrdinalIgnoreCase))
      {
        resultado.Omitidas++;
        resultado.Mensajes.Add(
          $"{fila.ArchivoNombre}: el acuse es del RFC {fila.Rfc}, no de {rfc}. No se guardo.");
        continue;
      }

      var columnas = fila.Campos
        .Where(kv => ColumnasImportables.Contains(kv.Key) && kv.Value.HasValue)
        .ToList();

      var parametros = new DynamicParameters();
      parametros.Add("@Rfc", fila.Rfc);
      parametros.Add("@Ejercicio", fila.Ejercicio);
      parametros.Add("@Periodo", fila.Periodo);
      parametros.Add("@TipoDeclaracion", fila.TipoDeclaracion);
      parametros.Add("@NumeroComplementaria", fila.NumeroComplementaria);
      parametros.Add("@NumeroOperacion", fila.NumeroOperacion);
      parametros.Add("@FechaPresentacion", fila.FechaPresentacion);
      parametros.Add("@Estatus", fila.Estatus);
      parametros.Add("@EsParcial", fila.EsParcial);
      parametros.Add("@ArchivoNombre", fila.ArchivoNombre);
      parametros.Add("@ArchivoSha256", fila.ArchivoSha256);
      parametros.Add("@ImportadoPor", usuario);

      foreach (var (nombre, valor) in columnas)
      {
        parametros.Add("@c_" + nombre, valor);
      }

      // Un renglon parcial nunca pisa un dato que ya existe; uno completo si.
      var asignaciones = columnas
        .Select(kv => fila.EsParcial
          ? $"[{kv.Key}] = COALESCE([{kv.Key}], @c_{kv.Key})"
          : $"[{kv.Key}] = @c_{kv.Key}")
        .ToList();

      asignaciones.Add("NumeroOperacion = COALESCE(@NumeroOperacion, NumeroOperacion)");
      asignaciones.Add("FechaPresentacion = COALESCE(@FechaPresentacion, FechaPresentacion)");
      asignaciones.Add("Estatus = COALESCE(@Estatus, Estatus)");
      asignaciones.Add("EsParcial = CASE WHEN @EsParcial = 0 THEN 0 ELSE EsParcial END");
      asignaciones.Add("ImportadoEn = SYSUTCDATETIME()");
      asignaciones.Add("ImportadoPor = @ImportadoPor");

      if (!fila.EsParcial)
      {
        asignaciones.Add("ArchivoNombre = @ArchivoNombre");
        asignaciones.Add("ArchivoSha256 = @ArchivoSha256");
      }

      var listaColumnas = string.Join(", ", columnas.Select(kv => $"[{kv.Key}]"));
      var listaValores = string.Join(", ", columnas.Select(kv => "@c_" + kv.Key));
      var coma = columnas.Count > 0 ? ", " : string.Empty;

      var set = string.Join("," + Environment.NewLine + "    ", asignaciones);

      var sql = $"""
UPDATE fiscal.DeclaracionPresentada
SET {set}
WHERE Rfc = @Rfc AND Ejercicio = @Ejercicio AND Periodo = @Periodo
  AND TipoDeclaracion = @TipoDeclaracion AND NumeroComplementaria = @NumeroComplementaria;

IF @@ROWCOUNT = 0
BEGIN
  INSERT INTO fiscal.DeclaracionPresentada
    (Rfc, Ejercicio, Periodo, TipoDeclaracion, NumeroComplementaria, NumeroOperacion,
     FechaPresentacion, Estatus, EsParcial, ArchivoNombre, ArchivoSha256, ImportadoPor{coma}{listaColumnas})
  VALUES
    (@Rfc, @Ejercicio, @Periodo, @TipoDeclaracion, @NumeroComplementaria, @NumeroOperacion,
     @FechaPresentacion, @Estatus, @EsParcial, @ArchivoNombre, @ArchivoSha256, @ImportadoPor{coma}{listaValores});
  SELECT 1;
END
ELSE SELECT 0;
""";

      var insertada = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        sql, parametros, commandTimeout: 30, cancellationToken: cancellationToken))
        .ConfigureAwait(false);

      if (insertada == 1)
      {
        resultado.Insertadas++;
      }
      else
      {
        resultado.Actualizadas++;
      }
    }

    return resultado;
  }

  private static async Task OpenAsync(IDbConnection connection, CancellationToken cancellationToken)
  {
    if (connection is DbConnection db)
    {
      await db.OpenAsync(cancellationToken).ConfigureAwait(false);
      return;
    }

    connection.Open();
  }
}
