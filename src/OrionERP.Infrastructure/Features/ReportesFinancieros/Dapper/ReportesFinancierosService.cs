using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros;
using OrionERP.Application.Features.ReportesFinancieros.Models;
using System;
using System.Collections.Generic;
using System.Data;
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
            connection.Open();

            var parameters = new { Anio = anio, Mes = mes, Rfc = rfc };

            var result = await connection.QueryAsync<BalanzaComprobacionRow>(
                "reporteFinanciero.Rpt_BalanzaComprobacion",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30);

            return result.AsList();
        }

        public async Task<IReadOnlyList<EstadoPerdidasGananciasRow>> GetEstadoPerdidasGananciasAsync(
            DateTime startDate,
            DateTime endDate,
            string? rfc)
        {
            using var connection = _connectionFactory.Create();
            connection.Open();

            var parameters = new { startDate, endDate, RFC = rfc };

            var result = await connection.QueryAsync<EstadoPerdidasGananciasRow>(
                "reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30);

            return result.AsList();
        }

        public async Task<HojaTrabajoViewModel> GetHojaTrabajoAsync(int anio, string rfc)
        {
            using var connection = _connectionFactory.Create();
            connection.Open();

            var parameters = new { Anio = anio, Rfc = rfc };
            using var multi = await connection.QueryMultipleAsync(
                "contabilidad.Rpt_PapelTrabajo",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30);

            var cfdiRows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();
            var contabilidadRows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();
            var acumuladosRows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();
            var complementosRows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();
            var tipoERows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();
            var tipoNRows = (await multi.ReadAsync<HojaTrabajoLongRowDto>()).ToList();

            return new HojaTrabajoViewModel
            {
                Cfdi = PivotRows(cfdiRows),
                Complementos = PivotRows(complementosRows),
                Contabilidad = PivotRows(contabilidadRows),
                Acumulados = PivotRows(acumuladosRows),
                TipoE = PivotRows(tipoERows),
                TipoN = PivotRows(tipoNRows)
            };
        }

        private static List<HojaTrabajoTablaDto> PivotRows(IEnumerable<HojaTrabajoLongRowDto> rows)
        {
            return rows
                .GroupBy(r => new { r.Descripcion, r.Orden })
                .Select(group =>
                {
                    var dto = new HojaTrabajoTablaDto
                    {
                        Descripcion = group.Key.Descripcion,
                        Orden = group.Key.Orden,
                    };

                    foreach (var row in group)
                    {
                        switch (row.Mes)
                        {
                            case 1:
                                dto.ENERO = row.Monto;
                                break;
                            case 2:
                                dto.FEBRERO = row.Monto;
                                break;
                            case 3:
                                dto.MARZO = row.Monto;
                                break;
                            case 4:
                                dto.ABRIL = row.Monto;
                                break;
                            case 5:
                                dto.MAYO = row.Monto;
                                break;
                            case 6:
                                dto.JUNIO = row.Monto;
                                break;
                            case 7:
                                dto.JULIO = row.Monto;
                                break;
                            case 8:
                                dto.AGOSTO = row.Monto;
                                break;
                            case 9:
                                dto.SEPTIEMBRE = row.Monto;
                                break;
                            case 10:
                                dto.OCTUBRE = row.Monto;
                                break;
                            case 11:
                                dto.NOVIEMBRE = row.Monto;
                                break;
                            case 12:
                                dto.DICIEMBRE = row.Monto;
                                break;
                        }
                    }

                    return dto;
                })
                .OrderBy(dto => dto.Orden)
                .ToList();
        }
    }
}
