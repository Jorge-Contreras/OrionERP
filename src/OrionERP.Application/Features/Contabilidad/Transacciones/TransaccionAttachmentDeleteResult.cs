namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed record class TransaccionAttachmentDeleteResult(
    bool Success,
    string Message,
    bool Blocked,
    bool MovedToPlaceholder)
{
  public static TransaccionAttachmentDeleteResult Deleted()
    => new(true, "Archivo adjunto eliminado.", false, false);

  public static TransaccionAttachmentDeleteResult Moved(int placeholderTransaccionId, int comprobanteId)
    => new(
        true,
        $"El XML está vinculado al CFDI {comprobanteId} y se movió a la póliza placeholder {placeholderTransaccionId}.",
        false,
        true);

  public static TransaccionAttachmentDeleteResult Blocked(string message)
    => new(false, message, true, false);

  public static TransaccionAttachmentDeleteResult Fail(string message)
    => new(false, message, false, false);
}
