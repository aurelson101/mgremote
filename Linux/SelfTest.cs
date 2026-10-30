namespace MGRemote.Linux;
internal static class SelfTest
{
    public static void Run()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new Exception("Linux self-test failed at " + count); count++; }
        void Reject(Action action) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected); }
        var ssh = new Connection { Host = "server.example", User = "alice", Identity = "/home/alice/private key" };
        var args = SessionLauncher.Arguments(ssh);
        Check(args.Contains("-tt") && args.Contains("StrictHostKeyChecking=ask") && args.Contains(ssh.Identity));
        Check(!args.Contains("sh") && !args.Contains("-c"));
        Check(SessionLauncher.Arguments(ssh, true).Contains("-P"));
        foreach (string bad in new[] { "-oProxyCommand=evil", "host;touch /tmp/bad", "server\nother", "" }) Reject(() => Connections.Validate(ssh with { Host = bad }));
        Reject(() => Connections.Validate(ssh with { Port = 0 }));
        Reject(() => Connections.Validate(ssh with { User = "alice\ncmd" }));
        string localTransfer = Path.Combine(Path.GetTempPath(), "mgremote transfer fixture");
        var uploadArgs = FileTransfer.Arguments(ssh, localTransfer, "/remote path/file", false, false);
        Check(uploadArgs.Contains(ssh.Identity) && uploadArgs.Contains("StrictHostKeyChecking=ask") && uploadArgs[^1] == "server.example:/remote path/file");
        Check(!uploadArgs.Contains("-O") && !uploadArgs.Contains("-T") && uploadArgs.Contains("--"));
        var downloadArgs = FileTransfer.Arguments(ssh with { Host = "2001:db8::1" }, localTransfer, "/remote folder", true, true);
        Check(downloadArgs.Contains("-r") && downloadArgs[^2] == "[2001:db8::1]:/remote folder" && downloadArgs[^1] == localTransfer);
        foreach (string bad in new[] { "relative/path", "/remote/*", "/bad\npath" }) Reject(() => FileTransfer.Arguments(ssh, localTransfer, bad, false, false));
        Reject(() => FileTransfer.Arguments(ssh, "relative", "/remote/file", false, false));
        string fixture = Path.Combine(Path.GetTempPath(), "mgremote-transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        try {
            File.WriteAllText(Path.Combine(fixture, "fixture.txt"), "synthetic");
            FileTransfer.ValidateUpload(fixture, true); Check(true);
            Reject(() => FileTransfer.ValidateUpload(fixture, false));
            if (OperatingSystem.IsLinux()) {
                File.CreateSymbolicLink(Path.Combine(fixture, "link"), Path.Combine(fixture, "fixture.txt"));
                bool blocked = false; try { FileTransfer.ValidateUpload(fixture, true); } catch (IOException) { blocked = true; } Check(blocked);
                File.Delete(Path.Combine(fixture, "link"));
            }
        } finally { File.Delete(Path.Combine(fixture, "fixture.txt")); Directory.Delete(fixture); }
        Reject(() => Connections.Validate(ssh with { Host = null! }));
        var serial = new Connection { Protocol = "Serial", Host = "/dev/ttyUSB0", BaudRate = 9600, DataBits = 8, Parity = "none", StopBits = 1, FlowControl = "none" };
        var serialArgs = SessionLauncher.Arguments(serial);
        Check(serialArgs.SequenceEqual(new[] { "--baud", "9600", "--databits", "8", "--parity", "none", "--stopbits", "1", "--flow", "none", "/dev/ttyUSB0" }));
        Reject(() => Connections.Validate(serial with { Host = "/dev/ttyUSB0;command" }));
        Reject(() => Connections.Validate(serial with { BaudRate = 0 }));
        Reject(() => SessionLauncher.Arguments(serial, true));
        var roundtrip = System.Text.Json.JsonSerializer.Deserialize<Connection>(System.Text.Json.JsonSerializer.Serialize(serial))!;
        Check(roundtrip.Protocol == "Serial" && roundtrip.Host == serial.Host && roundtrip.BaudRate == 9600);
        var rdp = SessionLauncher.Arguments(new Connection { Host = "2001:db8::1", Protocol = "RDP", Port = 3389, Clipboard = true, MultiMonitor = true });
        Check(rdp.Contains("/v:[2001:db8::1]:3389") && rdp.Contains("/sec:nla") && rdp.Contains("+clipboard") && rdp.Contains("/multimon"));
        Check(!rdp.Any(s => s.StartsWith("/p:") || s.Contains("ignore") || s.StartsWith("/drive:")));
        string path = Path.Combine(Path.GetTempPath(), "mgremote-linux-test-" + Guid.NewGuid().ToString("N") + ".xml");
        try {
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='Linux' Protocol='SSH2' Hostname='server' Username='alice' Port='2222' Password='DO-NOT-IMPORT' SSHOptions='-i windows-key'/></Connections>");
            var imported = Connections.Import(path);
            Check(imported.Count == 1 && imported[0].Port == 2222 && imported[0].Identity == "");
            Check(!System.Text.Json.JsonSerializer.Serialize(imported).Contains("DO-NOT-IMPORT"));
            File.WriteAllText(path, "<Connections><Node Type='Container' Protocol='SSH2' Port='2222' Username='inherited-user' Domain='example'><Node Type='Connection' Name='Inherited' Hostname='server' Protocol='RDP' Port='3389' Username='wrong-user' InheritProtocol='true' InheritPort='true' InheritUsername='true' InheritDomain='true'/></Node></Connections>");
            var inherited = Connections.Import(path).Single();
            Check(inherited.Protocol == "SSH" && inherited.Port == 2222 && inherited.User == "inherited-user" && inherited.Domain == "example");
            Check(inherited.FolderPath.Length == 1);
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='Override' Hostname='server' Protocol='RDP' Port='3390' InheritPort='false'/></Connections>");
            Check(Connections.Import(path).Single().Port == 3390);
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='RDP' Protocol='RDP' Hostname='server'/><Node Type='Connection' Name='Old Telnet' Protocol='Telnet' Hostname='switch'/></Connections>");
            Check(Connections.Import(path, out int ignored).Count == 1 && ignored == 1);
            File.WriteAllText(path, "<Connections FullFileEncryption='true'><Node Protocol='RDP' Hostname='server'/></Connections>");
            bool encryptedBlocked = false; try { Connections.Import(path); } catch (InvalidDataException) { encryptedBlocked = true; } Check(encryptedBlocked);
            File.WriteAllText(path, "<!DOCTYPE Connections [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><Connections>&secret;</Connections>");
            bool blocked = false; try { Connections.Import(path); } catch (System.Xml.XmlException) { blocked = true; } Check(blocked);
        } finally { File.Delete(path); }
        Console.WriteLine($"PASS: {count} Linux metadata/argument/import security checks; no remote connections.");
    }
}
