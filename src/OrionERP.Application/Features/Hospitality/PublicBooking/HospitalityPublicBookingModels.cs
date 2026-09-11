using System;
using System.Collections.Generic;
using OrionERP.Application.Features.Platform;
using OrionERP.Application.Features.Reservaciones.Experiencias;

namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public sealed class HospitalityAvailabilityDto
{
  public DateOnly StartDate { get; set; }
  public DateOnly EndDateExclusive { get; set; }
  public IReadOnlyList<HospitalityRoomAvailabilityDto> Rooms { get; set; } = Array.Empty<HospitalityRoomAvailabilityDto>();
  public IReadOnlyList<HospitalityExtraOptionDto> Extras { get; set; } = Array.Empty<HospitalityExtraOptionDto>();
  public IReadOnlyList<ExperienceCatalogItemDto> Experiences { get; set; } = Array.Empty<ExperienceCatalogItemDto>();
}

public sealed class HospitalityRoomAvailabilityDto
{
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string Tag { get; set; } = string.Empty;
  public string Ideal { get; set; } = string.Empty;
  public string Image { get; set; } = string.Empty;
  public int Capacity { get; set; }
  public int Bedrooms { get; set; }
  public decimal Bathrooms { get; set; }
  public decimal BasePrice { get; set; }
  public IReadOnlyList<HospitalityDayAvailabilityDto> Days { get; set; } = Array.Empty<HospitalityDayAvailabilityDto>();
}

public sealed class HospitalityDayAvailabilityDto
{
  public DateOnly Date { get; set; }
  public bool IsAvailable { get; set; }
  public string StateCode { get; set; } = string.Empty;
  public decimal Price { get; set; }
}

public sealed class HospitalityExtraOptionDto
{
  public string Code { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;
  public string CatalogName { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public decimal UnitPrice { get; set; }
  public int MaxQuantity { get; set; } = 1;
}

public sealed class HospitalityQuoteRequest
{
  public string RoomName { get; set; } = string.Empty;
  public DateOnly CheckIn { get; set; }
  public DateOnly CheckOut { get; set; }
  public int Guests { get; set; }
  public IReadOnlyList<HospitalitySelectedExtraRequest> Extras { get; set; } = Array.Empty<HospitalitySelectedExtraRequest>();
  public IReadOnlyList<HospitalitySelectedExperienceRequest> Experiences { get; set; } = Array.Empty<HospitalitySelectedExperienceRequest>();
}

public sealed class HospitalitySelectedExtraRequest
{
  public string Code { get; set; } = string.Empty;
  public int Quantity { get; set; }
}

public sealed class HospitalitySelectedExperienceRequest
{
  public string Code { get; set; } = string.Empty;
  public string PackageCode { get; set; } = string.Empty;
  public DateOnly ExperienceDate { get; set; }
  public int AdultParticipants { get; set; }
  public int ChildParticipants { get; set; }
  public IReadOnlyList<HospitalitySelectedExperienceAddOnRequest> AddOns { get; set; } = Array.Empty<HospitalitySelectedExperienceAddOnRequest>();
}

public sealed class HospitalitySelectedExperienceAddOnRequest
{
  public string Code { get; set; } = string.Empty;
  public int Quantity { get; set; }
}

public sealed class HospitalityQuoteDto
{
  public Guid QuoteId { get; set; } = Guid.NewGuid();
  /// <summary>
  /// Trusted website identity stamped by the server before the quote is
  /// protected. It is part of the fingerprint and is never used to select a
  /// database scope.
  /// </summary>
  public string PublicSiteKey { get; set; } = string.Empty;
  public HospitalityQuoteRequest Request { get; set; } = new();
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
  public IReadOnlyList<HospitalityQuoteLineDto> Lines { get; set; } = Array.Empty<HospitalityQuoteLineDto>();
  public IReadOnlyList<int> RoomCalendarIds { get; set; } = Array.Empty<int>();
}

public sealed class HospitalityQuoteLineDto
{
  public string Type { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public int Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal Total { get; set; }
}

public sealed class HospitalityCustomerInfo
{
  public string FullName { get; set; } = string.Empty;
  public string Email { get; set; } = string.Empty;
  public string Phone { get; set; } = string.Empty;
}

public sealed record HospitalityLegalAcceptance(
  string PrivacyVersion,
  string TermsVersion,
  DateTimeOffset AcceptedAtUtc);

public static class HospitalityLegalConsentPolicy
{
  public static HospitalityLegalAcceptance EnsureAccepted(
    bool accepted,
    string? privacyVersion,
    string? termsVersion,
    PublicWebsitePresentationDefinition presentation,
    DateTimeOffset acceptedAtUtc)
  {
    ArgumentNullException.ThrowIfNull(presentation);

    if (!accepted
        || string.IsNullOrWhiteSpace(privacyVersion)
        || string.IsNullOrWhiteSpace(termsVersion))
    {
      throw new HospitalityPublicBookingException(
        "legal_consent_required",
        "Debes aceptar el aviso de privacidad y los terminos vigentes antes de continuar.");
    }

    if (!string.Equals(privacyVersion.Trim(), presentation.PrivacyVersion, StringComparison.Ordinal)
        || !string.Equals(termsVersion.Trim(), presentation.TermsVersion, StringComparison.Ordinal))
    {
      throw new HospitalityPublicBookingException(
        "legal_documents_changed",
        "El aviso de privacidad o los terminos cambiaron. Revisa y acepta las versiones vigentes.");
    }

    return new HospitalityLegalAcceptance(
      presentation.PrivacyVersion,
      presentation.TermsVersion,
      acceptedAtUtc.ToUniversalTime());
  }
}

public sealed class HospitalityPayPalOrderResult
{
  public string OrderId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
}

public sealed class HospitalityPayPalCaptureResult
{
  public string OrderId { get; set; } = string.Empty;
  public string CustomId { get; set; } = string.Empty;
  public string ReferenceId { get; set; } = string.Empty;
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

public sealed class HospitalityPaidReservationResult
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public decimal Total { get; set; }
  public bool CreatedNewReservation { get; set; }
}

public sealed class HospitalityPublicBookingException : Exception
{
  public HospitalityPublicBookingException(string errorCode, string message)
    : base(message)
  {
    ErrorCode = errorCode;
  }

  public string ErrorCode { get; }
}
