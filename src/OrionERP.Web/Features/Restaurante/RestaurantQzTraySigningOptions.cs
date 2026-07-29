namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantQzTraySigningOptions
{
  public const string SectionName = "RestaurantPos:QzTraySigning";

  public string CertificatePath { get; set; } = @"C:\QZ Tray\digital-certificate.txt";
  public string PrivateKeyPath { get; set; } = @"C:\QZ Tray\private-key.pem";
}
