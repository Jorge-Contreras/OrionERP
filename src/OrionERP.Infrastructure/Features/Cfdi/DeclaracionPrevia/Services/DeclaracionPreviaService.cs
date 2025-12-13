using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;
using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace OrionERP.Infrastructure.Features.Cfdi.DeclaracionPrevia.Services
{
    public class DeclaracionPreviaService : IDeclaracionPreviaService
    {
        private readonly string _connectionString;

        public DeclaracionPreviaService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("OrionDb") ?? throw new InvalidOperationException("Connection string 'OrionDb' not found.");
        }

        public async Task<int> GenerarPolizaDesdeComprobanteAsync(int comprobanteId, string rfc)
        {
            using var conn = new SqlConnection(_connectionString);
            var parameters = new DynamicParameters();
            parameters.Add("@Comprobante_Id", comprobanteId);
            parameters.Add("@RFC", rfc);
            parameters.Add("@TransaccionID", dbType: DbType.Int32, direction: ParameterDirection.Output);

            await conn.ExecuteAsync("[contabilidad].[Generar_Poliza_Desde_Comprobante]", parameters, commandType: CommandType.StoredProcedure);

            return parameters.Get<int>("@TransaccionID");
        }

        public async Task<DeclaracionPreviaData> GetDeclaracionPreviaDataAsync(int year, int month, bool isAnnual, string rfc)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var common = new
            {
                Year = year,
                Month = isAnnual ? (object)DBNull.Value : month,
                RFC = rfc
            };

            var allCfdiBase = (await conn.QueryAsync<DeclaracionCfdiBase>(
                "EXEC cfdi.Declaracion_CFDI_Base @Year, @Month, @RFC",
                common)).AsList();

            var desfase = (await conn.QueryAsync<DesfaseItem>(
                "EXEC dbo.Declaracion_Comprobantes_Con_Desfase @RFC, @Anio, @Mes",
                new { RFC = rfc, Anio = year, Mes = month })).AsList();

            var desfaseTotals = await conn.QueryFirstOrDefaultAsync<DesfaseTotales>(
                "EXEC dbo.Declaracion_Comprobantes_Con_Desfase_Totales @RFC, @Anio, @Mes",
                new { RFC = rfc, Anio = year, Mes = month });

            var polizasNoConsolidadas = (await conn.QueryAsync<PolizaNoConsolidada>(
                "EXEC dbo.Polizas_No_Consolidadas @RFC, @Anio, @Mes",
                new { RFC = rfc, Anio = year, Mes = month })).AsList();

            var impuestosSummary = await conn.QueryFirstOrDefaultAsync<string>(
                "EXEC dbo.CALCULATE_TAXES @RFC, @startDate, @endDate",
                new { RFC = rfc, startDate = GetStartDate(year, month, isAnnual), endDate = GetEndDate(year, month, isAnnual) }) ?? "";

            var bancosCajaSummary = await conn.QueryFirstOrDefaultAsync<string>(
                "EXEC dbo.Reporte_Bancos_Caja @Year, @Month, @RFC",
                common) ?? "";

            var emitidasBase = allCfdiBase.Where(x => x.EsEmitida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante)).ToList();
            var recibidasBase = allCfdiBase.Where(x => x.EsRecibida && !IsNomina(x.TipoDeComprobante) && !IsTipoE(x.TipoDeComprobante)).ToList();
            var emitidasNominaBase = allCfdiBase.Where(x => x.EsEmitida && IsNomina(x.TipoDeComprobante)).ToList();
            var recibidasNominaBase = allCfdiBase.Where(x => x.EsRecibida && IsNomina(x.TipoDeComprobante)).ToList();
            var tipoEEmitidasBase = allCfdiBase.Where(x => x.EsEmitida && IsTipoE(x.TipoDeComprobante)).ToList();
            var tipoERecibidasBase = allCfdiBase.Where(x => x.EsRecibida && IsTipoE(x.TipoDeComprobante)).ToList();

            return new DeclaracionPreviaData
            {
                Emitidas = emitidasBase.Select(ToDeclaracionEmitida).ToList(),
                EmitidasTotals = ComputeDeclaracionTotales(emitidasBase),
                EmitidasNomina = emitidasNominaBase.Select(ToDeclaracionEmitida).ToList(),
                EmitidasNominaTotals = ComputeDeclaracionTotales(emitidasNominaBase),
                Recibidas = recibidasBase.Select(ToDeclaracionRecibida).ToList(),
                RecibidasTotals = ComputeDeclaracionTotales(recibidasBase),
                RecibidasNomina = recibidasNominaBase.Select(ToDeclaracionRecibida).ToList(),
                RecibidasNominaTotals = ComputeDeclaracionTotales(recibidasNominaBase),
                TipoEEmitidas = tipoEEmitidasBase.Select(ToDeclaracionEmitida).ToList(),
                TipoEEmitidasTotals = ComputeDeclaracionTotales(tipoEEmitidasBase),
                TipoERecibidas = tipoERecibidasBase.Select(ToDeclaracionRecibida).ToList(),
                TipoERecibidasTotals = ComputeDeclaracionTotales(tipoERecibidasBase),
                Desfase = desfase,
                DesfaseTotals = desfaseTotals ?? new DesfaseTotales(),
                PolizasNoConsolidadas = polizasNoConsolidadas,
                ImpuestosSummary = impuestosSummary,
                BancosCajaSummary = bancosCajaSummary
            };
        }

        private DeclaracionTotales ComputeDeclaracionTotales(IEnumerable<DeclaracionCfdiBase> items)
        {
            var list = items?.ToList() ?? new List<DeclaracionCfdiBase>();

            return new DeclaracionTotales
            {
                CountCFDIs = list.Count,
                SumSubTotal = SatRound(list.Sum(x => x.SubTotal)),
                SumDescuento = SatRound(list.Sum(x => x.Descuento)),
                SumSubTotalDesc = SatRound(list.Sum(x => x.SubTotal_Desc)),
                SumActos16 = SatRound(list.Sum(x => x.Actos_16)),
                SumActos0 = SatRound(list.Sum(x => x.Actos_0)),
                SumIVA = SatRound(list.Sum(x => x.IVA)),
                SumIEPS = SatRound(list.Sum(x => x.IEPS)),
                SumIVA_RETENIDO = SatRound(list.Sum(x => x.IVA_RETENIDO)),
                SumISR_RETENIDO = SatRound(list.Sum(x => x.ISR_RETENIDO)),
                SumIEPS_RETENIDO = SatRound(list.Sum(x => x.IEPS_RETENIDO)),
                SumTotal = SatRound(list.Sum(x => x.Total))
            };
        }

        private static DeclaracionEmitida ToDeclaracionEmitida(DeclaracionCfdiBase item) => new DeclaracionEmitida(item);

        private static DeclaracionRecibida ToDeclaracionRecibida(DeclaracionCfdiBase item) => new DeclaracionRecibida(item);

        private static decimal SatRound(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

        private static bool IsNomina(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "N", StringComparison.OrdinalIgnoreCase);

        private static bool IsTipoE(string? tipoDeComprobante) => string.Equals(tipoDeComprobante, "E", StringComparison.OrdinalIgnoreCase);

        private DateTime GetStartDate(int year, int month, bool isAnnual) => isAnnual ? new DateTime(year, 1, 1) : new DateTime(year, month, 1);
        private DateTime GetEndDate(int year, int month, bool isAnnual) => isAnnual ? new DateTime(year, 12, 31) : new DateTime(year, month, DateTime.DaysInMonth(year, month));
    }
}
