using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using OrionERP.Application.Features.ReportesFinancieros.Models;

namespace OrionERP.Application.Features.ReportesFinancieros
{
    public interface IReportesFinancierosService
    {
        Task<HojaTrabajoViewModel> GetHojaTrabajoAsync(int anio, string rfc);

        /// <summary>
        /// Balanza de comprobación. <paramref name="soloPublicadas"/> en <c>false</c> es
        /// el reporte vigente, con toda la historia; en <c>true</c> agrega únicamente
        /// asientos del ciclo formal de E4. Son dos reportes distintos e identificables,
        /// no uno que cambia de significado en silencio.
        /// </summary>
        Task<IReadOnlyList<BalanzaComprobacionRow>> GetBalanzaComprobacionAsync(
            int anio,
            int? mes,
            string? rfc,
            bool soloPublicadas = false);

        /// <inheritdoc cref="GetBalanzaComprobacionAsync"/>
        Task<IReadOnlyList<EstadoPerdidasGananciasRow>> GetEstadoPerdidasGananciasAsync(
            DateTime startDate,
            DateTime endDate,
            string? rfc,
            bool soloPublicadas = false);

        /// <summary>
        /// Si la variante publicada tiene sentido para la empresa de la sesión, y qué
        /// falta para adoptarla como oficial. Mientras el ciclo esté apagado devuelve
        /// no disponible con el motivo, para que la pantalla no ofrezca un reporte que
        /// sólo puede salir en cero.
        /// </summary>
        Task<PublishedReportAvailability> GetPublishedReportAvailabilityAsync(
            string? rfc,
            CancellationToken ct = default);

        Task<SaludEmpresaReport> GetSaludEmpresaAsync(
            int anioInicio,
            int mesInicio,
            int anioFin,
            int mesFin,
            string? rfc);

        Task<SaludEmpresaReport> GetSaludEmpresaAsync(
            SaludEmpresaQuery query,
            CancellationToken cancellationToken = default);

        Task<SaludEmpresaReconciliationPage> GetSaludEmpresaReconciliationAsync(
            SaludEmpresaReconciliationQuery query,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<SaludEmpresaTarget>> GetSaludEmpresaTargetsAsync(
            string rfc,
            DateTime startMonth,
            DateTime endMonth,
            CancellationToken cancellationToken = default);

        Task SaveSaludEmpresaTargetAsync(
            SaludEmpresaTarget target,
            string userName,
            CancellationToken cancellationToken = default);

        Task<SaludEmpresaConfiguration> GetSaludEmpresaConfigurationAsync(
            string rfc,
            CancellationToken cancellationToken = default);

        Task SaveSaludEmpresaConfigurationAsync(
            SaludEmpresaConfiguration configuration,
            string userName,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<SaludEmpresaRoomConfiguration>> GetSaludEmpresaRoomsAsync(
            CancellationToken cancellationToken = default);

        Task SaveSaludEmpresaRoomAsync(
            SaludEmpresaRoomConfiguration room,
            CancellationToken cancellationToken = default);

    }

    public class HojaTrabajoLongRowDto
    {
        public string? Descripcion { get; set; }
        public int Mes { get; set; }
        public decimal Monto { get; set; }
        public int Orden { get; set; }
    }

    public class HojaTrabajoTablaDto
    {
        public string? Descripcion { get; set; }
        public int Orden { get; set; }
        public decimal ENERO { get; set; }
        public decimal FEBRERO { get; set; }
        public decimal MARZO { get; set; }
        public decimal ABRIL { get; set; }
        public decimal MAYO { get; set; }
        public decimal JUNIO { get; set; }
        public decimal JULIO { get; set; }
        public decimal AGOSTO { get; set; }
        public decimal SEPTIEMBRE { get; set; }
        public decimal OCTUBRE { get; set; }
        public decimal NOVIEMBRE { get; set; }
        public decimal DICIEMBRE { get; set; }
    }

    public class HojaTrabajoViewModel
    {
        public List<HojaTrabajoTablaDto> Cfdi { get; set; } = new();
        public List<HojaTrabajoTablaDto> Complementos { get; set; } = new();
        public List<HojaTrabajoTablaDto> Contabilidad { get; set; } = new();
        public List<HojaTrabajoTablaDto> Acumulados { get; set; } = new();
        public List<HojaTrabajoTablaDto> TipoE { get; set; } = new();
        public List<HojaTrabajoTablaDto> TipoN { get; set; } = new();
    }
}
