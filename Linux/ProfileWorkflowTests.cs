namespace MGRemote.Linux;

internal static class ProfileWorkflowTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "mgremote-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Profile workflow check failed: " + checks); checks++; }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException or System.Text.Json.JsonException) { checks++; return; } throw new Exception("Unsafe profile action succeeded."); }
        try {
            var connection = new Connection { Name = "Password authentication", Host = "fixture.example.invalid" };
            SessionLauncher.ValidateIdentity(connection);
            foreach (bool sftp in new[] { false, true }) {
                var args = SessionLauncher.Arguments(connection, sftp);
                Check(!args.Contains("-i") && !args.Contains("BatchMode=yes") && !args.Contains("PasswordAuthentication=no"));
            }
            string original = Path.Combine(root, "connections.json"), created = ProfileRecovery.NewPath(root, "../independent profile");
            Check(created.StartsWith(Path.Combine(root, "profiles") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            ProfileRecovery.Create(created); Check(Connections.LoadStore(created).Items.Length == 0);
            string empty = File.ReadAllText(created); Reject(() => ProfileRecovery.Create(created)); Check(File.ReadAllText(created) == empty);
            var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(root) : default;
            Connections.Save([connection], dataPath: original); Check(Connections.LoadStore(original).Items.Single().Identity == "");
            if (OperatingSystem.IsLinux()) Check(File.GetUnixFileMode(root) == mode);
            using var key = ProfileEncryption.CreateKey("synthetic recovery fixture phrase");
            Connections.Save([connection], dataPath: original, profileKey: key);
            string encrypted = File.ReadAllText(original);
            string restored = ProfileRecovery.NewPath(root, "restored"); ProfileRecovery.Restore(original, restored);
            Check(File.ReadAllText(restored) == encrypted && Connections.LoadStore(restored, key).Items.Single().Host == connection.Host);
            Reject(() => ProfileRecovery.Restore(original, restored)); Check(File.ReadAllText(restored) == encrypted);
            Reject(() => ProfileRecovery.Archive(original, "stale")); Check(File.ReadAllText(original) == encrypted);
            string archive = ProfileRecovery.Archive(original, ProfileEncryption.ReadRevision(original));
            Check(!File.Exists(original) && File.ReadAllText(archive) == encrypted);
            ProfileRecovery.Restore(archive, original); Check(Connections.LoadStore(original, key).Items.Length == 1);
            string[] listed = ProfileRecovery.List(original); Check(listed.Contains(original) && listed.Contains(restored) && listed.Contains(created));
            Check(ProfileRecovery.LastSelected(original) == original);
            ProfileRecovery.Remember(original, restored); Check(ProfileRecovery.LastSelected(original) == restored);
            string choiceFile = Path.Combine(root, "last-profile.json");
            if (OperatingSystem.IsLinux()) Check((File.GetUnixFileMode(choiceFile) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0);
            File.WriteAllText(choiceFile, "invalid settings"); Check(ProfileRecovery.LastSelected(original) == original);
            string invalid = Path.Combine(root, "invalid.json"); File.WriteAllText(invalid, "corrupt profile");
            string target = ProfileRecovery.NewPath(root, "invalid restore"); Reject(() => ProfileRecovery.Restore(invalid, target)); Check(!File.Exists(target));
            if (OperatingSystem.IsLinux()) {
                string link = Path.Combine(root, "linked-profile"); File.CreateSymbolicLink(link, original);
                Reject(() => ProfileRecovery.Restore(link, target));
                Reject(() => ProfileRecovery.Restore(archive, link));
                Reject(() => ProfileRecovery.Archive(link, ProfileEncryption.Revision(encrypted)));
                File.Delete(choiceFile); File.CreateSymbolicLink(choiceFile, original);
                Reject(() => ProfileRecovery.Remember(original, restored)); Check(ProfileRecovery.LastSelected(original) == original);
                Check(File.ReadAllText(original) == encrypted);
                Check((File.GetUnixFileMode(restored) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0);
            }
            Console.WriteLine($"PASS: {checks} profile workflow checks: optional SSH keys, independent creation, encrypted restore/archive, stale-action and overwrite rejection, parent permissions and linked-profile guards.");
        } finally { Directory.Delete(root, recursive: true); }
    }
}
