namespace OrionERP.Infrastructure.Features.Mail;

public class MicrosoftGraphMailOptions
{
  public string TenantId { get; set; } = string.Empty;
  public string ClientId { get; set; } = string.Empty;
  public string ClientSecret { get; set; } = string.Empty;
  public string SenderAddress { get; set; } = string.Empty;
  public string? PublicBaseUrl { get; set; }
}

public sealed class GraphMailOptions : MicrosoftGraphMailOptions
{
  public const string SectionName = "GraphMail";
}

public sealed class BonhomiaGraphMailOptions : MicrosoftGraphMailOptions
{
  public const string SectionName = "BonhomiaGraphMail";

  public BonhomiaGraphMailOptions()
  {
    SenderAddress = "recepcion@bonhomiasuites.com";
  }
}

public sealed class BrunoGraphMailOptions : MicrosoftGraphMailOptions
{
  public const string SectionName = "BrunoGraphMail";

  public BrunoGraphMailOptions()
  {
    SenderAddress = "hola@brunosgarden.com";
    PublicBaseUrl = "https://brunosgarden.com";
  }
}
