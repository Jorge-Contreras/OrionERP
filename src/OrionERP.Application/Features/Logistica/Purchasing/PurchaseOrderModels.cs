using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Materials;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Purchasing;

public static class PurchaseOrderStatuses
{
  public const string Draft = "Draft";
  public const string Issued = "Issued";
  public const string PartiallyReceived = "PartiallyReceived";
  public const string Completed = "Completed";
  public const string Cancelled = "Cancelled";

  public static readonly IReadOnlyList<string> All =
  [
    Draft,
    Issued,
    PartiallyReceived,
    Completed,
    Cancelled
  ];

  public static readonly IReadOnlyList<string> Open =
  [
    Issued,
    PartiallyReceived
  ];
}

public sealed class PurchaseOrderFilter
{
  public string? SearchText { get; set; }
  public int? VendorId { get; set; }
  public string? Status { get; set; }
  public bool OpenOnly { get; set; }
  public int Skip { get; set; }
  public int Take { get; set; }
}

public sealed class PurchaseOrderListItemDto
{
  public int Id { get; set; }
  public string PurchaseOrderCode { get; set; } = string.Empty;
  public int BusinessPartnerId { get; set; }
  public string VendorName { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public DateTime OrderDate { get; set; }
  public DateTime? ExpectedDate { get; set; }
  public decimal OrderedQuantity { get; set; }
  public decimal ReceivedQuantity { get; set; }
  public decimal RemainingQuantity { get; set; }
  public int LineCount { get; set; }
  public int AllocationCount { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? UpdatedAt { get; set; }
}

public sealed class PurchaseOrderDetailDto
{
  public int Id { get; set; }
  public string PurchaseOrderCode { get; set; } = string.Empty;
  public int BusinessPartnerId { get; set; }
  public string VendorName { get; set; } = string.Empty;
  public string? VendorRfc { get; set; }
  public string Status { get; set; } = string.Empty;
  public DateTime OrderDate { get; set; }
  public DateTime? ExpectedDate { get; set; }
  public string? Notes { get; set; }
  public decimal OrderedQuantity { get; set; }
  public decimal ReceivedQuantity { get; set; }
  public decimal RemainingQuantity { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
  public DateTime? UpdatedAt { get; set; }
  public string? UpdatedBy { get; set; }
  public DateTime? IssuedAt { get; set; }
  public string? IssuedBy { get; set; }
  public DateTime? CompletedAt { get; set; }
  public string? CompletedBy { get; set; }
  public DateTime? CancelledAt { get; set; }
  public string? CancelledBy { get; set; }
  public IReadOnlyList<LookupOptionDto> RoomScope { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<PurchaseOrderLineDto> Lines { get; set; } = Array.Empty<PurchaseOrderLineDto>();
  public IReadOnlyList<PurchaseReceiptLineHistoryDto> ReceiptHistory { get; set; } = Array.Empty<PurchaseReceiptLineHistoryDto>();
}

public sealed class PurchaseOrderLineDto
{
  public int Id { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public string? VendorCode { get; set; }
  public string? BaseUnitName { get; set; }
  public decimal PurchaseQuantity { get; set; } = 1m;
  public string? PurchaseUnitName { get; set; }
  public decimal PurchaseIncrement { get; set; } = MaterialPurchaseIncrement.WholePresentation;
  public decimal? BaseUnitPrice { get; set; }
  public decimal OrderedQuantity { get; set; }
  public decimal ReceivedQuantity { get; set; }
  public decimal RemainingQuantity { get; set; }
  public IReadOnlyList<PurchaseOrderAllocationDto> Allocations { get; set; } = Array.Empty<PurchaseOrderAllocationDto>();
}

public sealed class PurchaseOrderAllocationDto
{
  public int Id { get; set; }
  public int PurchaseOrderLineId { get; set; }
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public string? LocationCode { get; set; }
  public decimal PlannedQuantity { get; set; }
  public decimal ReceivedQuantity { get; set; }
  public decimal RemainingQuantity { get; set; }
}

public sealed class PurchaseReceiptLineHistoryDto
{
  public int ReceiptId { get; set; }
  public string ReceiptCode { get; set; } = string.Empty;
  public DateTime ReceiptDate { get; set; }
  public int PurchaseOrderLineId { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public string? BaseUnitName { get; set; }
  public decimal PurchaseQuantity { get; set; } = 1m;
  public string? PurchaseUnitName { get; set; }
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public decimal? SubtotalAmount { get; set; }
  public decimal? IvaAmount { get; set; }
  public decimal? TotalAmount { get; set; }
  public bool IncludesIva { get; set; }
  public decimal? UnitCost { get; set; }
  public string? CreatedBy { get; set; }
  public string? Notes { get; set; }
}

public sealed class PurchaseOrderUpsertRequest
{
  public int? Id { get; set; }

  [Range(1, int.MaxValue, ErrorMessage = "Selecciona un proveedor válido.")]
  public int BusinessPartnerId { get; set; }

  [Required]
  public DateTime OrderDate { get; set; } = DateTime.Today;

  public DateTime? ExpectedDate { get; set; }

  [StringLength(1000)]
  public string? Notes { get; set; }

  public List<PurchaseOrderLineUpsertRequest> Lines { get; set; } = [];

  /// <summary>
  /// Registra al proveedor de la orden como proveedor alternativo de los materiales que compró
  /// sin surtir de costumbre. Es lo que convierte una compra de emergencia en catálogo: la
  /// siguiente vez el material ya aparece marcado como suyo.
  /// </summary>
  public bool LinkMaterialsToVendor { get; set; } = true;
}

public sealed class AutoPurchaseOrderCreateRequest
{
  [Range(1, int.MaxValue, ErrorMessage = "Selecciona un proveedor válido.")]
  public int BusinessPartnerId { get; set; }

  [Required]
  public DateTime OrderDate { get; set; } = DateTime.Today;

  public List<int> RoomIds { get; set; } = [];
}

public sealed class PurchaseOrderLineUpsertRequest
{
  public int? Id { get; set; }

  [Range(1, int.MaxValue, ErrorMessage = "Selecciona un material válido.")]
  public int MaterialId { get; set; }

  [Range(typeof(decimal), "0", "999999999", ErrorMessage = "El precio por unidad base no puede ser negativo.")]
  public decimal? BaseUnitPrice { get; set; }

  [Range(typeof(decimal), "0.0001", "999999999", ErrorMessage = "La cantidad por compra debe ser mayor a 0.")]
  public decimal PurchaseQuantitySnapshot { get; set; } = 1m;

  [StringLength(50)]
  public string? PurchaseUnitNameSnapshot { get; set; }

  /// <summary>
  /// Escalón de compra congelado al capturar la orden. En <c>null</c> se toma el del material, que es
  /// lo que debe pasar cuando quien arma la orden no lo conoce. Ver <see cref="MaterialPurchaseIncrement"/>.
  /// </summary>
  [Range(typeof(decimal), "0", "999999999", ErrorMessage = "El escalón de compra no puede ser negativo.")]
  public decimal? PurchaseIncrementSnapshot { get; set; }

  public List<PurchaseOrderAllocationUpsertRequest> Allocations { get; set; } = [];
}

public sealed class PurchaseOrderAllocationUpsertRequest
{
  public int? Id { get; set; }

  [Range(1, int.MaxValue, ErrorMessage = "Selecciona una ubicación válida.")]
  public int LocationId { get; set; }

  [Range(typeof(decimal), "0.0001", "999999999", ErrorMessage = "La cantidad planeada debe ser mayor a 0.")]
  public decimal PlannedQuantity { get; set; }
}

public sealed class PurchaseReceiptCreateRequest
{
  [Range(1, int.MaxValue, ErrorMessage = "Selecciona una orden de compra válida.")]
  public int PurchaseOrderId { get; set; }

  [Required]
  public DateTime ReceiptDate { get; set; } = DateTime.Today;

  [StringLength(1000)]
  public string? Notes { get; set; }

  public List<PurchaseReceiptLineCreateRequest> Lines { get; set; } = [];
}

public sealed class PurchaseReceiptLineCreateRequest
{
  [Range(1, int.MaxValue, ErrorMessage = "Selecciona una asignación válida.")]
  public int PurchaseOrderLineAllocationId { get; set; }

  [Range(typeof(decimal), "0.0001", "999999999", ErrorMessage = "La cantidad recibida debe ser mayor a 0.")]
  public decimal Quantity { get; set; }

  [Range(typeof(decimal), "0.01", "999999999", ErrorMessage = "Captura el total del artículo tal como aparece en el ticket.")]
  public decimal TotalAmount { get; set; }

  public bool IncludesIva { get; set; }
}

public sealed class PurchaseOrderCatalogDto
{
  public IReadOnlyList<LookupOptionDto> Vendors { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<LookupOptionDto> Locations { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<LookupOptionDto> Rooms { get; set; } = Array.Empty<LookupOptionDto>();
  public IReadOnlyList<string> Statuses { get; set; } = Array.Empty<string>();
}
