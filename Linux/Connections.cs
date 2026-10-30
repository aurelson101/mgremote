// MGRemote Linux prototype. GPL-2.0; see ../COPYING.txt.
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MGRemote.Linux;

public sealed record Connection
{
    public bool IsDraft { get; set; }
    public string Name { get; set; } = "Nouvelle connexion";
    public string EncryptedPassword { get; set; } = "";
    public string GatewayHost { get; set; } = "";
    public string GatewayMode { get; set; } = "direct";
    public string Description { get; set; } = "";
    public int ColorDepth { get; set; } = 32;
    public int AudioMode { get; set; } = 2;
    public bool ConsoleSession { get; set; }
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
    public static List<Connection> Import(string path, out int skipped, string? sourcePassword = null, string? localPassword = null)
        => Import(path, out skipped, out _, sourcePassword, localPassword);
    public static List<Connection> Import(string path, out int skipped, out string[][] folders, string? sourcePassword = null, string? localPassword = null)
    {
        skipped = 0; folders = [];
        using var reader = XmlReader.Create(path, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var xml = XDocument.Load(reader);
        if (xml.Root?.Name.LocalName != "mrng:Connections" && xml.Root?.Name.LocalName != "Connections")
            throw new InvalidDataException("Ce fichier n’est pas un XML mRemoteNG non chiffré.");
        string sourceKey = sourcePassword ?? "mR3m";
        if (localPassword != null && xml.Root.Attribute("Protected") is { } protection) {
            string marker = XmlSecrets.DecryptSource(protection.Value, sourceKey, xml.Root);
            if (marker is not ("ThisIsProtected" or "ThisIsNotProtected")) throw new System.Security.Cryptography.CryptographicException("Authentification XML invalide.");
        }
        if (bool.TryParse((string?)xml.Root.Attribute("FullFileEncryption"), out bool encrypted) && encrypted) {
            string content = XmlSecrets.DecryptSource(xml.Root.Value, sourceKey, xml.Root);
            string wrapper = new XElement("Connections", xml.Root.Attributes().Where(a => a.IsNamespaceDeclaration)).ToString(SaveOptions.DisableFormatting);
            using var innerReader = XmlReader.Create(new StringReader(wrapper[..^2] + ">" + content + "</Connections>"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var inner = XDocument.Load(innerReader);
            xml.Root.ReplaceNodes(inner.Root!.Nodes());
        }
        folders = xml.Descendants().Where(n => n.Name.LocalName == "Node" && (string?)n.Attribute("Type") == "Container")
            .Select(n => n.AncestorsAndSelf().Reverse().Where(a => (string?)a.Attribute("Type") == "Container").Select(a => (string?)a.Attribute("Name") ?? "Dossier").ToArray())
            .DistinctBy(p => JsonSerializer.Serialize(p)).ToArray();
        foreach (var folderPath in folders) ValidateFolderPath(folderPath);
        int secretCount = 0; long sourceWork = 0;
        var results = new List<Connection>();
        foreach (var node in xml.Descendants().Where(n => n.Name.LocalName == "Node")) {
            if ((string?)node.Attribute("Type") == "Container") continue;
            string protocol = Effective(node, "Protocol") ?? "";
            if (protocol != "SSH2" && protocol != "RDP") { skipped++; continue; }
            string host = (Effective(node, "Hostname") ?? "").Trim();
            if (host.Length == 0) throw new InvalidDataException("Connexion XML sans hôte : " + ((string?)node.Attribute("Name") ?? "sans nom"));
            string? portText = Effective(node, "Port");
            if (!string.IsNullOrEmpty(portText) && !int.TryParse(portText, out _)) throw new InvalidDataException("Port XML invalide : " + portText);
            int port = int.TryParse(portText, out int value) ? value : protocol == "RDP" ? 3389 : 22;
            string? colors = Effective(node, "Colors"), sound = Effective(node, "RedirectSound");
            int depth = colors switch { null or "" or "Colors32Bit" => 32, "Colors256" => 8, "Colors15Bit" => 15, "Colors16Bit" => 16, "Colors24Bit" => 24, _ => throw new InvalidDataException("Couleurs RDP XML non reconnues : " + colors) };
            int audio = sound switch { null or "" or "DoNotPlay" => 2, "BringToThisComputer" => 0, "LeaveAtRemoteComputer" => 1, _ => throw new InvalidDataException("Son RDP XML non reconnu : " + sound) };
            var connection = new Connection {
                Name = (string?)node.Attribute("Name") ?? host, Host = host,
                Protocol = protocol == "SSH2" ? "SSH" : "RDP", Port = port,
                User = Effective(node, "Username") ?? "", Domain = Effective(node, "Domain") ?? "",
                Description = Effective(node, "Descr") ?? "", ColorDepth = depth, AudioMode = audio,
                Clipboard = ReadBoolean(node, "RedirectClipboard"), ConsoleSession = ReadBoolean(node, "UseConsoleSession"),
                FolderPath = node.Ancestors().Reverse().Where(a => (string?)a.Attribute("Type") == "Container").Select(a => (string?)a.Attribute("Name") ?? "Dossier").ToArray()
            };
            if (protocol == "RDP") {
                string usage = Effective(node, "RDGatewayUsageMethod") ?? "Never";
                if (usage != "Never") {
                    connection.GatewayMode = usage switch { "Always" => "direct", "Detect" => "detect", _ => throw new InvalidDataException("Mode de passerelle XML non reconnu.") };
                    connection.GatewayHost = Effective(node, "RDGatewayHostname") ?? "";
                    if (connection.GatewayHost.Length == 0) throw new InvalidDataException("Passerelle XML sans hôte.");
                }
            }
            if (localPassword != null && Effective(node, "Password") is { Length: > 0 } cipherText) {
                int sourceIterations = int.TryParse((string?)xml.Root.Attribute("KdfIterations"), out int work) ? work : 1000;
                sourceWork += sourceIterations;
                if (++secretCount > 64 || sourceWork > 10000000) throw new InvalidDataException("Import trop coûteux : limitez l’export à 64 secrets par opération (budget KDF dépassé).");
                string plainPassword = XmlSecrets.DecryptSource(cipherText, sourceKey, xml.Root);
                if (System.Text.Encoding.UTF8.GetByteCount(plainPassword) > 4096) throw new InvalidDataException("Mot de passe XML trop volumineux.");
                connection.EncryptedPassword = XmlSecrets.Protect(plainPassword, localPassword);
            }
            Validate(connection);
            results.Add(connection);
        }
        if (results.Count == 0 && folders.Length == 0) throw new InvalidDataException("Aucune connexion SSH2/RDP explicite importable. Les fichiers entièrement chiffrés et les valeurs héritées doivent être exportés à plat sans secrets depuis Windows.");
        return results;
    }
    private static bool ReadBoolean(XElement node, string name)
    {
        string? text = Effective(node, name);
        if (string.IsNullOrEmpty(text)) return false;
        return bool.TryParse(text, out bool value) ? value : throw new InvalidDataException("Valeur XML booléenne invalide : " + name);
    }
    public static void Validate(Connection c, bool allowDraft = false)
    {
        if (c == null || c.Host == null || c.User == null || c.Domain == null || c.Identity == null || c.JumpHost == null || c.RdpSharePath == null) throw new ArgumentException("Connexion incomplète : hôte, utilisateur, domaine ou clé manquant dans les données.");
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 256 || c.Name.Any(char.IsControl)) throw new ArgumentException("Nom invalide.");
        if (c.EncryptedPassword == null || c.EncryptedPassword.Length > 16384 || (c.EncryptedPassword.Length > 0 && !c.EncryptedPassword.StartsWith("v1:"))) throw new ArgumentException("Secret local non valide.");
        if (c.GatewayHost == null || c.GatewayMode is not ("direct" or "detect")) throw new ArgumentException("Passerelle RDP non valide.");
        if (c.GatewayHost.Length > 0 && (!Regex.IsMatch(c.GatewayHost, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,252}(?::[0-9]{1,5})?\z") || (c.GatewayHost.Contains(':') && (!int.TryParse(c.GatewayHost.Split(':')[1], out int gatewayPort) || gatewayPort is < 1 or > 65535)))) throw new ArgumentException("Hôte ou port de passerelle RDP invalide.");
        if (c.Description == null || c.Description.Length > 4096 || c.Description.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))) throw new ArgumentException("Description invalide.");
        if (c.ColorDepth is not (8 or 15 or 16 or 24 or 32) || c.AudioMode is < 0 or > 2) throw new ArgumentException("Couleurs ou son RDP invalides.");
        ValidateFolderPath(c.FolderPath);
        if (c.Protocol == "Serial") {
            if (!Regex.IsMatch(c.Host, @"\A/dev/(?:tty(?:USB|ACM|S)[0-9]+|serial/by-id/[A-Za-z0-9_.:+-]+|pts/[0-9]+)\z")) throw new ArgumentException("Port série Linux invalide (exemple : /dev/ttyUSB0).");
            if (c.BaudRate < 50 || c.BaudRate > 4000000 || c.DataBits < 5 || c.DataBits > 8 || c.StopBits is not (1 or 2) ||
                c.Parity is not ("none" or "even" or "odd") || c.FlowControl is not ("none" or "hardware" or "software")) throw new ArgumentException("Paramètres série invalides.");
            return;
        }
        if (!(allowDraft && c.IsDraft && c.Host.Length == 0) && !Regex.IsMatch(c.Host, @"\A[A-Za-z0-9_\[\]][A-Za-z0-9_.:\[\]-]{0,252}\z")) throw new ArgumentException("Renseignez un hôte/IP ou alias SSH valide avant connexion.");
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
    internal static void ValidateFolderPath(string[] path)
    {
        if (path == null || path.Length > 128 || path.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 256 || p.Any(char.IsControl))) throw new ArgumentException("Dossier de connexion invalide.");
    }
    public sealed record Store(int Version, Connection[] Items, string[][] Folders);
    public static List<Connection> Load() => LoadStore().Items.ToList();
    public static Store LoadStore(string? dataPath = null)
    {
        string file = dataPath ?? DataPath;
        if (!File.Exists(file)) return new Store(1, [], []);
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Fichier de connexions trop volumineux.");
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Store store = json.RootElement.ValueKind == JsonValueKind.Array
            ? new Store(1, json.RootElement.Deserialize<Connection[]>()!, [])
            : json.RootElement.Deserialize<Store>() ?? throw new InvalidDataException("Stockage invalide.");
        if (store.Version != 1 || store.Items == null || store.Folders == null) throw new InvalidDataException("Version de stockage non prise en charge ou données incomplètes.");
        foreach (var item in store.Items) Validate(item, allowDraft: true);
        foreach (var path in store.Folders) { ValidateFolderPath(path); if (path.Length == 0) throw new InvalidDataException("Dossier racine explicite invalide."); }
        return store with { Folders = store.Folders.Concat(store.Items.Select(c => c.FolderPath))
            .SelectMany(path => Enumerable.Range(1, path.Length).Select(length => path.Take(length).ToArray()))
            .DistinctBy(path => JsonSerializer.Serialize(path)).ToArray() };
    }
    public static void Save(IEnumerable<Connection> connections, IEnumerable<string[]>? folders = null, string? dataPath = null)
    {
        var items = connections.ToArray(); foreach (var item in items) Validate(item, allowDraft: true);
        var paths = (folders ?? []).ToArray(); foreach (var path in paths) { ValidateFolderPath(path); if (path.Length == 0) throw new ArgumentException("Dossier racine explicite invalide."); }
        string file = dataPath ?? DataPath;
        string folder = Path.GetDirectoryName(file)!;
        if (new DirectoryInfo(folder).LinkTarget != null || new FileInfo(file).LinkTarget != null) throw new IOException("Stockage redirigé par lien refusé.");
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(folder, ".connections-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, new Store(1, items, paths), new JsonSerializerOptions { WriteIndented = true });
            File.Move(temporary, file, true);
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
            rdp.Add("/bpp:" + c.ColorDepth); rdp.Add("/audio-mode:" + c.AudioMode);
            if (c.ConsoleSession) rdp.Add("/admin");
            if (c.GatewayHost.Length > 0) rdp.Add("/gateway:g:" + c.GatewayHost + ",usage-method:" + c.GatewayMode);
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
