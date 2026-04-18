namespace OrionERP.Web.Features.Logistica.Purchasing;

public interface IPurchaseMaterialThumbnailHydrator
{
  Task<IReadOnlyDictionary<int, string>> GetDataUrlsAsync(IEnumerable<int> materialIds, CancellationToken ct = default);
}
