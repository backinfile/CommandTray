using System.Security.Cryptography;
using System.Text;

namespace CommandTray.Sync;

public sealed record SecretEnvelope(int Version, string Salt, string Nonce, string Ciphertext, string Tag)
{
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 600_000;

    public static SecretEnvelope Encrypt(ReadOnlySpan<byte> cleartext, ReadOnlySpan<char> passphrase, string recordId)
    {
        if (passphrase.IsEmpty) throw new ArgumentException("A vault passphrase is required.", nameof(passphrase));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[cleartext.Length];
        var tag = new byte[TagSize];
        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Iterations, HashAlgorithmName.SHA256);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Encrypt(nonce, cleartext, ciphertext, tag, Context(recordId));
            return new(1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public byte[] Decrypt(ReadOnlySpan<char> passphrase, string recordId)
    {
        if (Version != 1) throw new CryptographicException("Unsupported secret envelope version.");
        var ciphertext = Convert.FromBase64String(Ciphertext);
        var salt = Convert.FromBase64String(Salt);
        if (salt.Length != 16) throw new CryptographicException("Invalid secret envelope salt.");
        var cleartext = new byte[ciphertext.Length];
        Span<byte> key = stackalloc byte[KeySize];
        try
        {
            Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, key, Iterations, HashAlgorithmName.SHA256);
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(Convert.FromBase64String(Nonce), ciphertext, Convert.FromBase64String(Tag),
                cleartext, Context(recordId));
            return cleartext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(cleartext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Context(string recordId) => Encoding.UTF8.GetBytes("CommandTray/secret/v1/" + recordId);
}
