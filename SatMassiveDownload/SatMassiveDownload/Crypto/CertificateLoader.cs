using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Sat.MassiveDownload.Crypto
{
    public static class CertificateLoader
    {
        public static X509Certificate2 FromPfx(string pfxPath, string pfxPassword)
            => X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                pfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        public static X509Certificate2 FromCerAndKey(string cerPath, string keyPath, string keyPassword)
        {
            var certificateBytes = File.ReadAllBytes(cerPath);
            var keyBytes = File.ReadAllBytes(keyPath);

            return FromCerAndKeyBytes(certificateBytes, keyBytes, keyPassword);
        }

        public static X509Certificate2 FromCerAndKeyBytes(byte[] certificateBytes, byte[] keyBytes, string keyPassword)
        {
            var publicCert = X509CertificateLoader.LoadCertificate(certificateBytes);

            using var rsa = RSA.Create();

            if (LooksLikePem(keyBytes))
            {
                var pem = Encoding.ASCII.GetString(keyBytes);
                try
                {
                    rsa.ImportFromEncryptedPem(pem, keyPassword);
                }
                catch (CryptographicException)
                {
                    try
                    {
                        rsa.ImportFromPem(pem);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new InvalidOperationException(
                            "No se pudo leer la llave privada (.key). Verifica contraseña y que sea PKCS#8 (PEM).",
                            ex);
                    }
                }
            }
            else
            {
                try
                {
                    rsa.ImportEncryptedPkcs8PrivateKey(keyPassword.AsSpan(), keyBytes, out _);
                }
                catch (CryptographicException)
                {
                    try
                    {
                        rsa.ImportPkcs8PrivateKey(keyBytes, out _);
                    }
                    catch (CryptographicException ex)
                    {
                        throw new InvalidOperationException(
                            "No se pudo leer la llave privada (.key). Verifica contraseña y que sea PKCS#8 (DER).", ex);
                    }
                }
            }

            // Asociar la private key al certificado y devolver un X509 “bien horneado”
            using var withPrivate = publicCert.CopyWithPrivateKey(rsa);
            var pfxBytes = withPrivate.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        }

        private static bool LooksLikePem(byte[] data)
        {
            var head = Encoding.ASCII.GetString(data, 0, Math.Min(64, data.Length));
            return head.Contains("-----BEGIN", StringComparison.Ordinal);
        }

    }
}
