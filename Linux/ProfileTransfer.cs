using System.Text;
using System.Text.Json;

namespace MGRemote.Linux;

internal static class ProfileTransfer
{
    internal static string DestinationRevision(string path, string activePath)
    {
        string file = Path.GetFullPath(path);
        if (!file.EndsWith(".mgremote", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choisissez un fichier .mgremote pour l’export chiffré.");
        if (string.Equals(file, Path.GetFullPath(activePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("L’export doit être différent du profil actif.");
        ProfileEncryption.RejectLinkedPath(file);
        string content;
        try { content = ProfileEncryption.ReadProfileText(file); } catch (FileNotFoundException) { return "missing"; }
        try {
            using var document = JsonDocument.Parse(content);
            if (!ProfileEncryption.IsEnvelope(document.RootElement) || document.RootElement.GetProperty("Format").ValueKind != JsonValueKind.String || document.RootElement.GetProperty("Format").GetString() != "MGRemote-profile-v1") throw new JsonException();
        } catch (JsonException) { throw new IOException("Un fichier existant qui n’est pas un profil MGRemote chiffré ne sera pas remplacé."); }
        return ProfileEncryption.Revision(content);
    }
    internal static string Encrypt(Connections.Store store, string password)
    {
        var validated = Connections.ParseStore(JsonSerializer.Serialize(store));
        using var key = ProfileEncryption.CreateKey(password);
        return ProfileEncryption.Encode(JsonSerializer.Serialize(validated), key);
    }
    internal static void Write(string content, string path, string activePath, string expectedRevision)
    {
        if (Encoding.UTF8.GetByteCount(content) > ProfileEncryption.MaxFile) throw new InvalidDataException("Export trop volumineux.");
        using (var document = JsonDocument.Parse(content)) {
            if (!ProfileEncryption.IsEnvelope(document.RootElement)) throw new ArgumentException("Seul un profil chiffré peut être exporté.");
        }
        string file = Path.GetFullPath(path);
        string current = DestinationRevision(file, activePath);
        if (current != expectedRevision) throw new IOException("Le fichier d’export a changé. Choisissez-le à nouveau avant de remplacer sa version actuelle.");
        string folder = Path.GetDirectoryName(file)!;
        string temporary = Path.Combine(folder, ".mgremote-export-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)) writer.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (DestinationRevision(file, activePath) != current) throw new IOException("Le fichier d’export a changé pendant l’opération ; sa version existante est conservée.");
            File.Move(temporary, file, overwrite: current != "missing");
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static Connections.Store Read(string path, string password)
    {
        using var document = JsonDocument.Parse(ProfileEncryption.ReadProfileText(path));
        if (!ProfileEncryption.IsEnvelope(document.RootElement)) throw new InvalidDataException("Ce fichier n’est pas un profil MGRemote chiffré.");
        using var key = ProfileEncryption.Unlock(document.RootElement, password);
        return Connections.ParseStore(ProfileEncryption.Decode(document.RootElement, key));
    }
    internal static Connections.Store Merge(Connections.Store current, Connections.Store incoming)
    {
        var merged = new Connections.Store(1,
            current.Items.Concat(incoming.Items).DistinctBy(c => JsonSerializer.Serialize(c)).ToArray(),
            current.Folders.Concat(incoming.Folders).DistinctBy(p => JsonSerializer.Serialize(p)).ToArray());
        return Connections.ParseStore(JsonSerializer.Serialize(merged));
    }
}
