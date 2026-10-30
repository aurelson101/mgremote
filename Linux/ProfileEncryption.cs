// Optional authenticated encryption of the complete local profile. GPL-2.0.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MGRemote.Linux;

public sealed class ProfileKey : IDisposable
{
    internal byte[] Key { get; }
    internal byte[] Salt { get; }
    private bool _disposed;
    internal ProfileKey(string password, byte[] salt)
    {
        if (password.Length is < 12 or > 1024 || string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Phrase du profil : 12 à 1024 caractères requis.");
        Salt = salt.ToArray();
        Key = Rfc2898DeriveBytes.Pbkdf2(password, Salt, ProfileEncryption.Iterations, HashAlgorithmName.SHA256, 32);
    }
    internal void CheckUsable() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() { if (_disposed) return; _disposed = true; CryptographicOperations.ZeroMemory(Key); CryptographicOperations.ZeroMemory(Salt); }
}

internal static class ProfileEncryption
{
    internal const int Iterations = 600000;
    internal const int MaxPlaintext = 16 * 1024 * 1024;
    internal const int MaxFile = 24 * 1024 * 1024;
    private const string Format = "MGRemote-profile-v1";
    private sealed record Envelope(string Format, string Kdf, int Iterations, string Salt, string Nonce, string Tag, string Ciphertext);
    internal static bool IsEnvelope(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Format", out _);
    internal static bool IsEncryptedFile(string file)
    {
        if (!File.Exists(file)) return false;
        if (new FileInfo(file).Length > MaxFile) throw new InvalidDataException("Profil trop volumineux.");
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        return IsEnvelope(json.RootElement);
    }
    internal static ProfileKey CreateKey(string password) => new(password, RandomNumberGenerator.GetBytes(16));
    private static Envelope ReadEnvelope(JsonElement root)
    {
        var envelope = root.Deserialize<Envelope>() ?? throw new InvalidDataException("Profil chiffré invalide.");
        if (envelope.Format != Format || envelope.Kdf != "PBKDF2-SHA256" || envelope.Iterations != Iterations || envelope.Salt == null || envelope.Nonce == null || envelope.Tag == null || envelope.Ciphertext == null || envelope.Ciphertext.Length > MaxFile)
            throw new InvalidDataException("Format ou paramètres du profil chiffré non pris en charge.");
        return envelope;
    }
    internal static ProfileKey Unlock(JsonElement root, string password)
    {
        var envelope = ReadEnvelope(root);
        byte[] salt = Convert.FromBase64String(envelope.Salt);
        if (salt.Length != 16) throw new InvalidDataException("Sel du profil invalide.");
        var key = new ProfileKey(password, salt);
        try { Decode(root, key); return key; } catch { key.Dispose(); throw; }
    }
    internal static string Encode(string content, ProfileKey key)
    {
        key.CheckUsable();
        byte[] plain = Encoding.UTF8.GetBytes(content);
        if (plain.Length > MaxPlaintext) { CryptographicOperations.ZeroMemory(plain); throw new InvalidDataException("Profil trop volumineux."); }
        byte[] nonce = RandomNumberGenerator.GetBytes(12), ciphertext = new byte[plain.Length], tag = new byte[16];
        try {
            using var aes = new AesGcm(key.Key, 16);
            aes.Encrypt(nonce, plain, ciphertext, tag, "MGRemote-profile-v1"u8);
            return JsonSerializer.Serialize(new Envelope(Format, "PBKDF2-SHA256", Iterations, Convert.ToBase64String(key.Salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext)));
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Decode(JsonElement root, ProfileKey? key)
    {
        if (key == null) throw new CryptographicException("Profil chiffré verrouillé : déverrouillez-le avant utilisation.");
        key.CheckUsable(); var envelope = ReadEnvelope(root);
        byte[] salt = Convert.FromBase64String(envelope.Salt), nonce = Convert.FromBase64String(envelope.Nonce), tag = Convert.FromBase64String(envelope.Tag), ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length > MaxPlaintext || !CryptographicOperations.FixedTimeEquals(salt, key.Salt)) throw new CryptographicException("Profil chiffré invalide ou clé incorrecte.");
        byte[] plain = new byte[ciphertext.Length];
        try {
            using var aes = new AesGcm(key.Key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plain, "MGRemote-profile-v1"u8);
            return new UTF8Encoding(false, true).GetString(plain);
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
