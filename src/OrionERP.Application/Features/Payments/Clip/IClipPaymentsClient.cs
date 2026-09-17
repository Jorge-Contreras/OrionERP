namespace OrionERP.Application.Features.Payments.Clip;

public interface IClipPaymentsClient
{
  /// <summary>
  /// Cobra el token de tarjeta. <b>Esta llamada nunca se reintenta:</b> Clip no
  /// acepta llave de idempotencia en <c>POST /payments</c>, así que un segundo
  /// intento cobra dos veces. Si falla sin respuesta utilizable lanza
  /// <see cref="ClipClientException"/> con <c>IsOutcomeUnknown</c> y el cargo se
  /// resuelve consultando.
  /// </summary>
  Task<ClipPaymentResult> CreatePaymentAsync(
    ClipPaymentRequest request,
    CancellationToken ct = default);

  /// <summary>Fuente de verdad del estado de un pago. Seguro de repetir.</summary>
  Task<ClipPaymentResult> GetPaymentAsync(
    string paymentId,
    CancellationToken ct = default);

  /// <summary>
  /// Pagos de un rango de fechas. Clip no permite filtrar por
  /// <c>external_reference</c>, así que encontrar un cargo huérfano obliga a
  /// barrer la ventana y empatar en memoria.
  /// </summary>
  Task<IReadOnlyList<ClipPaymentResult>> ListPaymentsAsync(
    DateTimeOffset fromUtc,
    DateTimeOffset toUtc,
    CancellationToken ct = default);

  /// <summary>
  /// Reembolso total o parcial. La llave de idempotencia de Clip sólo vive un
  /// minuto, así que antes de reintentar hay que consultar el pago y revisar
  /// <c>AmountRefunded</c>.
  /// </summary>
  Task<ClipRefundResult> RefundPaymentAsync(
    ClipRefundRequest request,
    string idempotencyKey,
    CancellationToken ct = default);

  Task<ClipRefundResult> GetRefundAsync(
    string refundId,
    CancellationToken ct = default);
}
