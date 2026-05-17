using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper
{
    public class ReportesFinancierosService : IReportesFinancierosService
    {
        private readonly IDbConnectionFactory _connectionFactory;

        public ReportesFinancierosService(IDbConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        public async Task<IReadOnlyList<BalanzaComprobacionRow>> GetBalanzaComprobacionAsync(
            int anio,
            int? mes,
            string? rfc)
        {
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new { Anio = anio, Mes = mes, Rfc = rfc };

            var result = await connection.QueryAsync<BalanzaComprobacionRow>(
                "reporteFinanciero.Rpt_BalanzaComprobacion",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30).ConfigureAwait(false);

            return result.AsList();
        }

        public async Task<IReadOnlyList<EstadoPerdidasGananciasRow>> GetEstadoPerdidasGananciasAsync(
            DateTime startDate,
            DateTime endDate,
            string? rfc)
        {
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new { startDate, endDate, RFC = rfc };

            var result = await connection.QueryAsync<EstadoPerdidasGananciasRow>(
                "reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30).ConfigureAwait(false);

            return result.AsList();
        }

        public async Task<SaludEmpresaReport> GetSaludEmpresaAsync(
            int anioInicio,
            int mesInicio,
            int anioFin,
            int mesFin,
            string? rfc)
        {
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new
            {
                AnioInicio = anioInicio,
                MesInicio = mesInicio,
                AnioFin = anioFin,
                MesFin = mesFin,
                RFC = rfc,
                IncluirHabitacionesNoRentables = false
            };

            using var multi = await connection.QueryMultipleAsync(
                "reporteFinanciero.Reporte_Salud_Empresa",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60).ConfigureAwait(false);

            var executiveIndicators = (await multi.ReadAsync<SaludEmpresaExecutiveIndicatorRow>().ConfigureAwait(false)).AsList();
            var suitePerformance = (await multi.ReadAsync<SaludEmpresaSuitePerformanceRow>().ConfigureAwait(false)).AsList();
            var financialBreakdown = (await multi.ReadAsync<SaludEmpresaFinancialBreakdownRow>().ConfigureAwait(false)).AsList();
            var cashFlow = (await multi.ReadAsync<SaludEmpresaCashFlowRow>().ConfigureAwait(false)).AsList();
            var dataQuality = (await multi.ReadAsync<SaludEmpresaDataQualityRow>().ConfigureAwait(false)).AsList();

            return new SaludEmpresaReport(
                executiveIndicators,
                suitePerformance,
                financialBreakdown,
                cashFlow,
                dataQuality);
        }

        public async Task<HojaTrabajoViewModel> GetHojaTrabajoAsync(int anio, string rfc)
        {
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new { Anio = anio, Rfc = rfc };
            using var multi = await connection.QueryMultipleAsync(
                "contabilidad.Rpt_PapelTrabajo",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30).ConfigureAwait(false);

            return new HojaTrabajoViewModel
            {
                Cfdi = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false)),
                Complementos = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false)),
                Contabilidad = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false)),
                Acumulados = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false)),
                TipoE = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false)),
                TipoN = PivotRows(await multi.ReadAsync<HojaTrabajoLongRowDto>().ConfigureAwait(false))
            };
        }

        private static List<HojaTrabajoTablaDto> PivotRows(IEnumerable<HojaTrabajoLongRowDto> rows)
        {
            var groupedRows = new Dictionary<(string? Descripcion, int Orden), HojaTrabajoTablaDto>();

            foreach (var row in rows)
            {
                var key = (row.Descripcion, row.Orden);
                if (!groupedRows.TryGetValue(key, out var dto))
                {
                    dto = new HojaTrabajoTablaDto
                    {
                        Descripcion = row.Descripcion,
                        Orden = row.Orden,
                    };
                    groupedRows.Add(key, dto);
                }

                AssignMonto(dto, row.Mes, row.Monto);
            }

            return groupedRows.Values
                .OrderBy(dto => dto.Orden)
                .ToList();
        }

        private static async Task OpenConnectionAsync(IDbConnection connection)
        {
            if (connection is DbConnection dbConnection)
            {
                await dbConnection.OpenAsync().ConfigureAwait(false);
                return;
            }

            connection.Open();
        }

        private static void AssignMonto(HojaTrabajoTablaDto dto, int mes, decimal monto)
        {
            switch (mes)
            {
                case 1:
                    dto.ENERO = monto;
                    break;
                case 2:
                    dto.FEBRERO = monto;
                    break;
                case 3:
                    dto.MARZO = monto;
                    break;
                case 4:
                    dto.ABRIL = monto;
                    break;
                case 5:
                    dto.MAYO = monto;
                    break;
                case 6:
                    dto.JUNIO = monto;
                    break;
                case 7:
                    dto.JULIO = monto;
                    break;
                case 8:
                    dto.AGOSTO = monto;
                    break;
                case 9:
                    dto.SEPTIEMBRE = monto;
                    break;
                case 10:
                    dto.OCTUBRE = monto;
                    break;
                case 11:
                    dto.NOVIEMBRE = monto;
                    break;
                case 12:
                    dto.DICIEMBRE = monto;
                    break;
            }
        }
    }
}
