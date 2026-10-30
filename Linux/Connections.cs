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
    public string JumpHost { get; set; } = "";
    public bool RdpShareEnabled { get; set; }
    public string RdpSharePath { get; set; } = "";
    public bool Clipboard { get; set; }
    public bool MultiMonitor { get; set; }
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "none";
    public int StopBits { get; set; } = 1;
    public string FlowControl { get; set; } = "none";
    public string[] FolderPath { get; set; } = [];
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
        => Import(path, out _);
    public static List<Connection> Import(string path, out int skipped)
    {
        skipped = 0;
        using var reader = XmlReader.Create(path, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "mrng:Connections" && xml.Root?.Name.LocalName != "Connections")
            throw new InvalidDataException("Ce fichier n’est pas un XML mRemoteNG non chiffré.");
        if (bool.TryParse((string?)xml.Root.Attribute("FullFileEncryption"), out bool encrypted) && encrypted)
            throw new InvalidDataException("Fichier entièrement chiffré : exportez une copie XML sans secrets depuis mRemoteNG.");
        var results = new List<Connection>();
        foreach (var node in xml.Descendants().Where(n => n.Name.LocalName == "Node")) {
            if ((string?)node.Attribute("Type") == "Container") continue;
            string protocol = Effective(node, "Protocol") ?? "";
            if (protocol != "SSH2" && protocol != "RDP") { skipped++; continue; }
            string host = ((string?)node.Attribute("Hostname") ?? "").Trim();
            if (host.Length == 0) throw new InvalidDataException("Connexion XML sans hôte : " + ((string?)node.Attribute("Name") ?? "sans nom"));
            int port = int.TryParse(Effective(node, "Port"), out int value) ? value : protocol == "RDP" ? 3389 : 22;
            var connection = new Connection {
                Name = (string?)node.Attribute("Name") ?? host, Host = host,
                Protocol = protocol == "SSH2" ? "SSH" : "RDP", Port = port,
                User = Effective(node, "Username") ?? "", Domain = Effective(node, "Domain") ?? "",
                FolderPath = node.Ancestors().Reverse().Where(a => (string?)a.Attribute("Type") == "Container").Select(a => (string?)a.Attribute("Name") ?? "Dossier").ToArray()
            };
            Validate(connection);
            results.Add(connection);
        }
        if (results.Count == 0) throw new InvalidDataException("Aucune connexion SSH2/RDP explicite importable. Les fichiers entièrement chiffrés et les valeurs héritées doivent être exportés à plat sans secrets depuis Windows.");
        return results;
    }
    public static void Validate(Connection c)
    {
        if (c == null || c.Host == null || c.User == null || c.Domain == null || c.Identity == null || c.JumpHost == null || c.RdpSharePath == null) throw new ArgumentException("Connexion incomplète : hôte, utilisateur, domaine ou clé manquant dans les données.");
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 256 || c.Name.Any(char.IsControl)) throw new ArgumentException("Nom invalide.");
        if (c.FolderPath == null || c.FolderPath.Length > 128 || c.FolderPath.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 256 || p.Any(char.IsControl))) throw new ArgumentException("Dossier de connexion invalide.");
        if (c.Protocol == "Serial") {
            if (!Regex.IsMatch(c.Host, @"\A/dev/(?:tty(?:USB|ACM|S)[0-9]+|serial/by-id/[A-Za-z0-9_.:+-]+|pts/[0-9]+)\z")) throw new ArgumentException("Port série Linux invalide (exemple : /dev/ttyUSB0).");
            if (c.BaudRate < 50 || c.BaudRate > 4000000 || c.DataBits < 5 || c.DataBits > 8 || c.StopBits is not (1 or 2) ||
                c.Parity is not ("none" or "even" or "odd") || c.FlowControl is not ("none" or "hardware" or "software")) throw new ArgumentException("Paramètres série invalides.");
            return;
        }
        if (!Regex.IsMatch(c.Host, @"\A[A-Za-z0-9_\[\]][A-Za-z0-9_.:\[\]-]{0,252}\z")) throw new ArgumentException("Hôte/IP ou alias SSH invalide.");
        if (c.Protocol != "SSH" && c.Protocol != "RDP") throw new ArgumentException("Protocole non pris en charge.");
        if (c.Port is < 1 or > 65535) throw new ArgumentException("Port invalide.");
        foreach (string text in new[] { c.User, c.Domain })
            if (text.Length > 256 || text.Any(char.IsControl) || text.StartsWith('-')) throw new ArgumentException("Utilisateur ou domaine invalide.");
        if (c.Identity.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Chemin de clé invalide.");
        if (c.Protocol == "SSH" && c.JumpHost.Length > 0 && (!Regex.IsMatch(c.JumpHost, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,252}\z") ||
            c.JumpHost.Equals("none", StringComparison.OrdinalIgnoreCase) || c.JumpHost.Equals(c.Host, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Bastion invalide : choisissez un alias OpenSSH distinct de la destination, sans options de commande.");
        if (c.Protocol == "RDP" && c.RdpShareEnabled && (string.IsNullOrWhiteSpace(c.RdpSharePath) || !Path.IsPathFullyQualified(c.RdpSharePath) ||
            c.RdpSharePath.Any(char.IsControl) || c.RdpSharePath.Contains(','))) throw new ArgumentException("Choisissez un dossier RDP local absolu, sans virgule ni caractères de contrôle.");
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
        if (c.Protocol == "Serial") {
            if (sftp) throw new ArgumentException("SFTP nécessite une connexion SSH.");
            return ["--baud", c.BaudRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "--databits", c.DataBits.ToString(),
                "--parity", c.Parity, "--stopbits", c.StopBits.ToString(), "--flow", c.FlowControl, c.Host];
        }
        if (c.Protocol == "RDP") {
            if (sftp) throw new ArgumentException("SFTP nécessite SSH.");
            string host = c.Host.Contains(':') && !c.Host.StartsWith('[') ? "[" + c.Host + "]" : c.Host;
            var rdp = new List<string> { "/v:" + host + ":" + c.Port, "/sec:nla", "/dynamic-resolution", "/from-stdin:force" };
            if (c.User.Length > 0) rdp.Add("/u:" + c.User);
            if (c.Domain.Length > 0) rdp.Add("/d:" + c.Domain);
            rdp.Add(c.Clipboard ? "+clipboard" : "-clipboard");
            if (c.MultiMonitor) rdp.Add("/multimon");
            if (c.RdpShareEnabled) rdp.Add("/drive:MGRemote," + Path.GetFullPath(c.RdpSharePath));
            return rdp; // Certificate verification is retained; no password or redirected drives.
        }
        var ssh = new List<string> { "-o", "StrictHostKeyChecking=ask", "-o", "ForwardAgent=no", "-o", "PermitLocalCommand=no" };
        if (!sftp) ssh.Add("-tt");
        ssh.Add(sftp ? "-P" : "-p"); ssh.Add(c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (c.Identity.Length > 0) { ssh.AddRange(["-i", c.Identity, "-o", "IdentitiesOnly=yes"]); }
        if (c.JumpHost.Length > 0) ssh.AddRange(["-J", c.JumpHost]);
        if (c.User.Length > 0) ssh.AddRange(["-o", "User=" + c.User]);
        string destination = c.Host;
        if (sftp && destination.Contains(':') && !destination.StartsWith('[')) destination = "[" + destination + "]";
        ssh.Add(destination); return ssh;
    }
    internal static string Find(params string[] names)
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
        ValidateIdentity(c);
        string executable = Executable(c, sftp);
        string terminal = Find("gnome-terminal", "konsole", "xfce4-terminal", "xterm");
        var start = new ProcessStartInfo(terminal) { UseShellExecute = false };
        string name = Path.GetFileName(terminal);
        start.ArgumentList.Add(name == "gnome-terminal" ? "--" : name == "xfce4-terminal" ? "--execute" : "-e");
        start.ArgumentList.Add(executable);
        foreach (string argument in Arguments(c, sftp)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Impossible de lancer le terminal Linux.");
    }
    internal static void ValidateIdentity(Connection c)
    {
        Connections.Validate(c);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Les sessions intégrées nécessitent Linux X11/XWayland.");
        if (c.Protocol == "RDP" && c.RdpShareEnabled && (!Directory.Exists(c.RdpSharePath) || new DirectoryInfo(c.RdpSharePath).LinkTarget != null))
            throw new IOException("Le dossier à partager en RDP doit exister et ne pas être un lien symbolique.");
        if (c.Identity.Length > 0 && c.Protocol == "SSH") {
            if (!Path.IsPathFullyQualified(c.Identity) || !File.Exists(c.Identity) || new FileInfo(c.Identity).LinkTarget != null) throw new IOException("Choisissez une clé privée locale Linux existante.");
            if ((File.GetUnixFileMode(c.Identity) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new IOException("La clé privée doit être accessible uniquement à son propriétaire (chmod 600).");
            using var key = File.OpenText(c.Identity);
            var header = new char[100];
            string? marker = new string(header, 0, key.Read(header, 0, header.Length)).Split('\n')[0].TrimEnd('\r');
            if (marker == null || !marker.StartsWith("-----BEGIN ") || !marker.EndsWith("PRIVATE KEY-----")) throw new IOException("Format OpenSSH/PEM requis ; les clés PuTTY PPK doivent être converties.");
        }
    }
    private static string Executable(Connection c, bool sftp) => c.Protocol == "Serial" ? Find("picocom") : c.Protocol == "RDP" ? Find("xfreerdp3", "xfreerdp") : Find(sftp ? "sftp" : "ssh");
    public static ProcessStartInfo Embedded(Connection c, bool sftp, uint parent)
    {
        ValidateIdentity(c);
        var args = Arguments(c, sftp);
        if (c.Protocol == "RDP") {
            if (c.MultiMonitor) throw new NotSupportedException("Le multi-écran RDP utilise encore le mode externe sous Linux. Utilisez Sessions → Ouvrir en fenêtre externe.");
            args.Add("/parent-window:" + parent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return Terminal(parent, Executable(c, sftp), args);
    }
    internal static ProcessStartInfo Terminal(uint parent, string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Find("xterm")) { UseShellExecute = false };
        start.ArgumentList.Add("-hold"); // Keep connection errors/output readable after the client exits.
        start.ArgumentList.Add("-xrm"); start.ArgumentList.Add("XTerm*allowWindowOps: false");
        foreach (string arg in new[] { "-into", parent.ToString(System.Globalization.CultureInfo.InvariantCulture), "-bg", "#181a1f", "-fg", "#e6e6e6", "-fa", "Monospace", "-fs", "11", "-sl", "5000", "-xrm", "XTerm*VT100.translations: #override\\n Ctrl Shift <Key>C: copy-selection(CLIPBOARD)\\n Ctrl Shift <Key>V: insert-selection(CLIPBOARD)", "-e", executable }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }
}
