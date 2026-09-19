using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Restaurante;

public interface IRestaurantDeliveryService
{
  Task<IReadOnlyList<RestaurantDeliveryQueueItemDto>> GetQueueAsync(
    string rfc, int siteId, string userName, bool canSupervise, CancellationToken ct = default);
  Task<RestaurantCommandResult> ClaimAndDispatchAsync(
    string rfc, Guid orderId, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> ReleaseOrReassignAsync(
    RestaurantDeliveryAssignmentRequest request, string supervisorUserName, CancellationToken ct = default);
  Task<RestaurantCommandResult> ConfirmAddressAsync(
    string rfc, Guid orderId, string userName, CancellationToken ct = default);
  Task<RestaurantCommandResult> CompleteAsync(
    RestaurantDeliveryCompleteRequest request, string userName, bool canOverrideProof, CancellationToken ct = default);
  Task<RestaurantDeliveryEvidencePayload?> GetEvidenceAsync(
    string rfc, long evidenceId, string userName, bool canSupervise, CancellationToken ct = default);
}

public sealed class RestaurantDeliveryQueueItemDto
{
  public Guid OrderId { get; set; }
  public int Folio { get; set; }
  public int SiteId { get; set; }
  public string Status { get; set; } = string.Empty;
  public string PaymentStatus { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
  public DateTime? ReadyAt { get; set; }
  public string CustomerName { get; set; } = string.Empty;
  public string CustomerPhone { get; set; } = string.Empty;
  public string AddressLine { get; set; } = string.Empty;
  public string? AddressComplement { get; set; }
  public string? AddressReferences { get; set; }
  public decimal? Latitude { get; set; }
  public decimal? Longitude { get; set; }
  public string AddressVerificationStatus { get; set; } = string.Empty;
  public string DropoffPreference { get; set; } = string.Empty;
  public string? AssignedCourierUserName { get; set; }
  public DateTime? AssignedAt { get; set; }
  public DateTime? AddressConfirmedAt { get; set; }
  public long? FacadeEvidenceId { get; set; }
  public decimal Total { get; set; }
  public IReadOnlyList<RestaurantDeliveryLineDto> Lines { get; set; } = Array.Empty<RestaurantDeliveryLineDto>();
}

public sealed class RestaurantDeliveryLineDto
{
  public string ProductName { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public string? Notes { get; set; }
}

public sealed class RestaurantDeliveryAssignmentRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid OrderId { get; set; }
  [StringLength(256)] public string? CourierUserName { get; set; }
}

public sealed class RestaurantDeliveryCompleteRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public Guid OrderId { get; set; }
  public byte[]? ProofPhoto { get; set; }
  public bool OverrideMissingProof { get; set; }
  [StringLength(500)] public string? OverrideReason { get; set; }
}

public sealed class RestaurantDeliveryEvidencePayload
{
  public byte[] Bytes { get; set; } = Array.Empty<byte>();
  public string ContentType { get; set; } = "image/jpeg";
  public string ContentHash { get; set; } = string.Empty;
}
