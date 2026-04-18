namespace OrionERP.Web.Features.Logistica.Purchasing;

public sealed record PurchaseOrderPdfDocumentModel(
  string PurchaseOrderCode,
  string VendorName,
  string VendorRfc,
  string Status,
  string OrderDate,
  string ExpectedDate,
  string Notes,
  string GeneratedAt,
  string CreatedBy,
  string OrderedQuantity,
  string ReceivedQuantity,
  string RemainingQuantity,
  IReadOnlyList<PurchaseOrderPdfLineRow> Lines,
  IReadOnlyList<PurchaseOrderPdfAllocationRow> Allocations);

public sealed record PurchaseOrderPdfLineRow(
  byte[]? ThumbnailBytes,
  string ThumbnailFallback,
  string MaterialCode,
  string MaterialDescription,
  string VendorCode,
  string UnitName,
  string PurchasePresentation,
  string UnitPrice,
  string OrderedQuantity,
  string ReceivedQuantity,
  string RemainingQuantity);

public sealed record PurchaseOrderPdfAllocationRow(
  byte[]? ThumbnailBytes,
  string ThumbnailFallback,
  string MaterialCode,
  string MaterialDescription,
  string LocationName,
  string PlannedQuantity,
  string ReceivedQuantity,
  string RemainingQuantity);
