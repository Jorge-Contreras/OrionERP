namespace OrionERP.Web.Features.Cfdi.DescargaMasiva;

public sealed class SatIntegrationOptions
{
  public bool UsePfx { get; set; } = true;
  public string? PfxPath { get; set; }
  public string? PfxPassword { get; set; }

  public string? CerPath { get; set; }
  public string? KeyPath { get; set; }
  public string? KeyPassword { get; set; }

  public string RfcSolicitante { get; set; } = "";
}
