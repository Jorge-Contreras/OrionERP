using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Logistica.Stock;

public sealed class InventoryMovementWorkspaceDto
{
  public IReadOnlyList<InventoryLocationOptionDto> Locations { get; set; } = [];
  public IReadOnlyList<InventoryBalanceOptionDto> Balances { get; set; } = [];
  public IReadOnlyList<InventoryLotOptionDto> Lots { get; set; } = [];
}

public sealed class InventoryLocationOptionDto
{
  public int Id { get; set; }
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
}

public sealed class InventoryBalanceOptionDto
{
  public int MaterialId { get; set; }
  public int LocationId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialName { get; set; } = string.Empty;
  public string UnitCode { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public decimal ReservedQuantity { get; set; }
  public decimal AverageUnitCost { get; set; }
  public bool TrackLots { get; set; }
  public decimal AvailableQuantity => Quantity - ReservedQuantity;
}

public sealed class InventoryLotOptionDto
{
  public long Id { get; set; }
  public int MaterialId { get; set; }
  public int LocationId { get; set; }
  public string LotCode { get; set; } = string.Empty;
  public DateTime? ExpirationDate { get; set; }
  public decimal Quantity { get; set; }
  public decimal ReservedQuantity { get; set; }
  public decimal AvailableQuantity => Quantity - ReservedQuantity;
}

public sealed class InventoryTransferCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string TransferCode { get; set; } = string.Empty;
  public int FromLocationId { get; set; }
  public int ToLocationId { get; set; }
  [Required, StringLength(500)] public string Reason { get; set; } = string.Empty;
  [MinLength(1)] public List<InventoryTransferLineRequest> Lines { get; set; } = [];
}

public sealed class InventoryTransferLineRequest
{
  public int MaterialId { get; set; }
  public long? MaterialLotId { get; set; }
  [Range(typeof(decimal), "0.0001", "999999999")] public decimal Quantity { get; set; }
}

public sealed class InventoryAdjustmentCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  [Required, StringLength(30)] public string AdjustmentCode { get; set; } = string.Empty;
  [Required] public string AdjustmentType { get; set; } = "Adjustment";
  [Required, StringLength(30)] public string ReasonCode { get; set; } = string.Empty;
  [Required, StringLength(1000)] public string Reason { get; set; } = string.Empty;
  [Required, StringLength(256)] public string AuthorizedBy { get; set; } = string.Empty;
  [Required, StringLength(200)] public string EvidenceFileName { get; set; } = string.Empty;
  [Required] public byte[] Evidence { get; set; } = [];
  [MinLength(1)] public List<InventoryAdjustmentLineRequest> Lines { get; set; } = [];
}

public sealed class InventoryAdjustmentLineRequest
{
  public int MaterialId { get; set; }
  public int LocationId { get; set; }
  public long? MaterialLotId { get; set; }
  public decimal QuantityDelta { get; set; }
}
