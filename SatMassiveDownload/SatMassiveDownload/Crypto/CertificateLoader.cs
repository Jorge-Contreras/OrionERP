using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;

namespace Sat.MassiveDownload.Crypto
{
    public static class CertificateLoader
    {
        public static X509Certificate2 FromPfx(string pfxPath, string pfxPassword)
            => X509CertificateLoader.LoadPkcs12FromFile(
                pfxPath,
                pfxPassword,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet,
                Pkcs12LoaderLimits.Defaults);

        public static X509Certificate2 FromCerAndKey(string cerPath, string keyPath, string keyPassword)
        {
            var certificateBytes = File.ReadAllBytes(cerPath);
            var keyBytes = File.ReadAllBytes(keyPath);

            return FromCerAndKeyBytes(certificateBytes, keyBytes, keyPassword);
        }

        public static X509Certificate2 FromCerAndKeyBytes(byte[] certificateBytes, byte[] keyBytes, string keyPassword)
        {
            if (certificateBytes is not { Length: > 0 })
            {
                throw new ArgumentException("El certificado (.cer) no contiene datos.", nameof(certificateBytes));
            }

            if (keyBytes is not { Length: > 0 })
            {
                throw new ArgumentException("La llave privada (.key) no contiene datos.", nameof(keyBytes));
            }

            using var publicCert = X509CertificateLoader.LoadCertificate(certificateBytes);

            using var rsa = CreateRsaFromKeyBytes(keyBytes, keyPassword);
            using var withPrivate = publicCert.CopyWithPrivateKey(rsa);
            var pfxBytes = withPrivate.Export(X509ContentType.Pkcs12);

            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: (string?)null,
                keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet,
                loaderLimits: Pkcs12LoaderLimits.Defaults);
        }

        private static bool LooksLikePem(byte[] data)
        {
            var head = Encoding.ASCII.GetString(data, 0, Math.Min(64, data.Length));
            return head.Contains("-----BEGIN", StringComparison.Ordinal);
        }

        private static RSA CreateRsaFromKeyBytes(byte[] keyBytes, string? keyPassword)
        {
            keyPassword ??= string.Empty;

            if (LooksLikePem(keyBytes))
            {
                using var ms = new MemoryStream(keyBytes, writable: false);
                using var sr = new StreamReader(ms, Encoding.ASCII);
                var pemReader = new PemReader(sr, new PasswordFinder(keyPassword));
                var obj = pemReader.ReadObject()
                          ?? throw new InvalidOperationException("No se pudo leer la llave privada (PEM).");

                var rsaParams = obj switch
                {
                    AsymmetricCipherKeyPair kp => (RsaPrivateCrtKeyParameters)kp.Private,
                    RsaPrivateCrtKeyParameters rp => rp,
                    AsymmetricKeyParameter akp when akp.IsPrivate => (RsaPrivateCrtKeyParameters)akp,
                    _ => throw new InvalidOperationException($"Tipo de llave PEM no soportado: {obj.GetType().Name}")
                };

                var rsaPem = RSA.Create();
                rsaPem.ImportParameters(DotNetUtilities.ToRSAParameters(rsaParams));
                return rsaPem;
            }

            var rsa = RSA.Create();
            try
            {
                rsa.ImportEncryptedPkcs8PrivateKey(keyPassword.AsSpan(), keyBytes, out _);
                return rsa;
            }
            catch (CryptographicException)
            {
                try
                {
                    rsa.ImportPkcs8PrivateKey(keyBytes, out _);
                    return rsa;
                }
                catch (CryptographicException ex)
                {
                    rsa.Dispose();
                    throw new InvalidOperationException(
                        "No se pudo leer la llave privada (.key). Verifica contraseña y que sea PKCS#8 (DER).", ex);
                }
            }
        }

        private sealed class PasswordFinder(string pass) : IPasswordFinder
        {
            public char[] GetPassword() => pass.ToCharArray();
        }
    }
}
