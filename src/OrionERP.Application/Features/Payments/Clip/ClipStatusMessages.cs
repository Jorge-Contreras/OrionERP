namespace OrionERP.Application.Features.Payments.Clip;

/// <summary>
/// Traduce el <c>status_detail.code</c> de Clip a un mensaje que el cliente
/// pueda accionar. Un "pago rechazado" genérico hace que la gente reintente con
/// la misma tarjeta; decir que faltan fondos o que la tarjeta venció, no.
/// </summary>
public static class ClipStatusMessages
{
  private const string GenericRejection =
    "El banco no autorizó el pago. Intenta con otra tarjeta o comunícate con tu banco.";

  private static readonly Dictionary<string, string> ByCode = new(StringComparer.OrdinalIgnoreCase)
  {
    ["RE-ISS01"] = "La tarjeta no tiene fondos suficientes.",
    ["RE-ISS02"] = "El banco rechazó el cargo. Intenta con otra tarjeta o comunícate con tu banco.",
    ["RE-ISS03"] = "La tarjeta tiene restricciones para esta compra.",
    ["RE-ISS05"] = "Esta tarjeta no permite este tipo de compra.",
    ["RE-ISS06"] = "El banco pidió retener la tarjeta. Usa otra forma de pago.",
    ["RE-ISS07"] = "La tarjeta está vencida. Revisa la fecha de expiración.",
    ["RE-ISS08"] = "El cargo supera el límite de la tarjeta.",
    ["RE-ISS09"] = "El NIP es incorrecto.",
    ["RE-ISS10"] = "Se agotaron los intentos de NIP permitidos.",
    ["RE-ISS11"] = "Comunícate con tu banco para autorizar el cargo.",
    ["RE-ISS12"] = "El monto no es válido para esta tarjeta.",
    ["RE-ISS13"] = "El banco emisor no está disponible. Intenta de nuevo en unos minutos.",
    ["RE-ISS14"] = "El banco emisor no está disponible. Intenta de nuevo en unos minutos.",
    ["RE-ISS16"] = "El número de tarjeta no es válido.",
    ["RE-ISS07-EXP"] = "La tarjeta está vencida. Revisa la fecha de expiración.",
    ["RE-3DS01"] = "No se completó la verificación con tu banco. Intenta de nuevo.",
    ["RE-BIN01"] = "No aceptamos este tipo de tarjeta.",
    ["RE-ERI05"] = "El cobro no está disponible por ahora. Intenta más tarde.",
    ["PE-3DS01"] = "Tu banco necesita verificar el pago.",
    ["CA-AUT01"] = "El pago se canceló antes de completarse.",
    ["CA-MAN01"] = "El pago se canceló antes de completarse.",
    ["CA-MER01"] = "El pago se canceló antes de completarse."
  };

  public static string ForCode(string? statusCode)
    => !string.IsNullOrWhiteSpace(statusCode) && ByCode.TryGetValue(statusCode.Trim(), out var message)
      ? message
      : GenericRejection;

  /// <summary>
  /// Un rechazo del emisor se puede reintentar con otra tarjeta; uno de
  /// configuración o de tipo de tarjeta, no, y ofrecerlo sólo frustra.
  /// </summary>
  public static bool AllowsAnotherCard(string? statusCode)
    => !string.Equals(statusCode?.Trim(), "RE-ERI05", StringComparison.OrdinalIgnoreCase);
}
