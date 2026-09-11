using System.Threading;
using System.Threading.Tasks;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public interface IHospitalityPublicBookingService
{
  Task<HospitalityAvailabilityDto> GetAvailabilityAsync(
    DateOnly startDate,
    DateOnly endDateExclusive,
    CancellationToken ct = default);

  Task<HospitalityQuoteDto> CreateQuoteAsync(
    HospitalityQuoteRequest request,
    CancellationToken ct = default);

  Task ValidateQuoteAvailabilityAsync(
    HospitalityQuoteDto quote,
    CancellationToken ct = default);

  Task<HospitalityPaidReservationResult> CreatePaidReservationAsync(
    HospitalityQuoteDto quote,
    HospitalityCustomerInfo customer,
    HospitalityPayPalCaptureResult payment,
    HospitalityLegalAcceptance legalAcceptance,
    CancellationToken ct = default);

  Task<ReservacionDetailDto?> GetReservationDetailAsync(
    int reservationId,
    CancellationToken ct = default);
}
