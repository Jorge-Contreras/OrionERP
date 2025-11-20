using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.ReportesFinancieros;
using System.Collections.Generic;
using System.Data;
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

        public async Task<List<HojaTrabajoDto>> GetHojaTrabajoAsync(int anio, string rfc)
        {
            using var connection = _connectionFactory.Create();
            connection.Open();

            var parameters = new { Anio = anio, Rfc = rfc };
            var result = await connection.QueryAsync<HojaTrabajoDto>(
                "contabilidad.Rpt_HojaTrabajo",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30);

            return result.AsList();
        }
    }
}
