using OrionERP.Application.Features.Cfdi.DeclaracionPrevia.DTOs;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia.Interfaces
{
    public interface IDeclaracionPreviaService
    {
        Task<DeclaracionPreviaData> GetDeclaracionPreviaDataAsync(int year, int month, bool isAnnual, string rfc);
        Task<int> GenerarPolizaDesdeComprobanteAsync(int comprobanteId, string rfc);
    }
}
