using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public interface IBonhomiaPublicBookingService
{
  Task<BonhomiaAvailabilityDto> GetAvailabilityAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default);

  Task<BonhomiaQuoteDto> CreateQuoteAsync(
    BonhomiaQuoteRequest request,
    CancellationToken ct = default);

  Task ValidateQuoteAvailabilityAsync(
    BonhomiaQuoteDto quote,
    CancellationToken ct = default);

  Task<BonhomiaPaidReservationResult> CreatePaidReservationAsync(
    BonhomiaQuoteDto quote,
    BonhomiaCustomerInfo customer,
    BonhomiaPayPalCaptureResult payment,
    CancellationToken ct = default);
}
