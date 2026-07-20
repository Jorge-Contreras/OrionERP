using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Web.State;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public sealed class PurchaseMaterialThumbnailHydrator : IPurchaseMaterialThumbnailHydrator
{
  private readonly IMaterialService _materialService;
  private readonly IUserRfcState _rfcState;

  public PurchaseMaterialThumbnailHydrator(IMaterialService materialService, IUserRfcState rfcState)
  {
    _materialService = materialService ?? throw new ArgumentNullException(nameof(materialService));
    _rfcState = rfcState ?? throw new ArgumentNullException(nameof(rfcState));
  }

  public async Task<IReadOnlyDictionary<int, string>> GetDataUrlsAsync(IEnumerable<int> materialIds, CancellationToken ct = default)
  {
    var ids = materialIds?
      .Where(materialId => materialId > 0)
      .Distinct()
      .ToArray() ?? [];

    if (ids.Length == 0)
    {
      return new Dictionary<int, string>();
    }

    var thumbnails = await _materialService.GetMaterialThumbnailsAsync(LogisticsRfc.Require(_rfcState.CurrentRfc), ids, ct);
    return thumbnails
      .Where(thumbnail => thumbnail.Bytes.Length > 0)
      .ToDictionary(
        thumbnail => thumbnail.Id,
        thumbnail => BuildDataUrl(thumbnail.ContentType, thumbnail.Bytes));
  }

  private static string BuildDataUrl(string? contentType, byte[] bytes)
  {
    var safeContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
    return FormattableString.Invariant($"data:{safeContentType};base64,{Convert.ToBase64String(bytes)}");
  }
}
