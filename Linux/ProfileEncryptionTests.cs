using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MGRemote.Linux;

internal static class ProfileEncryptionTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-profile-test-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "connections.json");
        var item = new Connection { Name = "synthetic-profile-host", Host = "server.example", User = "synthetic-user", FolderPath = ["Operations"], EncryptedPassword = "v1:synthetic-envelope" };
        void Check(bool condition, string message) { if (!condition) throw new Exception("Profile encryption: " + message); }
        void Refused<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Profile encryption: " + message); }
        try {
            Connections.Save([item], [["Empty"]], file);
            Check(!ProfileEncryption.IsEncryptedFile(file) && Connections.LoadStore(file).Items.Single().User == item.User, "plaintext compatibility");
            using var key = ProfileEncryption.CreateKey("synthetic profile phrase 123");
            Connections.Save([item], [["Empty"]], file, key);
            string encrypted = File.ReadAllText(file);
            Check(ProfileEncryption.IsEncryptedFile(file) && !encrypted.Contains(item.Host) && !encrypted.Contains(item.Name) && !encrypted.Contains(item.User) && !encrypted.Contains("Operations"), "metadata must be encrypted");
            Refused<CryptographicException>(() => Connections.LoadStore(file), "locked profile was readable");
            Refused<CryptographicException>(() => Connections.Save([], dataPath: file), "locked profile overwrite was accepted");
            Check(File.ReadAllText(file) == encrypted, "rejected write changed the file");
            using var envelope = JsonDocument.Parse(encrypted);
            Refused<CryptographicException>(() => { using var wrong = ProfileEncryption.Unlock(envelope.RootElement, "incorrect synthetic phrase"); }, "wrong password accepted");
            using var unlocked = ProfileEncryption.Unlock(envelope.RootElement, "synthetic profile phrase 123");
            var loaded = Connections.LoadStore(file, unlocked);
            Check(loaded.Items.Single().Host == item.Host && loaded.Items.Single().EncryptedPassword == item.EncryptedPassword && loaded.Folders.Any(p => p.SequenceEqual(new[] { "Empty" })), "encrypted round trip lost settings or nested secrets");
            Connections.Save(loaded.Items, loaded.Folders, file, unlocked);
            Check(File.ReadAllText(file) != encrypted, "save reused the GCM nonce");
            var modified = JsonNode.Parse(encrypted)!;
            byte[] tag = Convert.FromBase64String(modified["Tag"]!.GetValue<string>()); tag[0] ^= 1;
            modified["Tag"] = Convert.ToBase64String(tag);
            using (var tampered = JsonDocument.Parse(modified.ToJsonString())) Refused<CryptographicException>(() => ProfileEncryption.Decode(tampered.RootElement, key), "tampering accepted");
            modified["Iterations"] = int.MaxValue;
            using (var excessive = JsonDocument.Parse(modified.ToJsonString())) Refused<InvalidDataException>(() => ProfileEncryption.Decode(excessive.RootElement, key), "unbounded KDF settings accepted");
            Refused<ArgumentException>(() => ProfileEncryption.CreateKey("too short"), "short profile phrase accepted");
            using (var disposed = ProfileEncryption.CreateKey("synthetic disposed phrase")) {
                disposed.Dispose(); Refused<ObjectDisposedException>(() => Connections.Save([item], dataPath: file, profileKey: disposed), "disposed key accepted");
            }
            if (OperatingSystem.IsLinux()) Check((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "profile permissions allow other users");
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { item }));
            Check(Connections.LoadStore(file).Items.Single().Name == item.Name, "legacy array compatibility");
            Console.WriteLine("PASS: optional profile encryption, metadata/folder/secret round trip, locked write guard, wrong password, tampering, nonce renewal, bounded KDF, key disposal, permissions and legacy JSON compatibility.");
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
