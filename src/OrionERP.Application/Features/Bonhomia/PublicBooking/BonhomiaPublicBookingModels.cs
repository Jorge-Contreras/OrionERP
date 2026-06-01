using System;
using System.Collections.Generic;
using OrionERP.Application.Features.Reservaciones.Experiencias;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public sealed class BonhomiaAvailabilityDto
{
  public DateOnly StartDate { get; set; }
  public DateOnly EndDateExclusive { get; set; }
  public IReadOnlyList<BonhomiaRoomAvailabilityDto> Rooms { get; set; } = Array.Empty<BonhomiaRoomAvailabilityDto>();
  public IReadOnlyList<BonhomiaExtraOptionDto> Extras { get; set; } = Array.Empty<BonhomiaExtraOptionDto>();
  public IReadOnlyList<ExperienceCatalogItemDto> Experiences { get; set; } = Array.Empty<ExperienceCatalogItemDto>();
}

public sealed class BonhomiaRoomAvailabilityDto
{
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string Tag { get; set; } = string.Empty;
  public string Ideal { get; set; } = string.Empty;
  public string Image { get; set; } = string.Empty;
  public int Capacity { get; set; }
  public int Bedrooms { get; set; }
  public decimal BasePrice { get; set; }
  public IReadOnlyList<BonhomiaDayAvailabilityDto> Days { get; set; } = Array.Empty<BonhomiaDayAvailabilityDto>();
}

public sealed class BonhomiaDayAvailabilityDto
{
  public DateOnly Date { get; set; }
  public bool IsAvailable { get; set; }
  public string StateCode { get; set; } = string.Empty;
  public decimal Price { get; set; }
}

public sealed class BonhomiaExtraOptionDto
{
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;
  public string CatalogName { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public decimal UnitPrice { get; set; }
  public int MaxQuantity { get; set; } = 1;
}

public sealed class BonhomiaQuoteRequest
{
  public string RoomName { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public int Guests { get; set; }
  public IReadOnlyList<BonhomiaSelectedExtraRequest> Extras { get; set; } = Array.Empty<BonhomiaSelectedExtraRequest>();
  public IReadOnlyList<BonhomiaSelectedExperienceRequest> Experiences { get; set; } = Array.Empty<BonhomiaSelectedExperienceRequest>();
}

public sealed class BonhomiaSelectedExtraRequest
{
  public string Code { get; set; } = string.Empty;
  public int Quantity { get; set; }
}

public sealed class BonhomiaSelectedExperienceRequest
{
  public string Code { get; set; } = string.Empty;
  public string PackageCode { get; set; } = string.Empty;
  public DateOnly ExperienceDate { get; set; }
  public int AdultParticipants { get; set; }
  public int ChildParticipants { get; set; }
  public IReadOnlyList<BonhomiaSelectedExperienceAddOnRequest> AddOns { get; set; } = Array.Empty<BonhomiaSelectedExperienceAddOnRequest>();
}

public sealed class BonhomiaSelectedExperienceAddOnRequest
{
  public string Code { get; set; } = string.Empty;
  public int Quantity { get; set; }
}

public sealed class BonhomiaQuoteDto
{
  public Guid QuoteId { get; set; } = Guid.NewGuid();
  public BonhomiaQuoteRequest Request { get; set; } = new();
  public string RoomName { get; set; } = string.Empty;
  public string RoomImage { get; set; } = string.Empty;
  public int Nights { get; set; }
  public int Guests { get; set; }
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public decimal SuiteSubtotal { get; set; }
  public decimal ExtrasSubtotal { get; set; }
  public decimal ExperiencesSubtotal { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Tax { get; set; }
  public decimal Ish { get; set; }
  public decimal Total { get; set; }
  public string Currency { get; set; } = "MXN";
  public DateTimeOffset ExpiresAtUtc { get; set; }
  public string Fingerprint { get; set; } = string.Empty;
  public IReadOnlyList<BonhomiaQuoteLineDto> Lines { get; set; } = Array.Empty<BonhomiaQuoteLineDto>();
  public IReadOnlyList<int> RoomCalendarIds { get; set; } = Array.Empty<int>();
}

public sealed class BonhomiaQuoteLineDto
{
  public string Type { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public int Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal Total { get; set; }
}

public sealed class BonhomiaCustomerInfo
{
  public string FullName { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string Phone { get; set; } = string.Empty;
}

public sealed class BonhomiaPayPalOrderResult
{
  public string OrderId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
}

public sealed class BonhomiaPayPalCaptureResult
{
  public string OrderId { get; set; } = string.Empty;
  public string OrderStatus { get; set; } = string.Empty;
  public string CaptureId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string StatusReason { get; set; } = string.Empty;
  public string PayerName { get; set; } = string.Empty;
  public string PayerEmail { get; set; } = string.Empty;
  public string PayerPhone { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public string Currency { get; set; } = "MXN";
  public bool IsCompleted => string.Equals(Status, "COMPLETED", StringComparison.OrdinalIgnoreCase);
}

public sealed class BonhomiaPaidReservationResult
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public decimal Total { get; set; }
}

public sealed class BonhomiaPublicBookingException : Exception
{
  public BonhomiaPublicBookingException(string errorCode, string message)
    : base(message)
  {
    ErrorCode = errorCode;
  }

  public string ErrorCode { get; }
}
