namespace OrionERP.Web.Features.Reservaciones.OpenClaw;

public sealed class OpenClawApiOptions
{
  public const string SectionName = "OpenClawApi";

  public string ApiKey { get; set; } = string.Empty;
  public int PdfTokenLifetimeMinutes { get; set; } = 30;
  public string? PublicBaseUrl { get; set; }
}
