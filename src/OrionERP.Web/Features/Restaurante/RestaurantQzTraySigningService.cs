using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace OrionERP.Web.Features.Restaurante;

public sealed class RestaurantQzTraySigningService : IRestaurantQzTraySigningService
{
  public const int MaximumRequestLength = 262_144;

  private readonly IOptions<RestaurantQzTraySigningOptions> options;

  public RestaurantQzTraySigningService(IOptions<RestaurantQzTraySigningOptions> options)
  {
    this.options = options;
  }

  public string GetCertificate()
  {
    var path = RequireExistingFile(options.Value.CertificatePath, "certificado digital");
    var certificate = File.ReadAllText(path);
    if (!certificate.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ||
        !certificate.Contains("-----END CERTIFICATE-----", StringComparison.Ordinal))
    {
      throw new InvalidOperationException("El certificado de QZ Tray no tiene formato PEM válido.");
    }

    return certificate;
  }

  public string Sign(string request)
  {
    if (string.IsNullOrWhiteSpace(request))
    {
      throw new ArgumentException("La solicitud de firma está vacía.", nameof(request));
    }
    if (request.Length > MaximumRequestLength)
    {
      throw new ArgumentException("La solicitud de firma excede el tamaño permitido.", nameof(request));
    }

    var path = RequireExistingFile(options.Value.PrivateKeyPath, "llave privada");
    var privateKey = File.ReadAllText(path);
    using var rsa = RSA.Create();
    rsa.ImportFromPem(privateKey);
    var signature = rsa.SignData(
      Encoding.UTF8.GetBytes(request),
      HashAlgorithmName.SHA512,
      RSASignaturePadding.Pkcs1);
    return Convert.ToBase64String(signature);
  }

  private static string RequireExistingFile(string? configuredPath, string description)
  {
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
      throw new InvalidOperationException($"No se configuró la ruta de {description} de QZ Tray.");
    }

    var fullPath = Path.GetFullPath(configuredPath.Trim());
    if (!File.Exists(fullPath))
    {
      throw new InvalidOperationException($"No se encontró el archivo de {description} de QZ Tray.");
    }

    return fullPath;
  }
}
