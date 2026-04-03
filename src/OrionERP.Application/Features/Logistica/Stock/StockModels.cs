using System.ComponentModel.DataAnnotations;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Application.Features.Logistica.Stock;

public sealed class StockFilter
{
  public string? SearchText { get; set; }
  public int? RoomId { get; set; }
  public int? LocationId { get; set; }
  public bool LowStockOnly { get; set; }
  public bool CountDueOnly { get; set; }
  public bool IncludeZeroBalances { get; set; } = true;
  public int Skip { get; set; }
  public int Take { get; set; }
}

public sealed class StockListItemDto
{
  public int StockBalanceId { get; set; }
  public int LocationId { get; set; }
  public string LocationCode { get; set; } = string.Empty;
  public string LocationName { get; set; } = string.Empty;
  public string LocationType { get; set; } = string.Empty;
  public string? RoomName { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialDescription { get; set; } = string.Empty;
  public string MaterialClass { get; set; } = string.Empty;
  public string? Barcode { get; set; }
  public string? BaseUnitName { get; set; }
  public string? VendorName { get; set; }
  public decimal Quantity { get; set; }
  public decimal? MinQuantity { get; set; }
  public decimal? MaxQuantity { get; set; }
  public bool IsLowStock { get; set; }
  public bool IsCountDue { get; set; }
  public DateTime? LastCountedAt { get; set; }
  public int? CountFrequencyDays { get; set; }
  public int AttachmentCount { get; set; }
}

public sealed class StockThresholdUpdateRequest : IValidatableObject
{
  [Range(1, int.MaxValue, ErrorMessage = "Selecciona un registro de inventario válido.")]
  public int StockBalanceId { get; set; }

  [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "El mínimo no puede ser negativo.")]
  public decimal? MinQuantity { get; set; }

  [Range(typeof(decimal), "0", "79228162514264337593543950335", ErrorMessage = "El máximo no puede ser negativo.")]
  public decimal? MaxQuantity { get; set; }

  public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
  {
    if (MinQuantity.HasValue && MaxQuantity.HasValue && MinQuantity.Value > MaxQuantity.Value)
    {
      yield return new ValidationResult(
        "El mínimo no puede ser mayor que el máximo.",
        [nameof(MinQuantity), nameof(MaxQuantity)]);
    }
  }
}

public sealed class StockTransactionDto
{
  public int Id { get; set; }
  public DateTime OccurredAt { get; set; }
  public string TransactionType { get; set; } = string.Empty;
  public decimal QuantityDelta { get; set; }
  public decimal QuantityAfter { get; set; }
  public string? ReferenceType { get; set; }
  public int? ReferenceId { get; set; }
  public string? Notes { get; set; }
  public string? PerformedBy { get; set; }
}

public sealed class LocationMaterialAttachmentDto
{
  public int Id { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string FileExtension { get; set; } = string.Empty;
  public string? Description { get; set; }
  public long Length { get; set; }
  public DateTime CreatedAt { get; set; }
  public string? CreatedBy { get; set; }
}

public sealed class LocationMaterialAttachmentCreateRequest
{
  [Required]
  public int LocationId { get; set; }

  [Required]
  public int MaterialId { get; set; }

  [Required]
  [StringLength(200)]
  public string FileName { get; set; } = string.Empty;

  [Required]
  [StringLength(50)]
  public string FileExtension { get; set; } = string.Empty;

  [StringLength(500)]
  public string? Description { get; set; }

  [Required]
  public byte[] Bytes { get; set; } = Array.Empty<byte>();

  [StringLength(100)]
  public string? ContentType { get; set; }
}
