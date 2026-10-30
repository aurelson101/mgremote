namespace MGRemote.Linux;

internal static class ConnectionSettingsTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new Exception("Connection settings check failed at " + count); count++; }
        void Reject(Action action) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected); }
        var ssh = new Connection { Host = "server.example.invalid", Identity = "/tmp/test private key", CertificateFile = "/tmp/test certificate.pub", TunnelEnabled = true, TunnelRemoteHost = "rdp.example.invalid" };
        var args = SessionLauncher.Arguments(ssh);
        Check(args.Contains("CertificateFile=\"/tmp/test certificate.pub\""));
        Check(args.Contains("GatewayPorts=no") && args.Contains("ExitOnForwardFailure=yes"));
        Check(args.Contains("-L") && args[args.IndexOf("-L") + 1] == "127.0.0.1:13389:rdp.example.invalid:3389");
        Check(args[^1] == ssh.Host && !args.Contains("-R") && !args.Contains("-g"));
        var ipv6 = SessionLauncher.Arguments(ssh with { TunnelRemoteHost = "2001:db8::1" });
        Check(ipv6.Contains("127.0.0.1:13389:[2001:db8::1]:3389"));
        var sftp = SessionLauncher.Arguments(ssh, true);
        Check(sftp.Contains("ClearAllForwardings=yes") && !sftp.Contains("-L") && sftp.Contains("CertificateFile=\"/tmp/test certificate.pub\""));
        var transfer = FileTransfer.Arguments(ssh, "/tmp/test transfer", "/remote/file", false, false);
        Check(transfer.Contains("ClearAllForwardings=yes") && !transfer.Contains("-L") && transfer.Contains("CertificateFile=\"/tmp/test certificate.pub\""));
        foreach (string host in new[] { "", "-oProxyCommand=command", "host;cmd", "host\ncommand", "host:3389", "*", "host name" }) Reject(() => Connections.Validate(ssh with { TunnelRemoteHost = host }));
        foreach (int port in new[] { 0, 22, 65536 }) Reject(() => Connections.Validate(ssh with { TunnelLocalPort = port }));
        foreach (int port in new[] { 0, 65536 }) Reject(() => Connections.Validate(ssh with { TunnelRemotePort = port }));
        foreach (string path in new[] { "file\nother", "file\" extra", "${HOME}/file", "/tmp/%h", "C:\\key.pub" }) Reject(() => Connections.Validate(ssh with { CertificateFile = path }));
        Reject(() => Connections.Validate(ssh with { CertificateFile = null! }));
        var rdp = new Connection { Protocol = "RDP", Host = "rdp.example.invalid", Port = 3389, RdpResolution = "fixed", RdpWidth = 1920, RdpHeight = 1080 };
        var fixedArgs = SessionLauncher.Arguments(rdp);
        Check(fixedArgs.Contains("/size:1920x1080") && !fixedArgs.Contains("/dynamic-resolution") && fixedArgs.Contains("/sec:nla"));
        Check(SessionLauncher.Arguments(rdp with { RdpResolution = "fullscreen" }).Contains("/f"));
        Check(SessionLauncher.Arguments(rdp with { RdpResolution = "dynamic" }).Contains("/dynamic-resolution"));
        Check(SessionLauncher.Arguments(rdp with { RdpResolution = "fullscreen", MultiMonitor = true }).Count(a => a == "/f") == 1);
        Check(!SessionLauncher.Arguments(rdp with { MultiMonitor = true }).Any(a => a.StartsWith("/size:") || a == "/dynamic-resolution"));
        Reject(() => Connections.Validate(rdp with { RdpResolution = "custom /cert:ignore" }));
        Reject(() => Connections.Validate(rdp with { RdpWidth = 0 }));
        Reject(() => Connections.Validate(rdp with { RdpHeight = 8193 }));
        Reject(() => Connections.Validate(rdp with { TunnelEnabled = true }));
        var restored = System.Text.Json.JsonSerializer.Deserialize<Connection>(System.Text.Json.JsonSerializer.Serialize(ssh))!;
        Check(restored.TunnelEnabled && restored.TunnelLocalPort == 13389 && restored.CertificateFile == ssh.CertificateFile);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Connection>("{\"Host\":\"legacy.example.invalid\"}")!;
        Check(legacy.RdpResolution == "dynamic" && legacy.CertificateFile == "" && !legacy.TunnelEnabled && legacy.Icon == "Auto");
        Console.WriteLine($"PASS: {count} SSH certificate, loopback tunnel, transfer isolation, RDP resolution and compatibility checks.");
    }
}
