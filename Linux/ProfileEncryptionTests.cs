using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MGRemote.Linux;

internal static class ProfileEncryptionTests
{
    private sealed class GrowingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override long Length => 0; // Simulate a file which grows after the initial size observation.
    }
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
            var firstReader = Connections.LoadStore(file); var secondReader = Connections.LoadStore(file);
            string changedRevision = Connections.Save([item with { Description = "another instance" }], dataPath: file, expectedRevision: firstReader.Revision);
            string changed = File.ReadAllText(file);
            Refused<IOException>(() => Connections.Save(secondReader.Items, dataPath: file, expectedRevision: secondReader.Revision), "stale plaintext editor overwrote another instance");
            Check(File.ReadAllText(file) == changed && changedRevision == Connections.LoadStore(file).Revision && !changed.Contains("Revision"), "revision guard changed the file or leaked internal metadata");
            File.Delete(file);
            Refused<IOException>(() => Connections.Save([item], dataPath: file, expectedRevision: changedRevision), "stale editor recreated a deleted profile");
            Check(!File.Exists(file), "deleted profile was recreated");
            Connections.Save([item], [["Empty"]], file, expectedRevision: "missing");
            using var key = ProfileEncryption.CreateKey("synthetic profile phrase 123");
            Connections.Save([item], [["Empty"]], file, key);
            string encrypted = File.ReadAllText(file);
            Check(ProfileEncryption.IsEncryptedFile(file) && !encrypted.Contains(item.Host) && !encrypted.Contains(item.Name) && !encrypted.Contains(item.User) && !encrypted.Contains("Operations"), "metadata must be encrypted");
            Refused<CryptographicException>(() => Connections.LoadStore(file), "locked profile was readable");
            Refused<CryptographicException>(() => Connections.Save([], dataPath: file), "locked profile overwrite was accepted");
            Check(File.ReadAllText(file) == encrypted, "rejected write changed the file");
            using (var otherKey = ProfileEncryption.CreateKey("different synthetic profile phrase")) {
                Refused<CryptographicException>(() => Connections.Save([], dataPath: file, profileKey: otherKey), "a different profile key overwrote the encrypted store");
                Check(File.ReadAllText(file) == encrypted, "wrong-key overwrite changed the encrypted store");
            }
            using var envelope = JsonDocument.Parse(encrypted);
            Refused<CryptographicException>(() => { using var wrong = ProfileEncryption.Unlock(envelope.RootElement, "incorrect synthetic phrase"); }, "wrong password accepted");
            using var unlocked = ProfileEncryption.Unlock(envelope.RootElement, "synthetic profile phrase 123");
            var loaded = Connections.LoadStore(file, unlocked);
            Check(loaded.Items.Single().Host == item.Host && loaded.Items.Single().EncryptedPassword == item.EncryptedPassword && loaded.Folders.Any(p => p.SequenceEqual(new[] { "Empty" })), "encrypted round trip lost settings or nested secrets");
            Connections.Save(loaded.Items, loaded.Folders, file, unlocked, loaded.Revision);
            Check(File.ReadAllText(file) != encrypted, "save reused the GCM nonce");
            string fresh = File.ReadAllText(file);
            Refused<IOException>(() => Connections.Save(loaded.Items, loaded.Folders, file, unlocked, loaded.Revision), "stale encrypted editor overwrote a newer save");
            Check(File.ReadAllText(file) == fresh, "stale encrypted save changed ciphertext");
            Console.WriteLine("PASS: two-reader stale-save rejection for plaintext/encrypted profiles, deleted-profile protection and revision refresh without persisted metadata.");
            var modified = JsonNode.Parse(encrypted)!;
            byte[] tag = Convert.FromBase64String(modified["Tag"]!.GetValue<string>()); tag[0] ^= 1;
            modified["Tag"] = Convert.ToBase64String(tag);
            using (var tampered = JsonDocument.Parse(modified.ToJsonString())) Refused<CryptographicException>(() => ProfileEncryption.Decode(tampered.RootElement, key), "tampering accepted");
            string authenticatedStore = File.ReadAllText(file);
            File.WriteAllText(file, modified.ToJsonString());
            Refused<CryptographicException>(() => Connections.Save([item], dataPath: file, profileKey: key), "saving replaced unauthenticated existing bytes");
            Check(File.ReadAllText(file) == modified.ToJsonString(), "rejected save changed the tampered store");
            File.WriteAllText(file, authenticatedStore);
            modified["Iterations"] = int.MaxValue;
            using (var excessive = JsonDocument.Parse(modified.ToJsonString())) Refused<InvalidDataException>(() => ProfileEncryption.Decode(excessive.RootElement, key), "unbounded KDF settings accepted");
            Refused<ArgumentException>(() => ProfileEncryption.CreateKey("too short"), "short profile phrase accepted");
            using (var disposed = ProfileEncryption.CreateKey("synthetic disposed phrase")) {
                disposed.Dispose(); Refused<ObjectDisposedException>(() => Connections.Save([item], dataPath: file, profileKey: disposed), "disposed key accepted");
            }
            if (OperatingSystem.IsLinux()) Check((File.GetUnixFileMode(file) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0, "profile permissions allow other users");
            File.WriteAllText(file, JsonSerializer.Serialize(new[] { item }));
            Check(Connections.LoadStore(file).Items.Single().Name == item.Name, "legacy array compatibility");
            using (var growing = new GrowingStream(new byte[33])) Refused<InvalidDataException>(() => ProfileEncryption.ReadBoundedText(growing, 32), "file growth bypassed the read budget");
            using (var exact = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("12345678"))) Check(ProfileEncryption.ReadBoundedText(exact, 8) == "12345678", "exact read budget was rejected");
            if (OperatingSystem.IsLinux()) {
                string link = Path.Combine(directory, "linked.json"), broken = Path.Combine(directory, "broken.json"), linkedFolder = Path.Combine(directory, "linked-folder");
                File.CreateSymbolicLink(link, file); File.CreateSymbolicLink(broken, Path.Combine(directory, "absent.json"));
                Directory.CreateSymbolicLink(linkedFolder, directory);
                Refused<IOException>(() => Connections.LoadStore(link), "profile read followed a file symlink");
                Refused<IOException>(() => ProfileEncryption.IsEncryptedFile(link), "encryption detection followed a file symlink");
                Refused<IOException>(() => Connections.LoadStore(broken), "dangling file symlink was treated as a new profile");
                Refused<IOException>(() => Connections.LoadStore(Path.Combine(linkedFolder, "connections.json")), "profile read followed an ancestor symlink");
                Refused<IOException>(() => Connections.Save([item], dataPath: Path.Combine(linkedFolder, "new.json")), "profile write followed an ancestor symlink");
                Check(Connections.LoadStore(file).Items.Single().Host == item.Host, "symlink checks modified the real profile");
            }
            Console.WriteLine("PASS: authenticated profile encryption, locked/wrong-key/tampered overwrite guards, bounded growing-file reads, symlink rejection, nonce renewal, bounded KDF, key disposal, permissions and legacy JSON compatibility.");
        } finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
