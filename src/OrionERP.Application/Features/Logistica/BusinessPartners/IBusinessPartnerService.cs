using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.BusinessPartners;

public interface IBusinessPartnerService
{
  Task<IReadOnlyList<BusinessPartnerListItemDto>> GetPartnersAsync(BusinessPartnerFilter filter, CancellationToken ct = default);
  Task<BusinessPartnerDetailDto?> GetPartnerAsync(int businessPartnerId, CancellationToken ct = default);
  Task<IReadOnlyList<LookupOptionDto>> GetVendorLookupAsync(CancellationToken ct = default);
  Task<BusinessPartnerCatalogDto> GetCatalogAsync(CancellationToken ct = default);
  Task<LogisticsCommandResult> SavePartnerAsync(BusinessPartnerUpsertRequest request, CancellationToken ct = default);
}
