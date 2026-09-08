using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace OrionERP.Web.Services;

/// <summary>
/// Traduce excepciones inesperadas de la capa Web en un mensaje entendible para el usuario
/// (qué se intentaba hacer, qué salió mal y qué puede hacer) y, al mismo tiempo, registra la
/// excepción completa con un código de referencia para poder diagnosticarla después.
/// </summary>
/// <remarks>
/// No sustituye a los mensajes de negocio que ya devuelven los servicios vía
/// <c>LogisticsCommandResult.Fail(...)</c>: se usa únicamente en los <c>catch</c> de errores
/// que el servicio no previó y volvió a lanzar.
/// </remarks>
public interface IOperationErrorPresenter
{
  /// <summary>
  /// Registra <paramref name="ex"/> con contexto técnico y devuelve un mensaje para el usuario.
  /// </summary>
  /// <param name="ex">Excepción capturada.</param>
  /// <param name="attemptedOperation">
  /// Operación en infinitivo y en minúsculas, p. ej. <c>"guardar la orden de compra"</c>.
  /// </param>
  /// <param name="context">
  /// Datos no sensibles útiles para reproducir el problema (ids, filtros). Se serializan de forma
  /// superficial en el log; nunca incluir contraseñas, tokens ni cadenas de conexión.
  /// </param>
  string ToUserMessage(
    Exception ex,
    string attemptedOperation,
    object? context = null,
    [CallerMemberName] string? caller = null,
    [CallerFilePath] string? callerFile = null);
}

public sealed class OperationErrorPresenter : IOperationErrorPresenter
{
  private readonly ILogger<OperationErrorPresenter> _logger;

  public OperationErrorPresenter(ILogger<OperationErrorPresenter> logger)
    => _logger = logger;

  public string ToUserMessage(
    Exception ex,
    string attemptedOperation,
    object? context = null,
    [CallerMemberName] string? caller = null,
    [CallerFilePath] string? callerFile = null)
  {
    var operation = string.IsNullOrWhiteSpace(attemptedOperation)
      ? "completar la operación"
      : attemptedOperation.Trim();

    var reference = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    var component = string.IsNullOrEmpty(callerFile)
      ? caller
      : $"{Path.GetFileNameWithoutExtension(callerFile)}.{caller}";

    var (userMessage, isExpected) = Classify(ex, operation, reference);

    if (isExpected)
    {
      _logger.LogWarning(
        ex,
        "Operación controlada fallida [{Reference}] al {Operation} en {Component}. Contexto: {@Context}",
        reference, operation, component, context);
    }
    else
    {
      _logger.LogError(
        ex,
        "Excepción inesperada [{Reference}] al {Operation} en {Component}. Contexto: {@Context}",
        reference, operation, component, context);
    }

    return userMessage;
  }

  private static (string Message, bool IsExpected) Classify(Exception ex, string operation, string reference)
  {
    for (var current = ex; current is not null; current = current.InnerException)
    {
      switch (current)
      {
        case OperationCanceledException:
          return ($"La acción de {operation} se canceló o tardó demasiado. Verifica tu conexión e inténtalo de nuevo.", true);

        case DbUpdateConcurrencyException:
          return ($"No se pudo {operation} porque otra persona modificó esta información mientras la editabas. Actualiza la página y vuelve a intentarlo.", true);

        case SqlException sql:
          return (TranslateSql(sql, operation, reference), IsExpectedSql(sql));
      }

      if (current is TimeoutException)
      {
        return ($"No se pudo {operation} porque la base de datos tardó demasiado en responder. Inténtalo de nuevo en un momento.", true);
      }
    }

    return ($"No se pudo {operation} por un problema inesperado. El equipo técnico quedó notificado (referencia {reference}). Actualiza la página e inténtalo nuevamente.", false);
  }

  private static bool IsExpectedSql(SqlException sql) => sql.Number switch
  {
    547 or 2601 or 2627 or 515 or 8152 or 2628 or 1205 or -2 => true,
    _ => false
  };

  private static string TranslateSql(SqlException sql, string operation, string reference) => sql.Number switch
  {
    547 when sql.Message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase)
      => $"No se puede {operation} porque este registro está siendo utilizado por otros movimientos del sistema. Primero elimina o reasigna los movimientos relacionados.",
    547
      => $"No se pudo {operation} porque uno de los datos capturados no corresponde a un registro válido del sistema.",
    2601 or 2627
      => $"No se pudo {operation} porque ya existe un registro con los mismos datos clave.",
    515
      => $"No se pudo {operation} porque falta capturar un dato obligatorio.",
    8152 or 2628
      => $"No se pudo {operation} porque uno de los valores capturados es más largo de lo permitido. Acórtalo e inténtalo de nuevo.",
    1205
      => $"No se pudo {operation} porque otra operación estaba usando los mismos datos al mismo tiempo. Inténtalo de nuevo.",
    -2
      => $"No se pudo {operation} porque la base de datos tardó demasiado en responder. Inténtalo de nuevo en un momento.",
    _
      => $"No se pudo {operation} por un problema de la base de datos. El equipo técnico quedó notificado (referencia {reference})."
  };
}
