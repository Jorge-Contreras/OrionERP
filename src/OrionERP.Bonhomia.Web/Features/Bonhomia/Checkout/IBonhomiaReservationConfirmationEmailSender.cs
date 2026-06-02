using OrionERP.Application.Features.Bonhomia.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public interface IBonhomiaReservationConfirmationEmailSender
{
  Task SendConfirmationAsync(
    BonhomiaReservationConfirmationEmail confirmation,
    CancellationToken ct = default);
}

public sealed class BonhomiaReservationConfirmationEmail
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public BonhomiaQuoteDto Quote { get; set; } = new();
  public BonhomiaCustomerInfo Customer { get; set; } = new();
  public BonhomiaPayPalCaptureResult Payment { get; set; } = new();
  public decimal Total { get; set; }
  public DateTimeOffset ConfirmedAtUtc { get; set; }
  public string PdfUrl { get; set; } = string.Empty;
}
