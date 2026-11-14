// Optional authenticated encryption of the complete local profile. GPL-2.0.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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
    private const int NoFollow = 0x20000;
    private const int ReadFlags = NoFollow | 0x80000 | 0x800; // O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK (Linux).
    private const int DirectoryFlags = ReadFlags | 0x10000; // O_DIRECTORY.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, string path, int flags);
    private sealed record Envelope(string Format, string Kdf, int Iterations, string Salt, string Nonce, string Tag, string Ciphertext);
    internal static bool IsEnvelope(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Format", out _);
    internal static bool IsEncryptedFile(string file)
    {
        string text;
        try { text = ReadProfileText(file); } catch (FileNotFoundException) { return false; }
        using var json = JsonDocument.Parse(text);
        return IsEnvelope(json.RootElement);
    }
    internal static void RejectLinkedPath(string file)
    {
        string full = Path.GetFullPath(file);
        if (new FileInfo(full).LinkTarget != null) throw new IOException("Profil redirigé par lien refusé.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(full)!); directory != null; directory = directory.Parent)
            if (directory.LinkTarget != null) throw new IOException("Dossier du profil redirigé par lien refusé.");
    }
    private static SafeFileHandle CheckedHandle(int descriptor, string file)
    {
        if (descriptor >= 0) return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        int error = Marshal.GetLastPInvokeError();
        if (error == 2) throw new FileNotFoundException("Fichier introuvable.", file);
        throw new IOException("Ouverture sûre du fichier refusée (code " + error + ").");
    }
    internal static FileStream OpenLocalRead(string file, bool allowLinks = false)
    {
        string full = Path.GetFullPath(file);
        if (!OperatingSystem.IsLinux()) { RejectLinkedPath(full); return new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read); }
        // Profiles refuse links; SSH files may follow them. Checks/read use the opened target descriptor.
        int readFlags = allowLinks ? ReadFlags & ~NoFollow : ReadFlags;
        int directoryFlags = allowLinks ? DirectoryFlags & ~NoFollow : DirectoryFlags;
        SafeFileHandle directory = CheckedHandle(Open("/", directoryFlags), full);
        try {
            foreach (string component in Path.GetDirectoryName(full)!.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
                var next = CheckedHandle(OpenAt(directory.DangerousGetHandle().ToInt32(), component, directoryFlags), full);
                directory.Dispose(); directory = next;
            }
            var handle = CheckedHandle(OpenAt(directory.DangerousGetHandle().ToInt32(), Path.GetFileName(full), readFlags), full);
            try { return new FileStream(handle, FileAccess.Read); } catch { handle.Dispose(); throw; }
        } finally { directory.Dispose(); }
    }
    internal static string ReadProfileText(string file)
    {
        using var stream = OpenLocalRead(file);
        return ReadBoundedText(stream, MaxFile);
    }
    internal static string Revision(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    internal static string ReadRevision(string file)
    {
        try { return Revision(ReadProfileText(file)); }
        catch (FileNotFoundException) { return "missing"; }
    }
    internal static string ReadBoundedText(Stream stream, int maximumBytes)
    {
        if (!stream.CanSeek) throw new IOException("Le profil doit être un fichier ordinaire.");
        if (stream.Length > maximumBytes) throw new InvalidDataException("Profil trop volumineux.");
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - bytes.Length + 1))) > 0) {
            if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Profil trop volumineux.");
            bytes.Write(buffer, 0, count);
        }
        ReadOnlySpan<byte> text = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (text.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) text = text[3..];
        return new UTF8Encoding(false, true).GetString(text);
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
