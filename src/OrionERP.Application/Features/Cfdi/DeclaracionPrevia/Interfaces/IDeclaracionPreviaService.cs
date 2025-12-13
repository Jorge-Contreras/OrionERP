using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces
{
    public interface IDeclaracionPreviaService
    {
        Task<DeclaracionPreviaData> GetDeclaracionPreviaDataAsync(int year, int month, bool isAnnual, string rfc);
        Task<int> GenerarPolizaDesdeComprobanteAsync(int comprobanteId, string rfc);
        Task<string> CancelarCfdiAsync(int comprobanteId, string uuid, string facturamaUser, string facturamaPassword);
        Task ToggleInclusionAsync(int comprobanteId);
        Task<int> ExcludePagosYDevolucionesAsync(string rfc, int year, int? month);
        Task<List<PagoComplementoResumen>> GetComplementosAsync(Guid uuid);
        Task<long?> GetLinkedTransactionIdAsync(int comprobanteId);
        Task<string> GenerateDiotAsync(int year, int month, string rfc);
    }
}
