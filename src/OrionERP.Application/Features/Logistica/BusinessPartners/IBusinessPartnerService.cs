using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.BusinessPartners;

public interface IBusinessPartnerService
{
  Task<IReadOnlyList<BusinessPartnerListItemDto>> GetPartnersAsync(BusinessPartnerFilter filter, CancellationToken ct = default);
  Task<BusinessPartnerDetailDto?> GetPartnerAsync(string rfc, int businessPartnerId, CancellationToken ct = default);
  Task<IReadOnlyList<LookupOptionDto>> GetVendorLookupAsync(string rfc, CancellationToken ct = default);
  Task<BusinessPartnerCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default);
  Task<LogisticsCommandResult> SavePartnerAsync(BusinessPartnerUpsertRequest request, CancellationToken ct = default);
}
