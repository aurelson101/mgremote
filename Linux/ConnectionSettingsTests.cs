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
        foreach (string option in new[] { "ForwardAgent=no", "ForwardX11=no", "ForwardX11Trusted=no", "GSSAPIDelegateCredentials=no", "ControlMaster=no", "ControlPath=none", "ControlPersist=no", "ForkAfterAuthentication=no" }) Check(args.Contains(option));
        Check(args.Contains("CertificateFile=\"/tmp/test certificate.pub\""));
        Check(args.Contains("GatewayPorts=no") && args.Contains("ExitOnForwardFailure=yes"));
        Check(args.Contains("-L") && args[args.IndexOf("-L") + 1] == "127.0.0.1:13389:rdp.example.invalid:3389");
        Check(args[^1] == ssh.Host && !args.Contains("-R") && !args.Contains("-g"));
        var ipv6 = SessionLauncher.Arguments(ssh with { TunnelRemoteHost = "2001:db8::1" });
        Check(ipv6.Contains("127.0.0.1:13389:[2001:db8::1]:3389"));
        var sftp = SessionLauncher.Arguments(ssh, true);
        Check(sftp.Contains("ForwardX11=no") && sftp.Contains("ControlPath=none") && sftp.Contains("ForkAfterAuthentication=no"));
        Check(sftp.Contains("ClearAllForwardings=yes") && !sftp.Contains("-L") && sftp.Contains("CertificateFile=\"/tmp/test certificate.pub\""));
        var transfer = FileTransfer.Arguments(ssh, "/tmp/test transfer", "/remote/file", false, false);
        Check(transfer.Contains("GSSAPIDelegateCredentials=no") && transfer.Contains("ControlMaster=no"));
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
        const string clipboardFiles = "/clipboard:use-selection:CLIPBOARD,direction-to:all,files-to:all";
        Check(SessionLauncher.Arguments(rdp with { Clipboard = true, RdpFileClipboard = true }).Contains(clipboardFiles));
        Check(SessionLauncher.Arguments(rdp with { Clipboard = true }, modernRdpClipboard: true).Contains("/clipboard:use-selection:CLIPBOARD,direction-to:all,files-to:off"));
        Check(SessionLauncher.Arguments(rdp with { Clipboard = true }).Contains("+clipboard"));
        Check(SessionLauncher.Arguments(rdp, modernRdpClipboard: true).Contains("-clipboard"));
        Reject(() => Connections.Validate(rdp with { Clipboard = false, RdpFileClipboard = true }));
        Reject(() => Connections.Validate(ssh with { Clipboard = true, RdpFileClipboard = true }));
        var fileClip = System.Text.Json.JsonSerializer.Deserialize<Connection>(System.Text.Json.JsonSerializer.Serialize(rdp with { Clipboard = true, RdpFileClipboard = true }))!;
        Check(fileClip.RdpFileClipboard && fileClip.Clipboard);
        Reject(() => Connections.Validate(rdp with { RdpResolution = "custom /cert:ignore" }));
        Reject(() => Connections.Validate(rdp with { RdpWidth = 0 }));
        Reject(() => Connections.Validate(rdp with { RdpHeight = 8193 }));
        Reject(() => Connections.Validate(rdp with { TunnelEnabled = true }));
        var restored = System.Text.Json.JsonSerializer.Deserialize<Connection>(System.Text.Json.JsonSerializer.Serialize(ssh))!;
        Check(restored.TunnelEnabled && restored.TunnelLocalPort == 13389 && restored.CertificateFile == ssh.CertificateFile);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<Connection>("{\"Host\":\"legacy.example.invalid\"}")!;
        Check(legacy.RdpResolution == "dynamic" && legacy.CertificateFile == "" && !legacy.TunnelEnabled && legacy.Icon == "Auto");
        if (OperatingSystem.IsLinux()) {
            string folder = Path.Combine(Path.GetTempPath(), "mgremote-client-policy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try {
                string empty = Path.Combine(folder, "empty"); File.WriteAllText(empty, ""); File.SetUnixFileMode(empty, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                void RejectFile(Connection connection) { bool rejected = false; try { SessionLauncher.ValidateIdentity(connection); } catch (IOException) { rejected = true; } Check(rejected); }
                RejectFile(ssh with { Identity = empty, CertificateFile = "" });
                RejectFile(ssh with { Identity = "", CertificateFile = empty });
                string large = Path.Combine(folder, "oversize");
                using (var file = new FileStream(large, FileMode.CreateNew)) file.SetLength(1048577);
                File.SetUnixFileMode(large, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                RejectFile(ssh with { Identity = large, CertificateFile = "" });
                RejectFile(ssh with { Identity = "", CertificateFile = large });
                if (File.Exists("/usr/bin/ssh")) {
                    string config = Path.Combine(folder, "config");
                    File.WriteAllText(config, "Host *\n    ForwardX11 yes\n    ForwardX11Trusted yes\n    ForwardAgent yes\n    GSSAPIDelegateCredentials yes\n    ControlMaster auto\n    ControlPath /tmp/mgremote-synthetic-control\n    ControlPersist 60\n    ForkAfterAuthentication yes\n");
                    var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/ssh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("-G"); start.ArgumentList.Add("-F"); start.ArgumentList.Add(config);
                    foreach (string argument in args) start.ArgumentList.Add(argument);
                    using var parser = System.Diagnostics.Process.Start(start)!;
                    var output = parser.StandardOutput.ReadToEndAsync(); var error = parser.StandardError.ReadToEndAsync();
                    if (!parser.WaitForExit(5000)) { parser.Kill(true); throw new Exception("Offline OpenSSH configuration check timed out."); }
                    string effective = output.GetAwaiter().GetResult(); _ = error.GetAwaiter().GetResult();
                    Check(parser.ExitCode == 0);
                    foreach (string option in new[] { "forwardx11 no", "forwardx11trusted no", "forwardagent no", "gssapidelegatecredentials no", "controlmaster false", "controlpersist no", "forkafterauthentication no" }) Check(effective.Split('\n').Contains(option));
                    Check(!effective.Split('\n').Any(line => line.StartsWith("controlpath ")));
                    Check(effective.Contains("localforward [127.0.0.1]:13389 [rdp.example.invalid]:3389"));
                }
                if (File.Exists("/usr/bin/xterm")) {
                    var external = SessionLauncher.ExternalStart(ssh with { Identity = "", CertificateFile = "" });
                    Check(!external.UseShellExecute && Path.GetFileName(external.FileName) == "xterm");
                    Check(external.ArgumentList.Contains("XTerm*allowWindowOps: false") && external.ArgumentList.Contains("ControlPath=none"));
                    Check(external.ArgumentList.Contains("-e") && !external.ArgumentList.Contains("-into"));
                }
            } finally { foreach (string file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
        }
        Console.WriteLine($"PASS: {count} SSH certificate, loopback tunnel, transfer isolation, RDP resolution and compatibility checks.");
    }
}
