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
      detail.Lines.Count.ToString("N0", culture),
      detail.Lines.Sum(line => line.Allocations.Count).ToString("N0", culture),
      detail.Lines.Sum(line => line.Allocations.Count(allocation => allocation.RemainingQuantity > 0m)).ToString("N0", culture),
      detail.Lines.Select(line => new PurchaseOrderPdfLineRow(
          GetThumbnail(line.MaterialId),
          GetFallback(line),
          Safe(line.MaterialCode),
          Safe(line.MaterialDescription),
          Safe(line.VendorCode),
          PurchaseQuantityDisplay.GetPrimaryUnitName(line.BaseUnitName, line.PurchaseUnitName),
          BuildPurchasePresentation(line, culture),
          line.UnitPrice.HasValue ? line.UnitPrice.Value.ToString("C", culture) : "-",
          FormatPurchaseQuantity(line.OrderedQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture),
          FormatPurchaseQuantity(line.ReceivedQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture),
          FormatPurchaseQuantity(line.RemainingQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture)))
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
          FormatPurchaseQuantity(allocation.PlannedQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture),
          FormatPurchaseQuantity(allocation.ReceivedQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture),
          FormatPurchaseQuantity(allocation.RemainingQuantity, line.PurchaseQuantity, line.BaseUnitName, line.PurchaseUnitName, culture))))
        .OrderBy(row => row.LocationName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.MaterialCode, StringComparer.OrdinalIgnoreCase)
        .ThenBy(row => row.MaterialDescription, StringComparer.OrdinalIgnoreCase)
        .ToList());
  }

  private static string FormatDate(DateTime? value, CultureInfo culture)
    => value.HasValue ? value.Value.ToString("d", culture) : "-";

  private static string FormatQuantity(decimal value, CultureInfo culture)
    => value.ToString("N2", culture);

  private static string FormatPurchaseQuantity(
    decimal value,
    decimal purchaseQuantity,
    string? baseUnitName,
    string? purchaseUnitName,
    CultureInfo culture)
    => PurchaseQuantityDisplay.FormatQuantity(value, purchaseQuantity, baseUnitName, purchaseUnitName, culture);

  private static string BuildPurchasePresentation(PurchaseOrderLineDto line, CultureInfo culture)
    => PurchaseQuantityDisplay.BuildPresentationSummary(
      line.BaseUnitName,
      line.PurchaseQuantity,
      line.PurchaseUnitName,
      culture)
      ?? string.Empty;

  private static string Safe(string? value)
    => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
}
