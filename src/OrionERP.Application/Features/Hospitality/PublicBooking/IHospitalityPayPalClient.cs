using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Hospitality.PublicBooking;

public interface IHospitalityPayPalClient
{
  Task<HospitalityPayPalOrderResult> CreateOrderAsync(
    HospitalityQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<HospitalityPayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    HospitalityQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default);
}
