using System.Globalization;
using System.Net;
using OrionERP.Application.Features.Bonhomia.PublicBooking;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Bonhomia.Web.Features.Bonhomia.Checkout;

public sealed class BonhomiaReservationConfirmationEmailSender : IBonhomiaReservationConfirmationEmailSender
{
  private const string ReceptionEmail = "recepcion@bonhomiasuites.com";
  private static readonly CultureInfo MexicanCulture = CultureInfo.GetCultureInfo("es-MX");

  private readonly IMicrosoftGraphMailClient<BonhomiaGraphMailOptions> _mailClient;

  public BonhomiaReservationConfirmationEmailSender(
    IMicrosoftGraphMailClient<BonhomiaGraphMailOptions> mailClient)
  {
    _mailClient = mailClient;
  }

  public Task SendConfirmationAsync(
    BonhomiaReservationConfirmationEmail confirmation,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(confirmation);
    ArgumentNullException.ThrowIfNull(confirmation.Quote);
    ArgumentNullException.ThrowIfNull(confirmation.Customer);
    ArgumentNullException.ThrowIfNull(confirmation.Payment);

    var subject = $"Confirmacion de reservacion Bonhomia #{confirmation.ReservationId:D6}";
    var html = BuildHtml(confirmation);
    return _mailClient.SendEmailAsync(
      new MicrosoftGraphMailMessage
      {
        ToRecipients = [confirmation.Customer.Email],
        BccRecipients = [ReceptionEmail],
        Subject = subject,
        Message = html
      },
      ct);
  }

  private static string BuildHtml(BonhomiaReservationConfirmationEmail confirmation)
  {
    var quote = confirmation.Quote;
    var payment = confirmation.Payment;
    var guestName = FirstPresent(confirmation.ClientName, confirmation.Customer.FullName, "Huesped");
    var paymentOrderId = FirstPresent(payment.OrderId, "No disponible");
    var paymentCaptureId = FirstPresent(payment.CaptureId, "No disponible");
    var confirmedAt = confirmation.ConfirmedAtUtc == default
      ? "No disponible"
      : confirmation.ConfirmedAtUtc.ToLocalTime().ToString("dddd d 'de' MMMM 'de' yyyy, HH:mm", MexicanCulture);

    return $"""
<!doctype html>
<html lang="es">
  <body style="font-family: Arial, sans-serif; color: #1f2933; line-height: 1.5; margin: 0; padding: 24px; background: #f6f7f8;">
    <main style="max-width: 680px; margin: 0 auto; background: #ffffff; border: 1px solid #d9dee3; padding: 24px;">
      <h1 style="font-size: 22px; margin: 0 0 16px;">Reservacion confirmada</h1>
      <p>Hola {Html(guestName)},</p>
      <p>Gracias por reservar en Bonhomia Suites. Tu pago fue confirmado y tu reservacion ya quedo registrada.</p>

      <table style="width: 100%; border-collapse: collapse; margin: 20px 0;">
        <tbody>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Reservacion</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">#{confirmation.ReservationId:D6}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Suite</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{Html(quote.RoomName)}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Check-in</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{FormatDate(quote.CheckIn)}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Check-out</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{FormatDate(quote.CheckOut)}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Noches</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{quote.Nights}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Huespedes</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{quote.Guests}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Total</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{FormatMoney(confirmation.Total, quote.Currency)}</td></tr>
          <tr><th align="left" style="padding: 8px; border-bottom: 1px solid #e5e7eb;">Confirmado</th><td style="padding: 8px; border-bottom: 1px solid #e5e7eb;">{Html(confirmedAt)}</td></tr>
        </tbody>
      </table>

      <h2 style="font-size: 18px; margin: 24px 0 12px;">Detalle</h2>
      <table style="width: 100%; border-collapse: collapse; margin: 0 0 20px;">
        <thead>
          <tr>
            <th align="left" style="padding: 8px; border-bottom: 1px solid #d9dee3;">Concepto</th>
            <th align="right" style="padding: 8px; border-bottom: 1px solid #d9dee3;">Cantidad</th>
            <th align="right" style="padding: 8px; border-bottom: 1px solid #d9dee3;">Total</th>
          </tr>
        </thead>
        <tbody>
          {BuildLineRows(quote)}
        </tbody>
      </table>

      <h2 style="font-size: 18px; margin: 24px 0 12px;">Pago PayPal</h2>
      <p style="margin: 0 0 4px;"><strong>Orden:</strong> {Html(paymentOrderId)}</p>
      <p style="margin: 0 0 16px;"><strong>Captura:</strong> {Html(paymentCaptureId)}</p>

      <p>Puedes descargar tu recibo de reservacion aqui:</p>
      <p><a href="{Html(confirmation.PdfUrl)}">{Html(confirmation.PdfUrl)}</a></p>

      <p style="margin-top: 24px;">Si necesitas ayuda, responde a este correo y con gusto te apoyamos.</p>
      <p style="margin-bottom: 0;">Bonhomia Suites</p>
    </main>
  </body>
</html>
""";
  }

  private static string BuildLineRows(BonhomiaQuoteDto quote)
  {
    if (quote.Lines.Count == 0)
    {
      return $"""
<tr>
  <td style="padding: 8px; border-bottom: 1px solid #edf0f2;">Hospedaje</td>
  <td align="right" style="padding: 8px; border-bottom: 1px solid #edf0f2;">{quote.Nights}</td>
  <td align="right" style="padding: 8px; border-bottom: 1px solid #edf0f2;">{FormatMoney(quote.Total, quote.Currency)}</td>
</tr>
""";
    }

    return string.Join(
      Environment.NewLine,
      quote.Lines.Select(line =>
        $"""
<tr>
  <td style="padding: 8px; border-bottom: 1px solid #edf0f2;">{Html(line.Description)}</td>
  <td align="right" style="padding: 8px; border-bottom: 1px solid #edf0f2;">{line.Quantity}</td>
  <td align="right" style="padding: 8px; border-bottom: 1px solid #edf0f2;">{FormatMoney(line.Total, quote.Currency)}</td>
</tr>
"""));
  }

  private static string FirstPresent(params string?[] values)
    => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

  private static string FormatDate(DateOnly date)
    => date == default
      ? "Sin fecha"
      : date.ToString("dddd d 'de' MMMM 'de' yyyy", MexicanCulture);

  private static string FormatMoney(decimal value, string? currency)
  {
    var formattedValue = value.ToString("C", MexicanCulture);
    return string.IsNullOrWhiteSpace(currency)
      ? formattedValue
      : $"{formattedValue} {currency.Trim().ToUpperInvariant()}";
  }

  private static string Html(string? value)
    => WebUtility.HtmlEncode(value ?? string.Empty);
}
