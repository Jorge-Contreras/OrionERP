namespace OrionERP.Web.Identity;

public sealed class GraphMailOptions
{
  public const string SectionName = "GraphMail";

  public string TenantId { get; set; } = string.Empty;
  public string ClientId { get; set; } = string.Empty;
  public string ClientSecret { get; set; } = string.Empty;
  public string SenderAddress { get; set; } = string.Empty;
  public string? PublicBaseUrl { get; set; }
}
