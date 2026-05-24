using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Application.Features.Bonhomia.PublicBooking;

public interface IBonhomiaPayPalClient
{
  Task<BonhomiaPayPalOrderResult> CreateOrderAsync(
    BonhomiaQuoteDto quote,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<BonhomiaPayPalCaptureResult> CaptureOrderAsync(
    string orderId,
    string idempotencyKey,
    CancellationToken ct = default);
}
