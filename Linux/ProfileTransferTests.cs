using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MGRemote.Linux;

internal static class ProfileTransferTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-portable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string active = Path.Combine(directory, "active", "connections.json"), export = Path.Combine(directory, "portable.mgremote");
        const string phrase = "synthetic portable export phrase";
        void Check(bool ok, string message) { if (!ok) throw new Exception("Portable profile: " + message); }
        void Refused<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Portable profile: " + message); }
        try {
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(directory) : default;
            string keyPath = Path.Combine(directory, "private-key-fixture"); File.WriteAllText(keyPath, "synthetic-private-key-material-never-packaged");
            var ssh = new Connection { Name = "Portable SSH", Host = "portable.example.invalid", User = "portable-user", Identity = keyPath, JumpHost = "bastion-alias", TunnelEnabled = true };
            var rdp = new Connection { Name = "Portable RDP", Host = "rdp.example.invalid", Protocol = "RDP", Port = 3389, FolderPath = ["Servers"], Clipboard = true, EncryptedPassword = "v1:preserved-existing-secret" };
            Connections.Save([ssh, rdp], [["Empty"]], active); string source = File.ReadAllText(active);
            var original = Connections.LoadStore(active);
            string encrypted = ProfileTransfer.Encrypt(original, phrase);
            ProfileTransfer.Write(encrypted, export, active, "missing");
            Check(File.ReadAllText(active) == source && File.ReadAllText(export) == encrypted, "export changed the active store");
            Check(!encrypted.Contains(ssh.Host) && !encrypted.Contains(ssh.User) && !encrypted.Contains("synthetic-private-key-material"), "archive exposes metadata or key material");
            var restored = ProfileTransfer.Read(export, phrase);
            Check(restored.Items.Length == 2 && restored.Items[0].Identity == keyPath && restored.Items[0].TunnelEnabled && restored.Items[1].EncryptedPassword == rdp.EncryptedPassword && restored.Folders.Any(p => p.SequenceEqual(new[] { "Empty" })), "portable round trip lost settings, folders or nested secrets");
            Refused<CryptographicException>(() => ProfileTransfer.Read(export, "incorrect portable export phrase"), "wrong archive phrase accepted");
            Refused<InvalidDataException>(() => ProfileTransfer.Read(active, phrase), "plaintext archive accepted");
            Refused<ArgumentException>(() => ProfileTransfer.Encrypt(original, "short"), "short phrase accepted");
            var merged = ProfileTransfer.Merge(new Connections.Store(1, [new Connection { Host = "local.example.invalid", Name = "Local" }], []), restored);
            Check(merged.Items.Length == 3 && ProfileTransfer.Merge(merged, restored).Items.Length == 3, "merge replaced local data or duplicated identical records");
            string revision = ProfileTransfer.DestinationRevision(export, active);
            string updated = ProfileTransfer.Encrypt(original, phrase);
            Check(updated != encrypted, "export reused encryption randomness");
            ProfileTransfer.Write(updated, export, active, revision);
            Refused<IOException>(() => ProfileTransfer.Write(encrypted, export, active, revision), "stale export overwrote a newer archive");
            Check(File.ReadAllText(export) == updated, "rejected overwrite changed archive bytes");
            Refused<IOException>(() => ProfileTransfer.Write(encrypted, export, export, ProfileEncryption.ReadRevision(export)), "active-profile path accepted as export");
            string unrelated = Path.Combine(directory, "unrelated.mgremote"); File.WriteAllText(unrelated, "unrelated data");
            Refused<IOException>(() => ProfileTransfer.Write(encrypted, unrelated, active, "missing"), "unrelated destination overwritten");
            Check(File.ReadAllText(unrelated) == "unrelated data", "unrelated file changed");
            var tampered = JsonNode.Parse(updated)!; byte[] tag = Convert.FromBase64String(tampered["Tag"]!.GetValue<string>()); tag[0] ^= 1; tampered["Tag"] = Convert.ToBase64String(tag);
            File.WriteAllText(export, tampered.ToJsonString());
            Refused<CryptographicException>(() => ProfileTransfer.Read(export, phrase), "altered archive accepted");
            if (OperatingSystem.IsLinux()) {
                Check(File.GetUnixFileMode(directory) == mode, "export changed destination-directory permissions");
                Check((File.GetUnixFileMode(export) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "archive readable by other users");
                string link = Path.Combine(directory, "linked.mgremote"); File.CreateSymbolicLink(link, export);
                Refused<IOException>(() => ProfileTransfer.DestinationRevision(link, active), "export followed a symbolic link");
            }
            Check(File.ReadAllText(active) == source && File.ReadAllText(keyPath) == "synthetic-private-key-material-never-packaged", "source/key files changed");
            Console.WriteLine("PASS: portable encrypted profile round trip, metadata confidentiality, merge/deduplication, wrong phrase/tampering rejection, safe overwrite, unchanged source/keys and destination permissions.");
        } finally { Directory.Delete(directory, recursive: true); }
    }
}
