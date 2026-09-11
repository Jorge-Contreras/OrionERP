using OrionERP.Application.Features.Hospitality.PublicBooking;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public interface IHospitalityReservationConfirmationEmailSender
{
  Task SendConfirmationAsync(
    HospitalityReservationConfirmationEmail confirmation,
    CancellationToken ct = default);
}

public sealed class HospitalityReservationConfirmationEmail
{
  public int ReservationId { get; set; }
  public int TransaccionId { get; set; }
  public string ClientName { get; set; } = string.Empty;
  public HospitalityQuoteDto Quote { get; set; } = new();
  public HospitalityCustomerInfo Customer { get; set; } = new();
  public HospitalityPayPalCaptureResult Payment { get; set; } = new();
  public decimal Total { get; set; }
  public DateTimeOffset ConfirmedAtUtc { get; set; }
  public string PdfUrl { get; set; } = string.Empty;
}
