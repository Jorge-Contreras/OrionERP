using OrionERP.Infrastructure.Features.Reservaciones;
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
using System.Threading;

namespace OrionERP.Infrastructure.Features.ReportesFinancieros.Dapper
{
    public class ReportesFinancierosService : IReportesFinancierosService
    {
        private readonly IDbConnectionFactory _connectionFactory;
        private readonly HospitalityConnectionFactory? _hospitalityConnections;
        private readonly HospitalityAdministrationScopeAccessor? _hospitalityScope;
        private readonly ICurrentCompanyContext? _companyContext;

        public ReportesFinancierosService(IDbConnectionFactory connectionFactory, HospitalityConnectionFactory? hospitalityConnections = null,
            HospitalityAdministrationScopeAccessor? hospitalityScope = null, ICurrentCompanyContext? companyContext = null)
        {
            _connectionFactory = connectionFactory;
            _hospitalityConnections = hospitalityConnections;
            _hospitalityScope = hospitalityScope;
            _companyContext = companyContext;
        }

        private void EnsureCompanyRfc(string? rfc)
        {
            if (string.IsNullOrWhiteSpace(rfc))
                throw new UnauthorizedAccessException("El reporte requiere la empresa de la sesion.");
            (_companyContext ?? throw new UnauthorizedAccessException("Falta la empresa autorizada.")).EnsureRfc(rfc);
        }

        private async Task<IDbConnection> OpenSaludConnectionAsync(string rfc, CancellationToken ct)
        {
            EnsureCompanyRfc(rfc);
            var sites = await (_hospitalityScope ?? throw new UnauthorizedAccessException("Falta el contexto de Hospedaje.")).GetSitesAsync(ct);
            if (sites.Count > 0)
                return await (_hospitalityConnections ?? throw new UnauthorizedAccessException("Falta el alcance de Hospedaje.")).OpenAsync(ct);
            var connection = _connectionFactory.Create();
            try { await OpenConnectionAsync(connection, ct); return connection; }
            catch { connection.Dispose(); throw; }
        }

        public async Task<IReadOnlyList<BalanzaComprobacionRow>> GetBalanzaComprobacionAsync(
            int anio,
            int? mes,
            string? rfc,
            bool soloPublicadas = false)
        {
            EnsureCompanyRfc(rfc);
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new { Anio = anio, Mes = mes, Rfc = rfc, SoloPublicadas = soloPublicadas };

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
            string? rfc,
            bool soloPublicadas = false)
        {
            EnsureCompanyRfc(rfc);
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection).ConfigureAwait(false);

            var parameters = new { startDate, endDate, RFC = rfc, SoloPublicadas = soloPublicadas };

            var result = await connection.QueryAsync<EstadoPerdidasGananciasRow>(
                "reporteFinanciero.ESTADO_PERDIDAS_GANANCIAS",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 30).ConfigureAwait(false);

            return result.AsList();
        }

        /// <summary>
        /// Lo que falta para adoptar la variante publicada como oficial. No la ofrece
        /// mientras el ciclo esté apagado: saldría en cero y parecería que la historia
        /// no existe.
        /// </summary>
        public async Task<PublishedReportAvailability> GetPublishedReportAvailabilityAsync(
            string? rfc,
            CancellationToken ct = default)
        {
            EnsureCompanyRfc(rfc);
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection, ct).ConfigureAwait(false);

            var installed = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT CONVERT(bit, CASE
                  WHEN OBJECT_ID(N'contabilidad.CompanyCycleActivation', N'U') IS NULL
                    OR COL_LENGTH(N'dbo.Transacciones', N'CycleState') IS NULL
                  THEN 0 ELSE 1 END);
                """, cancellationToken: ct)).ConfigureAwait(false);
            if (!installed)
            {
                return new PublishedReportAvailability(false, false, false, 0, 0,
                    "El ciclo contable formal no está instalado en esta base de datos.");
            }

            var row = await connection.QuerySingleAsync<AvailabilityRow>(new CommandDefinition("""
                SELECT
                  CONVERT(bit, CASE WHEN EXISTS
                  (
                    SELECT 1 FROM contabilidad.CompanyCycleActivation activacion
                    JOIN orion.Company empresa ON empresa.CompanyId = activacion.CompanyId
                    WHERE empresa.Rfc = @Rfc AND activacion.IsEnabled = 1
                  ) THEN 1 ELSE 0 END) AS CycleEnabled,
                  CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones
                                   WHERE RFC = @Rfc AND CycleState IN ('Posted', 'Reversed'))) AS PublishedEntryCount,
                  CONVERT(bigint, (SELECT COUNT_BIG(*) FROM dbo.Transacciones WHERE RFC = @Rfc)) AS TotalEntryCount;
                """, new { Rfc = rfc }, cancellationToken: ct)).ConfigureAwait(false);

            var missing = !row.CycleEnabled
                ? "La empresa no tiene el ciclo contable activado; hay que aprobar su baseline primero."
                : row.PublishedEntryCount == 0
                    ? "La empresa tiene el ciclo activado pero ninguna póliza publicada todavía."
                    : row.PublishedEntryCount < row.TotalEntryCount
                        ? $"Quedan {row.TotalEntryCount - row.PublishedEntryCount} pólizas fuera del ciclo; el reporte vigente sigue siendo el oficial hasta conciliarlas."
                        : "Nada: todas las pólizas de la empresa están dentro del ciclo.";

            return new PublishedReportAvailability(
                row.CycleEnabled && row.PublishedEntryCount > 0,
                true,
                row.CycleEnabled,
                row.PublishedEntryCount,
                row.TotalEntryCount,
                missing);
        }

        private sealed class AvailabilityRow
        {
            public bool CycleEnabled { get; set; }
            public long PublishedEntryCount { get; set; }
            public long TotalEntryCount { get; set; }
        }

        public async Task<SaludEmpresaReport> GetSaludEmpresaAsync(
            int anioInicio,
            int mesInicio,
            int anioFin,
            int mesFin,
            string? rfc)
        {
            return await GetSaludEmpresaAsync(
                new SaludEmpresaQuery(anioInicio, mesInicio, anioFin, mesFin, rfc ?? string.Empty),
                CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<SaludEmpresaReport> GetSaludEmpresaAsync(
            SaludEmpresaQuery query,
            CancellationToken cancellationToken = default)
        {
            using var connection = await OpenSaludConnectionAsync(query.Rfc, cancellationToken);

            var parameters = new
            {
                AnioInicio = query.StartYear,
                MesInicio = query.StartMonth,
                AnioFin = query.EndYear,
                MesFin = query.EndMonth,
                RFC = query.Rfc,
                IncluirHabitacionesNoRentables = query.IncludeNonRentableRooms,
                FechaCorte = query.CutoffDate?.Date
            };

            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
                "reporteFinanciero.Reporte_Salud_Empresa",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var executiveIndicators = (await multi.ReadAsync<SaludEmpresaExecutiveIndicatorRow>().ConfigureAwait(false)).AsList();
            var suitePerformance = (await multi.ReadAsync<SaludEmpresaSuitePerformanceRow>().ConfigureAwait(false)).AsList();
            var financialBreakdown = (await multi.ReadAsync<SaludEmpresaFinancialBreakdownRow>().ConfigureAwait(false)).AsList();
            var cashFlow = (await multi.ReadAsync<SaludEmpresaCashFlowRow>().ConfigureAwait(false)).AsList();
            var dataQuality = (await multi.ReadAsync<SaludEmpresaDataQualityRow>().ConfigureAwait(false)).AsList();
            var metadata = await multi.ReadFirstOrDefaultAsync<SaludEmpresaMetadata>().ConfigureAwait(false);
            var trends = (await multi.ReadAsync<SaludEmpresaTrendRow>().ConfigureAwait(false)).AsList();
            var revenueMix = (await multi.ReadAsync<SaludEmpresaRevenueMixRow>().ConfigureAwait(false)).AsList();
            var expenses = (await multi.ReadAsync<SaludEmpresaExpenseRow>().ConfigureAwait(false)).AsList();
            var liquidity = (await multi.ReadAsync<SaludEmpresaLiquidityRow>().ConfigureAwait(false)).AsList();
            var targetVariances = (await multi.ReadAsync<SaludEmpresaTargetVarianceRow>().ConfigureAwait(false)).AsList();
            var dailyOutlook = (await multi.ReadAsync<SaludEmpresaOutlookDailyRow>().ConfigureAwait(false)).AsList();
            var monthlyOutlook = (await multi.ReadAsync<SaludEmpresaOutlookMonthlyRow>().ConfigureAwait(false)).AsList();

            return new SaludEmpresaReport(
                executiveIndicators,
                suitePerformance,
                financialBreakdown,
                cashFlow,
                dataQuality,
                metadata,
                trends,
                revenueMix,
                expenses,
                liquidity,
                targetVariances,
                dailyOutlook,
                monthlyOutlook);
        }

        public async Task<SaludEmpresaReconciliationPage> GetSaludEmpresaReconciliationAsync(
            SaludEmpresaReconciliationQuery query,
            CancellationToken cancellationToken = default)
        {
            using var connection = await OpenSaludConnectionAsync(query.Rfc, cancellationToken);
            var parameters = new
            {
                RFC = query.Rfc,
                FechaInicio = query.StartDate.Date,
                FechaFin = query.EndDate.Date,
                Pagina = query.Page,
                TamanoPagina = query.PageSize,
                Severidad = query.Severity,
                Tipo = query.Type,
                Busqueda = query.Search
            };

            using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
                "reporteFinanciero.Reporte_Salud_Empresa_Conciliacion",
                parameters,
                commandType: CommandType.StoredProcedure,
                commandTimeout: 60,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            var items = (await multi.ReadAsync<SaludEmpresaReconciliationRow>().ConfigureAwait(false)).AsList();
            var counts = await multi.ReadFirstAsync<ReconciliationCounts>().ConfigureAwait(false);
            return new SaludEmpresaReconciliationPage
            {
                Items = items,
                Page = Math.Max(1, query.Page),
                PageSize = query.PageSize is >= 1 and <= 100 ? query.PageSize : 25,
                TotalCount = counts.TotalCount,
                HighCount = counts.HighCount,
                MediumCount = counts.MediumCount,
                LowCount = counts.LowCount
            };
        }

        public async Task<IReadOnlyList<SaludEmpresaTarget>> GetSaludEmpresaTargetsAsync(
            string rfc,
            DateTime startMonth,
            DateTime endMonth,
            CancellationToken cancellationToken = default)
        {
            EnsureCompanyRfc(rfc);
            const string sql = """
;WITH Months AS
(
  SELECT @StartMonth AS Mes
  UNION ALL
  SELECT DATEADD(MONTH,1,Mes) FROM Months WHERE Mes<@EndMonth
)
MERGE reporteFinanciero.SaludEmpresaMeta WITH (HOLDLOCK) AS target
USING Months AS source ON source.Mes=target.Mes AND target.RFC=@Rfc
WHEN NOT MATCHED THEN
  INSERT (RFC,Mes,ActualizadoPor) VALUES (@Rfc,source.Mes,N'Sistema Salud Financiera v2');

SELECT MetaID TargetId,RFC,Mes [Month],IngresoHabitacionMeta RoomRevenueTarget,
  IngresoComplementarioMeta ComplementaryRevenueTarget,OcupacionPctMeta OccupancyPctTarget,
  ADRMeta AdrTarget,GastosOperativosMeta OperatingExpensesTarget,ResultadoNetoMeta NetResultTarget,
  FlujoNetoMeta NetCashFlowTarget,SaldoEfectivoMeta ClosingCashTarget,Notas Notes,
  ActualizadoPor UpdatedBy,ActualizadoUtc UpdatedAtUtc,RowVersion
FROM reporteFinanciero.SaludEmpresaMeta
WHERE RFC=@Rfc AND Mes>=@StartMonth AND Mes<=@EndMonth
ORDER BY Mes
OPTION (MAXRECURSION 1000);
""";
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            var rows = await connection.QueryAsync<SaludEmpresaTarget>(new CommandDefinition(
                sql,
                new { Rfc = rfc, StartMonth = new DateTime(startMonth.Year, startMonth.Month, 1), EndMonth = new DateTime(endMonth.Year, endMonth.Month, 1) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }

        public async Task SaveSaludEmpresaTargetAsync(
            SaludEmpresaTarget target,
            string userName,
            CancellationToken cancellationToken = default)
        {
            EnsureCompanyRfc(target.Rfc);
            const string sql = """
UPDATE reporteFinanciero.SaludEmpresaMeta
SET IngresoHabitacionMeta=@RoomRevenueTarget,IngresoComplementarioMeta=@ComplementaryRevenueTarget,
    OcupacionPctMeta=@OccupancyPctTarget,ADRMeta=@AdrTarget,GastosOperativosMeta=@OperatingExpensesTarget,
    ResultadoNetoMeta=@NetResultTarget,FlujoNetoMeta=@NetCashFlowTarget,SaldoEfectivoMeta=@ClosingCashTarget,
    Notas=@Notes,ActualizadoPor=@UserName,ActualizadoUtc=SYSUTCDATETIME()
WHERE MetaID=@TargetId AND RFC=@Rfc AND (@HasVersion=0 OR RowVersion=@RowVersion);
IF @@ROWCOUNT=0 THROW 51101,'La meta cambio desde que fue consultada. Recarga antes de guardar.',1;
""";
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                target.TargetId,
                target.Rfc,
                target.RoomRevenueTarget,
                target.ComplementaryRevenueTarget,
                target.OccupancyPctTarget,
                target.AdrTarget,
                target.OperatingExpensesTarget,
                target.NetResultTarget,
                target.NetCashFlowTarget,
                target.ClosingCashTarget,
                target.Notes,
                UserName = string.IsNullOrWhiteSpace(userName) ? "Sistema" : userName.Trim(),
                HasVersion = target.RowVersion.Length > 0,
                target.RowVersion
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public async Task<SaludEmpresaConfiguration> GetSaludEmpresaConfigurationAsync(
            string rfc,
            CancellationToken cancellationToken = default)
        {
            EnsureCompanyRfc(rfc);
            const string sql = """
MERGE reporteFinanciero.SaludEmpresaConfiguracion WITH (HOLDLOCK) AS target
USING (SELECT @Rfc RFC) AS source ON source.RFC=target.RFC
WHEN NOT MATCHED THEN
  INSERT (RFC,HospedajeHabilitado,RetencionArrendadorPct,ActualizadoPor)
  VALUES
  (
    source.RFC,
    CASE WHEN EXISTS
    (
      SELECT 1
      FROM orion.Company company
      JOIN orion.CompanyModule companyModule
        ON companyModule.CompanyId=company.CompanyId
       AND companyModule.ModuleCode='HOSPITALITY'
      WHERE company.Rfc=source.RFC AND company.IsActive=1
        AND companyModule.[Status]='Enabled'
        AND (companyModule.EffectiveFromUtc IS NULL OR companyModule.EffectiveFromUtc<=SYSUTCDATETIME())
        AND (companyModule.EffectiveToUtc IS NULL OR companyModule.EffectiveToUtc>SYSUTCDATETIME())
    ) THEN 1 ELSE 0 END,
    10,
    N'Sistema Salud Financiera v2'
  );

SELECT RFC,HospedajeHabilitado LodgingEnabled,RetencionArrendadorPct OwnerWithholdingPct,
       ActualizadoUtc UpdatedAtUtc,ActualizadoPor UpdatedBy
FROM reporteFinanciero.SaludEmpresaConfiguracion WHERE RFC=@Rfc;
""";
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return await connection.QuerySingleAsync<SaludEmpresaConfiguration>(new CommandDefinition(
                sql, new { Rfc = rfc }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public async Task SaveSaludEmpresaConfigurationAsync(
            SaludEmpresaConfiguration configuration,
            string userName,
            CancellationToken cancellationToken = default)
        {
            EnsureCompanyRfc(configuration.Rfc);
            const string sql = """
MERGE reporteFinanciero.SaludEmpresaConfiguracion WITH (HOLDLOCK) AS target
USING (SELECT @Rfc RFC) AS source ON source.RFC=target.RFC
WHEN MATCHED THEN UPDATE SET
  HospedajeHabilitado=@LodgingEnabled,RetencionArrendadorPct=@OwnerWithholdingPct,
  ActualizadoPor=@UserName,ActualizadoUtc=SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT
  (RFC,HospedajeHabilitado,RetencionArrendadorPct,ActualizadoPor)
VALUES (@Rfc,@LodgingEnabled,@OwnerWithholdingPct,@UserName);
""";
            using var connection = _connectionFactory.Create();
            await OpenConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                configuration.Rfc,
                configuration.LodgingEnabled,
                configuration.OwnerWithholdingPct,
                UserName = string.IsNullOrWhiteSpace(userName) ? "Sistema" : userName.Trim()
            }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<SaludEmpresaRoomConfiguration>> GetSaludEmpresaRoomsAsync(
            CancellationToken cancellationToken = default)
        {
            const string sql = """
SELECT ID RoomId,ROOM_NAME RoomName,ROOM_TYPE RoomType,CAST(ISNULL(BASE_PRICE,0) AS decimal(19,2)) BasePrice,
       IsActive,IsRentable
FROM dbo.ROOM ORDER BY IsRentable DESC,ROOM_NAME;
""";
            using var connection = await (_hospitalityConnections ?? throw new UnauthorizedAccessException("Falta el alcance autorizado de Hospedaje.")).OpenAsync(cancellationToken);
            var rows = await connection.QueryAsync<SaludEmpresaRoomConfiguration>(new CommandDefinition(
                sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }

        public async Task SaveSaludEmpresaRoomAsync(
            SaludEmpresaRoomConfiguration room,
            CancellationToken cancellationToken = default)
        {
            const string sql = "UPDATE dbo.ROOM SET IsActive=@IsActive,IsRentable=@IsRentable WHERE ID=@RoomId;";
            using var connection = await (_hospitalityConnections ?? throw new UnauthorizedAccessException("Falta el alcance autorizado de Hospedaje.")).OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(sql, new { room.RoomId, room.IsActive, room.IsRentable }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        public async Task<HojaTrabajoViewModel> GetHojaTrabajoAsync(int anio, string rfc)
        {
            EnsureCompanyRfc(rfc);
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

        private static async Task OpenConnectionAsync(IDbConnection connection, CancellationToken cancellationToken = default)
        {
            if (connection is DbConnection dbConnection)
            {
                await dbConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            connection.Open();
        }

        private sealed class ReconciliationCounts
        {
            public int TotalCount { get; set; }
            public int HighCount { get; set; }
            public int MediumCount { get; set; }
            public int LowCount { get; set; }
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
