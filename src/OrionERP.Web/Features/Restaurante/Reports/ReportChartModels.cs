namespace OrionERP.Web.Features.Restaurante.Reports;

/// <summary>Tono semántico de una barra: describe qué tan bien está el dato, no su color.</summary>
public enum ReportTone
{
  Neutral,
  Good,
  Warning,
  Critical
}

/// <summary>Una barra de las gráficas horizontales de la página de reportes.</summary>
public sealed class ReportBarItem
{
  public string Label { get; set; } = string.Empty;
  public string? Sublabel { get; set; }
  public decimal Value { get; set; }
  public string Display { get; set; } = string.Empty;
  public ReportTone Tone { get; set; } = ReportTone.Neutral;
  public IReadOnlyList<string> Codes { get; set; } = [];
}
