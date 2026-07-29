namespace OrionERP.Web.Features.Restaurante;

public interface IRestaurantQzTraySigningService
{
  string GetCertificate();
  string Sign(string request);
}
