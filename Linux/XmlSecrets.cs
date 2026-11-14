// mRemoteNG authenticated XML compatibility and local password envelopes. GPL-2.0.
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace MGRemote.Linux;

internal static class XmlSecrets
{
    private const int LocalIterations = 600000;
    internal static string DecryptSource(string text, string password, XElement root)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length > 1024) throw new CryptographicException("Mot de passe XML requis ou trop long.");
        string engine = (string?)root.Attribute("EncryptionEngine") ?? "AES";
        string mode = (string?)root.Attribute("BlockCipherMode") ?? "GCM";
        string? iterationText = (string?)root.Attribute("KdfIterations");
        if (iterationText != null && !int.TryParse(iterationText, out _)) throw new InvalidDataException("KdfIterations XML invalide.");
        int iterations = iterationText == null ? 1000 : int.Parse(iterationText, System.Globalization.CultureInfo.InvariantCulture);
        if (iterations < 1000 || iterations > 1000000) throw new InvalidDataException("KdfIterations XML hors limites (1000–1000000).");
        if (text.Length > 24 * 1024 * 1024) throw new InvalidDataException("Secret XML trop volumineux.");
        byte[] input;
        try { input = Convert.FromBase64String(text); } catch (FormatException) { throw new CryptographicException("Secret XML non valide."); }
        if (input.Length < 48) throw new CryptographicException("Secret XML tronqué.");
        IBlockCipher block = engine switch { "AES" => new AesEngine(), "Twofish" => new TwofishEngine(), "Serpent" => new SerpentEngine(), _ => throw new NotSupportedException("Chiffrement XML non pris en charge : " + engine) };
        IAeadBlockCipher cipher = mode switch { "GCM" => new GcmBlockCipher(block), "EAX" => new EaxBlockCipher(block), _ => throw new NotSupportedException("Mode XML non pris en charge : " + mode) };
        byte[] pass = PbeParametersGenerator.Pkcs5PasswordToBytes(password.ToCharArray());
        byte[] key = [];
        byte[] plain = new byte[input.Length - 48];
        try {
            var kdf = new Pkcs5S2ParametersGenerator(); kdf.Init(pass, input[..16], iterations);
            key = ((KeyParameter)kdf.GenerateDerivedMacParameters(256)).GetKey();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, input[16..32], input[..16]));
            int count = cipher.ProcessBytes(input, 32, input.Length - 32, plain, 0);
            count += cipher.DoFinal(plain, count);
            return new UTF8Encoding(false, true).GetString(plain, 0, count);
        } catch (InvalidCipherTextException) { throw new CryptographicException("Mot de passe XML incorrect ou fichier altéré."); }
        finally { CryptographicOperations.ZeroMemory(pass); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Protect(string secret, string password)
    {
        if (password.Length < 12 || password.Length > 1024 || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Phrase secrète locale : 12 à 1024 caractères requis.");
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, LocalIterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = Encoding.UTF8.GetBytes(secret), encrypted = new byte[plain.Length], tag = new byte[16];
        try {
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, plain, encrypted, tag, "MGRemote-secret-v1"u8);
            return "v1:" + Convert.ToBase64String([..salt, ..nonce, ..tag, ..encrypted]);
        } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Unprotect(string envelope, string password)
    {
        if (!envelope.StartsWith("v1:") || envelope.Length > 16384 || password.Length > 1024) throw new CryptographicException("Secret local non valide.");
        byte[] data;
        try { data = Convert.FromBase64String(envelope[3..]); } catch (FormatException) { throw new CryptographicException("Secret local non valide."); }
        if (data.Length < 44) throw new CryptographicException("Secret local tronqué.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, data.AsSpan(0, 16), LocalIterations, HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[data.Length - 44];
        try {
            using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(16, 12), data.AsSpan(44), data.AsSpan(28, 16), plain, "MGRemote-secret-v1"u8);
            return Encoding.UTF8.GetString(plain);
        } catch (AuthenticationTagMismatchException) { throw new CryptographicException("Phrase secrète incorrecte ou secret altéré."); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
}
