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
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='Override' Hostname='server' Protocol='RDP' Port='3390' InheritPort='false'/></Connections>");
            Check(Connections.Import(path).Single().Port == 3390);
            File.WriteAllText(path, "<!DOCTYPE Connections [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><Connections>&secret;</Connections>");
            bool blocked = false; try { Connections.Import(path); } catch (System.Xml.XmlException) { blocked = true; } Check(blocked);
        } finally { File.Delete(path); }
        Console.WriteLine($"PASS: {count} Linux metadata/argument/import security checks; no remote connections.");
    }
}
