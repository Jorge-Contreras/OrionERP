using System.Globalization;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Purchasing;

namespace OrionERP.Web.Features.Logistica.Purchasing;

public sealed class PurchaseOrderPdfDocumentFactory : IPurchaseOrderPdfDocumentFactory
{
  private readonly IMaterialService _materialService;

  public PurchaseOrderPdfDocumentFactory(IMaterialService materialService)
  {
    _materialService = materialService ?? throw new ArgumentNullException(nameof(materialService));
  }

  public async Task<PurchaseOrderPdfDocumentModel> CreateFromDetailAsync(PurchaseOrderDetailDto detail, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(detail);

    var culture = CultureInfo.CurrentCulture;
    var materialIds = detail.Lines
      .Select(line => line.MaterialId)
      .Distinct()
      .ToArray();

    var thumbnails = (await _materialService.GetMaterialThumbnailsAsync(materialIds, ct))
      .Where(thumbnail => thumbnail.Bytes.Length > 0)
      .ToDictionary(thumbnail => thumbnail.Id, thumbnail => thumbnail.Bytes);

    byte[]? GetThumbnail(int materialId)
      => thumbnails.TryGetValue(materialId, out var bytes) ? bytes : null;

    string GetFallback(PurchaseOrderLineDto line)
      => string.IsNullOrWhiteSpace(line.MaterialCode)
        ? Safe(line.MaterialDescription)
        : Safe(line.MaterialCode);

    return new PurchaseOrderPdfDocumentModel(
      detail.PurchaseOrderCode,
      Safe(detail.VendorName),
      Safe(detail.VendorRfc),
      Safe(detail.Status),
      FormatDate(detail.OrderDate, culture),
      FormatDate(detail.ExpectedDate, culture),
      Safe(detail.Notes),
      DateTime.Now.ToString("f", culture),
      Safe(detail.CreatedBy),
      FormatQuantity(detail.OrderedQuantity, culture),
      FormatQuantity(detail.ReceivedQuantity, culture),
      FormatQuantity(detail.RemainingQuantity, culture),
      detail.Lines.Select(line => new PurchaseOrderPdfLineRow(
          GetThumbnail(line.MaterialId),
          GetFallback(line),
          Safe(line.MaterialCode),
          Safe(line.MaterialDescription),
          Safe(line.VendorCode),
          Safe(line.BaseUnitName),
          BuildPurchasePresentation(line, culture),
          line.UnitPrice.HasValue ? line.UnitPrice.Value.ToString("C", culture) : "-",
          FormatQuantity(line.OrderedQuantity, culture),
          FormatQuantity(line.ReceivedQuantity, culture),
          FormatQuantity(line.RemainingQuantity, culture)))
        .ToList(),
      detail.Lines
        .SelectMany(line => line.Allocations.Select(allocation => new PurchaseOrderPdfAllocationRow(
          GetThumbnail(line.MaterialId),
          GetFallback(line),
          Safe(line.MaterialCode),
          Safe(line.MaterialDescription),
          string.IsNullOrWhiteSpace(allocation.LocationCode)
            ? Safe(allocation.LocationName)
            : $"{allocation.LocationCode} · {Safe(allocation.LocationName)}",
          FormatQuantity(allocation.PlannedQuantity, culture),
          FormatQuantity(allocation.ReceivedQuantity, culture),
          FormatQuantity(allocation.RemainingQuantity, culture))))
        .ToList());
  }

  private static string FormatDate(DateTime? value, CultureInfo culture)
    => value.HasValue ? value.Value.ToString("d", culture) : "-";

  private static string FormatQuantity(decimal value, CultureInfo culture)
    => value.ToString("N2", culture);

  private static string BuildPurchasePresentation(PurchaseOrderLineDto line, CultureInfo culture)
  {
    if (line.PurchaseQuantity <= 1m
        || string.IsNullOrWhiteSpace(line.PurchaseUnitName)
        || string.IsNullOrWhiteSpace(line.BaseUnitName))
    {
      return string.Empty;
    }

    var orderedPurchaseUnits = line.OrderedQuantity / line.PurchaseQuantity;
    return $"{FormatQuantity(orderedPurchaseUnits, culture)} {Safe(line.PurchaseUnitName)} x {FormatQuantity(line.PurchaseQuantity, culture)} {Safe(line.BaseUnitName)} = {FormatQuantity(line.OrderedQuantity, culture)} {Safe(line.BaseUnitName)}";
  }

  private static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
