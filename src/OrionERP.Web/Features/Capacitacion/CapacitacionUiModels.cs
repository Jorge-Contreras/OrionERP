using OrionERP.Application.Features.Capacitacion;

namespace OrionERP.Web.Features.Capacitacion;

/// <summary>
/// A presentation-only representation of a published content block. Text is
/// deliberately rendered as text by the Razor component; authored HTML is not
/// accepted by the training surface.
/// </summary>
public sealed record CapacitacionContentViewModel(
  long Id,
  string Type,
  string Title,
  string Content,
  IReadOnlyList<string> Items,
  string? ActionLabel = null,
  string? ActionUrl = null,
  int EstimatedMinutes = 0,
  bool IsRequired = true,
  bool IsCompleted = false,
  string? ResourceType = null,
  string? ResourceAlt = null);

public sealed record CapacitacionQuizOptionViewModel(string Key, string Text);

public static class CapacitacionUi
{
  public static readonly string[] FlowSteps = ["Preparar", "Explicar", "Demostrar", "Practicar", "Evaluar", "Cerrar"];

  public static string IconForModule(string? module) => module?.Trim().ToUpperInvariant() switch
  {
    "CFDI" or "FISCAL" or "SAT" => "oi-document",
    "CONTABILIDAD" or "ACCOUNTING" => "oi-calculator",
    "RESERVACIONES" or "RESERVATIONS" => "oi-calendar",
    "LOGÍSTICA" or "LOGISTICA" or "LOGISTICS" => "oi-box",
    "CAPITAL HUMANO" or "HR" => "oi-people",
    "RESTAURANTE" => "oi-cart",
    "ÓRDENES DE TRABAJO" or "ORDENES DE TRABAJO" => "oi-wrench",
    _ => "oi-book"
  };

  public static string StatusLabel(string? status) => status?.Trim().ToUpperInvariant() switch
  {
    "ASSIGNED" or "ASIGNADO" or "ASIGNADA" or "PENDING" or "PENDIENTE" => "Pendiente",
    "IN_PROGRESS" or "EN_PROGRESO" or "EN_CURSO" or "ACTIVE" or "ACTIVA" => "En curso",
    "COMPLETED" or "COMPLETADO" or "COMPLETADA" or "FINALIZADO" or "FINALIZADA" => "Completado",
    "ESPERA_FIRMA" => "Pendiente de firma",
    "ESPERA_ACUSE" => "Pendiente de acuse",
    "OVERDUE" or "VENCIDO" => "Vencido",
    "SCHEDULED" or "PROGRAMADA" => "Programada",
    "CANCELLED" or "CANCELADA" => "Cancelada",
    _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status
  };

  public static string StatusCss(string? status) => status?.Trim().ToUpperInvariant() switch
  {
    "COMPLETED" or "COMPLETADO" or "COMPLETADA" or "FINALIZADO" or "FINALIZADA" => "cap-badge--success",
    "OVERDUE" or "VENCIDO" or "CANCELLED" or "CANCELADA" => "cap-badge--danger",
    "IN_PROGRESS" or "EN_PROGRESO" or "EN_CURSO" or "ACTIVE" or "ACTIVA" => "cap-badge--info",
    _ => "cap-badge--warn"
  };

  public static int ClampPercent(decimal value) => Math.Clamp((int)Math.Round(value), 0, 100);

  public static IReadOnlyList<string> GetStructuredItems(string type, string content, string? configurationJson)
  {
    if (!string.IsNullOrWhiteSpace(configurationJson))
    {
      try
      {
        using var document = System.Text.Json.JsonDocument.Parse(configurationJson);
        foreach (var propertyName in new[] { "items", "diagram", "demoSteps" })
        {
          if (!document.RootElement.TryGetProperty(propertyName, out var property)
              || property.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
          var items = property.EnumerateArray()
            .Where(item => item.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(item => item.GetString()?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
          if (items.Length > 0) return items;
        }
      }
      catch (System.Text.Json.JsonException) { }
    }

    if (!type.Equals(CapacitacionCodes.BloquePasos, StringComparison.OrdinalIgnoreCase)) return [];
    return System.Text.RegularExpressions.Regex.Matches(content, @"(?:^|\s)\d+\.\s*(.*?)(?=\s+\d+\.|$)")
      .Select(match => match.Groups[1].Value.Trim())
      .Where(item => item.Length > 0)
      .ToArray();
  }

  public static string? GetInstructorNotes(string? configurationJson)
  {
    if (string.IsNullOrWhiteSpace(configurationJson)) return null;
    try
    {
      using var document = System.Text.Json.JsonDocument.Parse(configurationJson);
      return document.RootElement.TryGetProperty("notasInstructor", out var notes)
        && notes.ValueKind == System.Text.Json.JsonValueKind.String ? notes.GetString() : null;
    }
    catch (System.Text.Json.JsonException) { return null; }
  }
}
