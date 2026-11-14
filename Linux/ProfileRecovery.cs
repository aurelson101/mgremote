using System.Text.Json;
using System.Text.RegularExpressions;

namespace MGRemote.Linux;

internal static class ProfileRecovery
{
    internal static string LastSelected(string defaultPath)
    {
        try {
            using var stream = ProfileEncryption.OpenLocalRead(Path.Combine(Path.GetDirectoryName(defaultPath)!, "last-profile.json"));
            string? selected = JsonSerializer.Deserialize<string>(ProfileEncryption.ReadBoundedText(stream, 16384));
            if (selected != null && Path.IsPathFullyQualified(selected) && File.Exists(selected)) return selected;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        return defaultPath;
    }
    internal static void Remember(string defaultPath, string selected)
    {
        string folder = Path.GetDirectoryName(defaultPath)!;
        string file = Path.Combine(folder, "last-profile.json");
        ProfileEncryption.RejectLinkedPath(file);
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".profile-choice-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) {
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true)) writer.Write(JsonSerializer.Serialize(selected));
                stream.Flush(flushToDisk: true);
            }
            ProfileEncryption.RejectLinkedPath(file); File.Move(temporary, file, true);
        } finally { File.Delete(temporary); }
    }
    internal static string NewPath(string home, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new ArgumentException("Nom du profil requis (80 caractères maximum).");
        string label = Regex.Replace(name.Trim(), @"[^A-Za-z0-9_-]+", "-").Trim('-');
        if (label.Length == 0) label = "profil";
        return Path.Combine(home, "profiles", label + "-" + Guid.NewGuid().ToString("N"), "connections.json");
    }
    internal static string[] List(string defaultPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(defaultPath)!, "profiles");
        var files = new List<string> { defaultPath };
        try { if (Directory.Exists(root) && new DirectoryInfo(root).LinkTarget == null)
            foreach (var folder in new DirectoryInfo(root).EnumerateDirectories().Take(100))
                if (folder.LinkTarget == null && File.Exists(Path.Combine(folder.FullName, "connections.json"))) files.Add(Path.Combine(folder.FullName, "connections.json"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Manual selection remains available. */ }
        return files.ToArray();
    }
    internal static string Read(string file)
    {
        string content = ProfileEncryption.ReadProfileText(file);
        using var json = JsonDocument.Parse(content);
        if (!ProfileEncryption.IsEnvelope(json.RootElement)) Connections.ParseStore(content);
        return content;
    }
    internal static void Create(string file) => Connections.Save([], [], file, expectedRevision: "missing");
    internal static void Restore(string source, string destination)
    {
        string content = Read(source);
        ProfileEncryption.RejectLinkedPath(destination);
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        if (OperatingSystem.IsLinux()) Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, ".restore-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) {
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true)) writer.Write(content);
                stream.Flush(flushToDisk: true);
            }
            ProfileEncryption.RejectLinkedPath(destination);
            File.Move(temporary, destination); // Never overwrite another profile, including a concurrent creation.
        } finally { File.Delete(temporary); }
    }
    internal static string Archive(string file, string expectedRevision)
    {
        ProfileEncryption.RejectLinkedPath(file);
        string revision = ProfileEncryption.ReadRevision(file);
        if (revision == "missing" || revision != expectedRevision) throw new IOException("Le profil a changé ou a disparu. Sélectionnez-le à nouveau avant suppression.");
        string backup = file + ".deleted-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Move(file, backup);
        return backup;
    }
}
