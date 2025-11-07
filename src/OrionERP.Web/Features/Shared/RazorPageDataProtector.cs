using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OrionERP.Web.Features.Shared;

public static class RazorPageDataProtector
{
  private const string EncryptionKeyFileName = "rfc-register.aes.key";
  private const int NonceSize = 12;
  private const int TagSize = 16;
  private static readonly Lazy<byte[]> EncryptionKey = new(LoadOrCreateKey, true);

  public static byte[]? ProtectUtf8OrNull(string? plaintext)
  {
    if (string.IsNullOrEmpty(plaintext)) return null;

    var bytes = Encoding.UTF8.GetBytes(plaintext);
    var nonce = RandomNumberGenerator.GetBytes(NonceSize);
    var ciphertext = new byte[bytes.Length];
    var tag = new byte[TagSize];

    using var aesGcm = new AesGcm(EncryptionKey.Value, TagSize);
    aesGcm.Encrypt(nonce, bytes, ciphertext, tag);

    var payload = new byte[NonceSize + TagSize + ciphertext.Length];
    Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
    Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
    Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);
    return payload;
  }

  public static string? UnprotectUtf8OrNull(byte[]? ciphertext)
  {
    if (ciphertext is not { Length: > 0 }) return null;
    if (ciphertext.Length < NonceSize + TagSize) return null;

    try
    {
      var ciphertextSpan = ciphertext.AsSpan();
      var nonce = ciphertextSpan[..NonceSize];
      var tag = ciphertextSpan.Slice(NonceSize, TagSize);
      var encryptedData = ciphertextSpan[(NonceSize + TagSize)..];
      var plaintext = new byte[encryptedData.Length];

      using var aesGcm = new AesGcm(EncryptionKey.Value, TagSize);
      aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);
      return Encoding.UTF8.GetString(plaintext);
    }
    catch (CryptographicException)
    {
      return null;
    }
  }

  private static byte[] LoadOrCreateKey()
  {
    var appDataDirectory = Path.Combine(AppContext.BaseDirectory, "App_Data");
    Directory.CreateDirectory(appDataDirectory);
    var keyPath = Path.Combine(appDataDirectory, EncryptionKeyFileName);

    if (File.Exists(keyPath))
    {
      var existing = File.ReadAllBytes(keyPath);
      if (existing.Length == 32)
      {
        return existing;
      }
    }

    var key = RandomNumberGenerator.GetBytes(32);
    using var fileStream = new FileStream(keyPath, FileMode.Create, FileAccess.Write, FileShare.None);
    fileStream.Write(key, 0, key.Length);
    return key;
  }
}
