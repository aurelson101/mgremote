// MGRemote Linux prototype. GPL-2.0; see ../COPYING.txt.
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MGRemote.Linux;

public sealed record Connection
{
    public string Name { get; set; } = "Nouvelle connexion";
    public string Host { get; set; } = "";
    public string Protocol { get; set; } = "SSH";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Identity { get; set; } = "";
    public bool Clipboard { get; set; }
    public bool MultiMonitor { get; set; }
    public override string ToString() => $"{Name}  ·  {Protocol}";
}

public static class Connections
{
    private static string? Effective(XElement node, string name)
    {
        int depth = 0;
        while (bool.TryParse((string?)node.Attribute("Inherit" + name), out bool inherited) && inherited) {
            if (++depth > 128 || node.Parent == null) throw new InvalidDataException("Héritage XML invalide ou trop profond.");
            node = node.Parent;
        }
        return (string?)node.Attribute(name);
    }
    public static List<Connection> Import(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "mrng:Connections" && xml.Root?.Name.LocalName != "Connections")
            throw new InvalidDataException("Ce fichier n’est pas un XML mRemoteNG non chiffré.");
        var results = new List<Connection>();
        foreach (var node in xml.Descendants().Where(n => n.Name.LocalName == "Node")) {
            if ((string?)node.Attribute("Type") == "Container") continue;
            string protocol = Effective(node, "Protocol") ?? "";
            if (protocol != "SSH2" && protocol != "RDP") continue;
            string host = ((string?)node.Attribute("Hostname") ?? "").Trim();
            if (host.Length == 0) continue;
            int port = int.TryParse(Effective(node, "Port"), out int value) ? value : protocol == "RDP" ? 3389 : 22;
            var connection = new Connection {
                Name = (string?)node.Attribute("Name") ?? host, Host = host,
                Protocol = protocol == "SSH2" ? "SSH" : "RDP", Port = port,
                User = Effective(node, "Username") ?? "", Domain = Effective(node, "Domain") ?? ""
            };
            Validate(connection);
            results.Add(connection);
        }
        if (results.Count == 0) throw new InvalidDataException("Aucune connexion SSH2/RDP explicite importable. Les fichiers entièrement chiffrés et les valeurs héritées doivent être exportés à plat sans secrets depuis Windows.");
        return results;
    }
    public static void Validate(Connection c)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 256 || c.Name.Any(char.IsControl)) throw new ArgumentException("Nom invalide.");
        if (!Regex.IsMatch(c.Host, @"\A[A-Za-z0-9_\[\]][A-Za-z0-9_.:\[\]-]{0,252}\z")) throw new ArgumentException("Hôte/IP ou alias SSH invalide.");
        if (c.Protocol != "SSH" && c.Protocol != "RDP") throw new ArgumentException("Protocole non pris en charge.");
        if (c.Port is < 1 or > 65535) throw new ArgumentException("Port invalide.");
        foreach (string text in new[] { c.User, c.Domain })
            if (text.Length > 256 || text.Any(char.IsControl) || text.StartsWith('-')) throw new ArgumentException("Utilisateur ou domaine invalide.");
        if (c.Identity.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Chemin de clé invalide.");
    }
    public static string DataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MGRemoteLinux", "connections.json");
    public static List<Connection> Load()
    {
        if (!File.Exists(DataPath)) return [];
        if (new FileInfo(DataPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("Fichier de connexions trop volumineux.");
        var items = JsonSerializer.Deserialize<List<Connection>>(File.ReadAllText(DataPath)) ?? [];
        foreach (var item in items) Validate(item);
        return items;
    }
    public static void Save(IEnumerable<Connection> connections)
    {
        var items = connections.ToArray(); foreach (var item in items) Validate(item);
        string folder = Path.GetDirectoryName(DataPath)!;
        if (new DirectoryInfo(folder).LinkTarget != null || new FileInfo(DataPath).LinkTarget != null) throw new IOException("Stockage redirigé par lien refusé.");
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(folder, ".connections-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, items, new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, DataPath, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class SessionLauncher
{
    public static List<string> Arguments(Connection c, bool sftp = false)
    {
        Connections.Validate(c);
        if (c.Protocol == "RDP") {
            if (sftp) throw new ArgumentException("SFTP nécessite SSH.");
            string host = c.Host.Contains(':') && !c.Host.StartsWith('[') ? "[" + c.Host + "]" : c.Host;
            var rdp = new List<string> { "/v:" + host + ":" + c.Port, "/sec:nla", "/dynamic-resolution", "/from-stdin:force" };
            if (c.User.Length > 0) rdp.Add("/u:" + c.User);
            if (c.Domain.Length > 0) rdp.Add("/d:" + c.Domain);
            rdp.Add(c.Clipboard ? "+clipboard" : "-clipboard");
            if (c.MultiMonitor) rdp.Add("/multimon");
            return rdp; // Certificate verification is retained; no password or redirected drives.
        }
        var ssh = new List<string> { "-o", "StrictHostKeyChecking=ask", "-o", "ForwardAgent=no", "-o", "PermitLocalCommand=no" };
        if (!sftp) ssh.Add("-tt");
        ssh.Add(sftp ? "-P" : "-p"); ssh.Add(c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (c.Identity.Length > 0) { ssh.AddRange(["-i", c.Identity, "-o", "IdentitiesOnly=yes"]); }
        if (c.User.Length > 0) ssh.AddRange(["-o", "User=" + c.User]);
        string destination = c.Host;
        if (sftp && destination.Contains(':') && !destination.StartsWith('[')) destination = "[" + destination + "]";
        ssh.Add(destination); return ssh;
    }
    private static string Find(params string[] names)
    {
        foreach (string name in names) foreach (string prefix in new[] { "/usr/bin", "/bin" }) {
            string file = Path.Combine(prefix, name);
            if (!File.Exists(file)) continue;
            var resolved = new FileInfo(file).ResolveLinkTarget(true)?.FullName ?? file;
            if ((File.GetUnixFileMode(resolved) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) continue;
            return file;
        }
        throw new FileNotFoundException("Composant Linux manquant : " + string.Join(" / ", names));
    }
    public static void Launch(Connection c, bool sftp = false)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Les sessions de cette version nécessitent Linux.");
        if (c.Identity.Length > 0 && c.Protocol == "SSH") {
            if (!Path.IsPathFullyQualified(c.Identity) || !File.Exists(c.Identity) || new FileInfo(c.Identity).LinkTarget != null) throw new IOException("Choisissez une clé privée locale Linux existante.");
            if ((File.GetUnixFileMode(c.Identity) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("La clé privée doit être accessible uniquement à son propriétaire (chmod 600).");
            using var key = File.OpenText(c.Identity);
            var header = new char[100];
            string? marker = new string(header, 0, key.Read(header, 0, header.Length)).Split('\n')[0].TrimEnd('\r');
            if (marker == null || !marker.StartsWith("-----BEGIN ") || !marker.EndsWith("PRIVATE KEY-----")) throw new IOException("Format OpenSSH/PEM requis ; les clés PuTTY PPK doivent être converties.");
        }
        string executable = c.Protocol == "RDP" ? Find("xfreerdp3", "xfreerdp") : Find(sftp ? "sftp" : "ssh");
        string terminal = Find("gnome-terminal", "konsole", "xfce4-terminal", "xterm");
        var start = new ProcessStartInfo(terminal) { UseShellExecute = false };
        string name = Path.GetFileName(terminal);
        start.ArgumentList.Add(name == "gnome-terminal" ? "--" : name == "xfce4-terminal" ? "--execute" : "-e");
        start.ArgumentList.Add(executable);
        foreach (string argument in Arguments(c, sftp)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Impossible de lancer le terminal Linux.");
    }
}
