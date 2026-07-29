using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using OrionERP.Web.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantQzTraySigningServiceTests
{
  [Fact]
  public void Sign_ProducesSha512SignatureMatchingConfiguredCertificate()
  {
    using var fixture = QzSigningFixture.Create();
    var service = fixture.CreateService();
    const string request = """{"call":"printers.print","params":{"data":"1B700032FA"}}""";

    var signature = Convert.FromBase64String(service.Sign(request));

    using var publicKey = fixture.Certificate.GetRSAPublicKey();
    Assert.NotNull(publicKey);
    Assert.True(publicKey.VerifyData(
      Encoding.UTF8.GetBytes(request),
      signature,
      HashAlgorithmName.SHA512,
      RSASignaturePadding.Pkcs1));
  }

  [Fact]
  public void GetCertificate_ReturnsConfiguredPublicCertificate()
  {
    using var fixture = QzSigningFixture.Create();
    var service = fixture.CreateService();

    var certificate = service.GetCertificate();

    Assert.Equal(fixture.CertificatePem, certificate);
    Assert.DoesNotContain("PRIVATE KEY", certificate, StringComparison.Ordinal);
  }

  [Fact]
  public void Sign_RejectsEmptyAndOversizedRequests()
  {
    using var fixture = QzSigningFixture.Create();
    var service = fixture.CreateService();

    Assert.Throws<ArgumentException>(() => service.Sign(string.Empty));
    Assert.Throws<ArgumentException>(() => service.Sign(
      new string('x', RestaurantQzTraySigningService.MaximumRequestLength + 1)));
  }

  [Fact]
  public void SigningApi_RequiresDedicatedBridgeAuthorizationAndDisablesCaching()
  {
    var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "../../../../../src/OrionERP.Web/Features/Restaurante/RestaurantQzTraySigningApi.cs")));

    Assert.Contains("RequireAuthorization(\"RestaurantQzBridge\")", source, StringComparison.Ordinal);
    Assert.Contains("no-store", source, StringComparison.Ordinal);
    Assert.DoesNotContain("PrivateKeyPath", source, StringComparison.Ordinal);
  }

  [Fact]
  public void SigningPolicy_UsesRestaurantRolesWithoutCircuitScopedRfcRequirement()
  {
    var source = File.ReadAllText(Path.GetFullPath(Path.Combine(
      AppContext.BaseDirectory,
      "../../../../../src/OrionERP.Web/Program.cs")));

    var policyStart = source.IndexOf("\"RestaurantQzBridge\"", StringComparison.Ordinal);
    Assert.True(policyStart >= 0);
    var policySource = source.Substring(
      policyStart,
      Math.Min(500, source.Length - policyStart));
    Assert.Contains("policy.RequireRole", policySource, StringComparison.Ordinal);
    Assert.Contains("\"RestauranteCaja\"", policySource, StringComparison.Ordinal);
    Assert.DoesNotContain("RoleForRfcRequirement", policySource, StringComparison.Ordinal);
  }

  private sealed class QzSigningFixture : IDisposable
  {
    private QzSigningFixture(
      string directory,
      string certificatePath,
      string privateKeyPath,
      X509Certificate2 certificate,
      string certificatePem)
    {
      Directory = directory;
      CertificatePath = certificatePath;
      PrivateKeyPath = privateKeyPath;
      Certificate = certificate;
      CertificatePem = certificatePem;
    }

    public string Directory { get; }
    public string CertificatePath { get; }
    public string PrivateKeyPath { get; }
    public X509Certificate2 Certificate { get; }
    public string CertificatePem { get; }

    public static QzSigningFixture Create()
    {
      var directory = Path.Combine(Path.GetTempPath(), $"orionerp-qz-signing-{Guid.NewGuid():N}");
      System.IO.Directory.CreateDirectory(directory);
      using var rsa = RSA.Create(2048);
      var request = new CertificateRequest(
        "CN=OrionERP QZ Test",
        rsa,
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);
      var certificate = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow.AddDays(1));
      var certificatePem = certificate.ExportCertificatePem();
      var certificatePath = Path.Combine(directory, "digital-certificate.txt");
      var privateKeyPath = Path.Combine(directory, "private-key.pem");
      File.WriteAllText(certificatePath, certificatePem);
      File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
      return new(directory, certificatePath, privateKeyPath, certificate, certificatePem);
    }

    public RestaurantQzTraySigningService CreateService()
      => new(Options.Create(new RestaurantQzTraySigningOptions
      {
        CertificatePath = CertificatePath,
        PrivateKeyPath = PrivateKeyPath
      }));

    public void Dispose()
    {
      Certificate.Dispose();
      if (System.IO.Directory.Exists(Directory))
      {
        System.IO.Directory.Delete(Directory, recursive: true);
      }
    }
  }
}
