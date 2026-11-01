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
    public string Icon { get; set; } = "Auto";
    public string EncryptedPassword { get; set; } = "";
    public string GatewayHost { get; set; } = "";
    public string GatewayMode { get; set; } = "direct";
    public string Description { get; set; } = "";
    public int ColorDepth { get; set; } = 32;
    public int AudioMode { get; set; } = 2;
    public bool ConsoleSession { get; set; }
    public string RdpResolution { get; set; } = "dynamic";
    public int RdpWidth { get; set; } = 1280;
    public int RdpHeight { get; set; } = 720;
    public string Host { get; set; } = "";
    public string Protocol { get; set; } = "SSH";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Identity { get; set; } = "";
    public string CertificateFile { get; set; } = "";
    public string JumpHost { get; set; } = "";
    public bool TunnelEnabled { get; set; }
    public int TunnelLocalPort { get; set; } = 13389;
    public string TunnelRemoteHost { get; set; } = "localhost";
    public int TunnelRemotePort { get; set; } = 3389;
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
                Icon = ConnectionIcons.FromImportedIcon(Effective(node, "Icon")),
                Protocol = protocol == "SSH2" ? "SSH" : "RDP", Port = port,
                User = Effective(node, "Username") ?? "", Domain = Effective(node, "Domain") ?? "",
                Description = Effective(node, "Descr") ?? "", ColorDepth = depth, AudioMode = audio,
                Clipboard = ReadBoolean(node, "RedirectClipboard"), ConsoleSession = ReadBoolean(node, "UseConsoleSession"),
                FolderPath = node.Ancestors().Reverse().Where(a => (string?)a.Attribute("Type") == "Container").Select(a => (string?)a.Attribute("Name") ?? "Dossier").ToArray()
            };
            if (protocol == "RDP") {
                string resolution = Effective(node, "Resolution") ?? "FitToWindow";
                connection.RdpResolution = resolution switch { "Fullscreen" => "fullscreen", "SmartSize" or "FitToWindow" => "dynamic", _ => throw new InvalidDataException("Résolution RDP XML non reconnue : " + resolution) };
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
        if (c == null || c.Host == null || c.User == null || c.Domain == null || c.Identity == null || c.CertificateFile == null || c.TunnelRemoteHost == null || c.JumpHost == null || c.RdpSharePath == null) throw new ArgumentException("Connexion incomplète : hôte, utilisateur, domaine ou clé manquant dans les données.");
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 256 || c.Name.Any(char.IsControl)) throw new ArgumentException("Nom invalide.");
        if (c.Icon is not ("Auto" or "Windows" or "Linux" or "Network" or "Serial")) throw new ArgumentException("Icône de connexion invalide.");
        if (c.EncryptedPassword == null || c.EncryptedPassword.Length > 16384 || (c.EncryptedPassword.Length > 0 && !c.EncryptedPassword.StartsWith("v1:"))) throw new ArgumentException("Secret local non valide.");
        if (c.GatewayHost == null || c.GatewayMode is not ("direct" or "detect")) throw new ArgumentException("Passerelle RDP non valide.");
        if (c.GatewayHost.Length > 0 && (!Regex.IsMatch(c.GatewayHost, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,252}(?::[0-9]{1,5})?\z") || (c.GatewayHost.Contains(':') && (!int.TryParse(c.GatewayHost.Split(':')[1], out int gatewayPort) || gatewayPort is < 1 or > 65535)))) throw new ArgumentException("Hôte ou port de passerelle RDP invalide.");
        if (c.Description == null || c.Description.Length > 4096 || c.Description.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))) throw new ArgumentException("Description invalide.");
        if (c.ColorDepth is not (8 or 15 or 16 or 24 or 32) || c.AudioMode is < 0 or > 2) throw new ArgumentException("Couleurs ou son RDP invalides.");
        if (c.RdpResolution is not ("dynamic" or "fixed" or "fullscreen") || c.RdpWidth is < 200 or > 8192 || c.RdpHeight is < 200 or > 8192) throw new ArgumentException("Résolution RDP invalide (200 à 8192 pixels).");
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
        if (c.CertificateFile.Length > 4096 || c.CertificateFile.Any(char.IsControl) || c.CertificateFile.IndexOfAny(['"', '\\', '$', '%']) >= 0) throw new ArgumentException("Chemin de certificat SSH invalide.");
        if (c.TunnelEnabled && (c.Protocol != "SSH" || c.TunnelLocalPort is < 1024 or > 65535 || c.TunnelRemotePort is < 1 or > 65535 || !Regex.IsMatch(c.TunnelRemoteHost, @"\A(?:[A-Za-z0-9_][A-Za-z0-9_.-]{0,252}|\[[0-9A-Fa-f:]+\]|[0-9A-Fa-f]*:[0-9A-Fa-f:]+)\z")))
            throw new ArgumentException("Tunnel SSH invalide : port local 1024–65535, destination hôte/IP et port 1–65535 requis.");
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
    public sealed record Store(int Version, Connection[] Items, string[][] Folders)
    {
        [System.Text.Json.Serialization.JsonIgnore] public string Revision { get; init; } = "missing";
    }
    public static List<Connection> Load() => LoadStore().Items.ToList();
    public static Store LoadStore(string? dataPath = null, ProfileKey? profileKey = null)
    {
        string file = dataPath ?? DataPath;
        string content;
        try { content = ProfileEncryption.ReadProfileText(file); } catch (FileNotFoundException) { return new Store(1, [], []); }
        string revision = ProfileEncryption.Revision(content);
        using (var envelope = JsonDocument.Parse(content)) {
            if (ProfileEncryption.IsEnvelope(envelope.RootElement)) content = ProfileEncryption.Decode(envelope.RootElement, profileKey);
            else if (System.Text.Encoding.UTF8.GetByteCount(content) > ProfileEncryption.MaxPlaintext) throw new InvalidDataException("Fichier de connexions trop volumineux.");
        }
        return ParseStore(content) with { Revision = revision };
    }
    internal static Store ParseStore(string content)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(content) > ProfileEncryption.MaxPlaintext) throw new InvalidDataException("Fichier de connexions trop volumineux.");
        using var json = JsonDocument.Parse(content);
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
    public static string Save(IEnumerable<Connection> connections, IEnumerable<string[]>? folders = null, string? dataPath = null, ProfileKey? profileKey = null, string? expectedRevision = null)
    {
        var items = connections.ToArray(); foreach (var item in items) Validate(item, allowDraft: true);
        var paths = (folders ?? []).ToArray(); foreach (var path in paths) { ValidateFolderPath(path); if (path.Length == 0) throw new ArgumentException("Dossier racine explicite invalide."); }
        string file = Path.GetFullPath(dataPath ?? DataPath);
        string folder = Path.GetDirectoryName(file)!;
        ProfileEncryption.RejectLinkedPath(file);
        // Existing encrypted bytes must authenticate with this key before replacement, even during encryption setup.
        string originalRevision = LoadStore(file, profileKey).Revision;
        if (expectedRevision != null && originalRevision != expectedRevision) throw new IOException("Le profil a été modifié par un autre programme. Relancez MGRemote pour recharger ses changements avant d’enregistrer.");
        string content = JsonSerializer.Serialize(new Store(1, items, paths), new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(content) > ProfileEncryption.MaxPlaintext) throw new InvalidDataException("Profil trop volumineux.");
        if (profileKey != null) content = ProfileEncryption.Encode(content, profileKey);
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string temporary = Path.Combine(folder, ".connections-" + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) {
                using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true)) writer.Write(content);
                stream.Flush(flushToDisk: true);
            }
            ProfileEncryption.RejectLinkedPath(file);
            if (ProfileEncryption.ReadRevision(file) != originalRevision) throw new IOException("Le profil a changé pendant l’enregistrement. Le fichier existant est conservé ; relancez MGRemote pour le recharger.");
            File.Move(temporary, file, true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return ProfileEncryption.Revision(content);
    }
}

public static class SessionLauncher
{
    internal const string ClipboardTranslations = "XTerm*VT100.translations: #override\\n Ctrl Shift <Key>C: copy-selection(CLIPBOARD)\\n Ctrl Shift <Key>V: insert-selection(CLIPBOARD)";
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
            var rdp = new List<string> { "/v:" + host + ":" + c.Port, "/sec:nla", "/from-stdin:force" };
            if (!c.MultiMonitor && c.RdpResolution == "dynamic") rdp.Add("/dynamic-resolution");
            else if (!c.MultiMonitor && c.RdpResolution == "fixed") rdp.Add("/size:" + c.RdpWidth + "x" + c.RdpHeight);
            else if (!c.MultiMonitor) rdp.Add("/f");
            if (c.User.Length > 0) rdp.Add("/u:" + c.User);
            if (c.Domain.Length > 0) rdp.Add("/d:" + c.Domain);
            rdp.Add(c.Clipboard ? "+clipboard" : "-clipboard");
            rdp.Add("/bpp:" + c.ColorDepth); rdp.Add("/audio-mode:" + c.AudioMode);
            if (c.ConsoleSession) rdp.Add("/admin");
            if (c.GatewayHost.Length > 0) rdp.Add("/gateway:g:" + c.GatewayHost + ",usage-method:" + c.GatewayMode);
            if (c.MultiMonitor) rdp.AddRange(["/multimon", "/f"]);
            if (c.RdpShareEnabled) rdp.Add("/drive:MGRemote," + Path.GetFullPath(c.RdpSharePath));
            return rdp; // Certificate verification is retained; no password or redirected drives.
        }
        var ssh = new List<string> {
            "-o", "StrictHostKeyChecking=ask", "-o", "ForwardAgent=no", "-o", "PermitLocalCommand=no",
            "-o", "ForwardX11=no", "-o", "ForwardX11Trusted=no", "-o", "GSSAPIDelegateCredentials=no",
            // Keep every client and its forwards within the owning tab's lifetime.
            "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "ControlPersist=no", "-o", "ForkAfterAuthentication=no"
        };
        if (!sftp) ssh.Add("-tt");
        ssh.Add(sftp ? "-P" : "-p"); ssh.Add(c.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (c.Identity.Length > 0) { ssh.AddRange(["-i", c.Identity, "-o", "IdentitiesOnly=yes"]); }
        if (c.CertificateFile.Length > 0) ssh.AddRange(["-o", "CertificateFile=\"" + c.CertificateFile + "\""]);
        if (c.JumpHost.Length > 0) ssh.AddRange(["-J", c.JumpHost]);
        if (sftp) ssh.AddRange(["-o", "ClearAllForwardings=yes"]);
        else if (c.TunnelEnabled) {
            string tunnelHost = c.TunnelRemoteHost.Contains(':') && !c.TunnelRemoteHost.StartsWith('[') ? "[" + c.TunnelRemoteHost + "]" : c.TunnelRemoteHost;
            ssh.AddRange(["-o", "GatewayPorts=no", "-o", "ExitOnForwardFailure=yes", "-L", "127.0.0.1:" + c.TunnelLocalPort + ":" + tunnelHost + ":" + c.TunnelRemotePort]);
        }
        if (c.User.Length > 0) ssh.AddRange(["-o", "User=" + c.User]);
        string destination = c.Host;
        if (sftp && destination.Contains(':') && !destination.StartsWith('[')) destination = "[" + destination + "]";
        ssh.Add(destination); return ssh;
    }
    internal static string Find(params string[] names)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cette édition nécessite Linux.");
        foreach (string name in names) foreach (string prefix in new[] { "/usr/bin", "/bin" }) {
            string file = Path.Combine(prefix, name);
            if (!File.Exists(file)) continue;
            var resolved = new FileInfo(file).ResolveLinkTarget(true)?.FullName ?? file;
            if ((File.GetUnixFileMode(resolved) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) continue;
            return file;
        }
        if (names.Contains("xterm")) {
            string bundled = Path.Combine(AppContext.BaseDirectory, "terminal", "xterm");
            if (File.Exists(bundled) && new FileInfo(bundled).LinkTarget == null &&
                (File.GetUnixFileMode(bundled) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0) return bundled;
        }
        throw new FileNotFoundException("Composant Linux manquant : " + string.Join(" / ", names) + ". Installez ce composant avec le gestionnaire de paquets de votre distribution.");
    }
    internal static string IdentityPath(string path)
    {
        path = path.Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Saisissez le chemin absolu de la clé privée (ou ~/.ssh/id_ed25519).");
        return Path.GetFullPath(path);
    }
    internal static void ValidateIdentityFile(string path)
    {
        string content = ReadSshFile(path, 1048576, privateKey: true).Trim();
        // Validate legacy encrypted PEM armor too; the original file is passed unchanged to SSH.
        content = Regex.Replace(content, @"\A(-----BEGIN (?:RSA|DSA|EC) PRIVATE KEY-----\r?\n)Proc-Type: 4,ENCRYPTED\r?\nDEK-Info: [A-Za-z0-9-]+,[0-9A-Fa-f]+\r?\n\r?\n", "$1");
        ReadOnlySpan<char> text = content.AsSpan();
        if (System.Security.Cryptography.PemEncoding.TryFind(text, out var fields)) {
            var (offset, length) = fields.Location.GetOffsetAndLength(text.Length);
            string label = text[fields.Label].ToString();
            if (offset == 0 && length == text.Length && fields.DecodedDataLength > 0 &&
                label is "OPENSSH PRIVATE KEY" or "RSA PRIVATE KEY" or "DSA PRIVATE KEY" or "EC PRIVATE KEY" or "PRIVATE KEY" or "ENCRYPTED PRIVATE KEY") return;
        }
        throw new IOException("Clé privée OpenSSH/PEM complète requise (base64 et marqueurs début/fin valides). Une clé publique (.pub) ne convient pas ; convertissez les clés PuTTY (.ppk) au format OpenSSH.");
    }
    private static string ReadSshFile(string path, int maximumBytes, bool privateKey)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cette édition nécessite Linux.");
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Chemin absolu requis pour le fichier SSH.");
        // SSH accepts linked keys/folders; inspect permissions and bounded content on the actual target.
        using var stream = ProfileEncryption.OpenLocalRead(path, allowLinks: true);
        var forbidden = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if (privateKey) forbidden |= UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(stream.SafeFileHandle) & forbidden) != 0)
            throw new IOException(privateKey ? "La clé privée doit être accessible uniquement à son propriétaire (permissions 600)." : "Le certificat SSH ne doit pas être modifiable par le groupe ou les autres utilisateurs.");
        try { return ProfileEncryption.ReadBoundedText(stream, maximumBytes); }
        catch (InvalidDataException) { throw new IOException("Fichier SSH trop volumineux (maximum " + maximumBytes / 1024 + " Kio)."); }
    }
    internal static void Preflight(Connection c)
    {
        ValidateIdentity(c);
        _ = Find("xterm");
        _ = Executable(c, false);
    }
    internal static void ValidateIdentity(Connection c)
    {
        Connections.Validate(c);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Les sessions intégrées nécessitent Linux X11/XWayland.");
        if (c.Protocol == "RDP" && c.RdpShareEnabled && (!Directory.Exists(c.RdpSharePath) || new DirectoryInfo(c.RdpSharePath).LinkTarget != null))
            throw new IOException("Le dossier à partager en RDP doit exister et ne pas être un lien symbolique.");
        if (c.Identity.Length > 0 && c.Protocol == "SSH") {
            ValidateIdentityFile(c.Identity);
        }
        if (c.CertificateFile.Length > 0 && c.Protocol == "SSH") {
            string content = ReadSshFile(c.CertificateFile, 65536, privateKey: false);
            string header = content.Split('\n', 2)[0].TrimEnd('\r');
            if (!Regex.IsMatch(header, @"\A(?:ssh|ecdsa|sk)-[A-Za-z0-9@.-]+-cert-v01@openssh\.com [A-Za-z0-9+/]+={0,2}(?: |$)"))
                throw new IOException("Un certificat public OpenSSH signé (-cert.pub) est requis.");
        }
    }
    private static string Executable(Connection c, bool sftp) => c.Protocol == "Serial" ? Find("picocom") : c.Protocol == "RDP" ? Find("xfreerdp3", "xfreerdp") : Find(sftp ? "sftp" : "ssh");
    internal static ProcessStartInfo ExternalStart(Connection c, bool sftp = false)
    {
        ValidateIdentity(c);
        var arguments = Arguments(c, sftp);
        // xterm stays in the foreground; desktop terminal launchers may detach through D-Bus.
        var start = TerminalStart();
        foreach (string arg in new[] { "-hold", "-xrm", "XTerm*allowWindowOps: false", "-xrm", ClipboardTranslations, "-title", "MGRemote · " + c.Name, "-e", Executable(c, sftp) }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }
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
        var start = TerminalStart();
        start.ArgumentList.Add("-hold"); // Keep connection errors/output readable after the client exits.
        start.ArgumentList.Add("-xrm"); start.ArgumentList.Add("XTerm*allowWindowOps: false");
        foreach (string arg in new[] { "-into", parent.ToString(System.Globalization.CultureInfo.InvariantCulture), "-bg", "#181a1f", "-fg", "#e6e6e6", "-fa", "Monospace", "-fs", "11", "-sl", "5000", "-xrm", ClipboardTranslations, "-e", executable }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        return start;
    }
    private static ProcessStartInfo TerminalStart()
    {
        var start = new ProcessStartInfo(Find("xterm")) { UseShellExecute = false };
        if (start.FileName == Path.Combine(AppContext.BaseDirectory, "terminal", "xterm")) {
            string libraries = Path.GetDirectoryName(start.FileName)!;
            start.Environment.TryGetValue("LD_LIBRARY_PATH", out string? existing);
            start.Environment["LD_LIBRARY_PATH"] = libraries + (string.IsNullOrEmpty(existing) ? "" : ":" + existing);
        }
        start.ArgumentList.Add("-ut"); // No utmp helper or elevated terminal permissions.
        return start;
    }
}
