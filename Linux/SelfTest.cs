namespace MGRemote.Linux;
internal static class SelfTest
{
    public static void Run()
    {
        ConnectionIconTests.Run();
        ConnectionSettingsTests.Run();
        ProfileEncryptionTests.Run();
        ProfileTransferTests.Run();
        SshFileTests.Run();
        ProfileWorkflowTests.Run();
        int count = 0;
        void Check(bool condition) { if (!condition) throw new Exception("Linux self-test failed at " + count); count++; }
        void Reject(Action action) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected); }
        var ssh = new Connection { Host = "server.example", User = "alice", Identity = "/home/alice/private key" };
        var args = SessionLauncher.Arguments(ssh);
        Check(args.Contains("-tt") && args.Contains("StrictHostKeyChecking=ask") && args.Contains(ssh.Identity));
        Check(!args.Contains("sh") && !args.Contains("-c"));
        var jumped = ssh with { JumpHost = "bastion-profile" };
        var jumpedArgs = SessionLauncher.Arguments(jumped);
        Check(jumpedArgs.Contains("-J") && jumpedArgs[jumpedArgs.IndexOf("-J") + 1] == "bastion-profile");
        Check(FileTransfer.Arguments(jumped, Path.Combine(Path.GetTempPath(), "jump-fixture"), "/remote/file", false, false).Contains("bastion-profile"));
        foreach (string badJump in new[] { "-oProxyCommand=command", "jump host", "jump\nother", "server.example", "none" }) Reject(() => Connections.Validate(ssh with { JumpHost = badJump }));
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
        Check(rdp.Contains("/v:[2001:db8::1]:3389") && rdp.Contains("/sec:nla") && rdp.Contains("+clipboard") && rdp.Contains("/multimon") && rdp.Contains("/f"));
        var privateRdp = SessionLauncher.Arguments(new Connection { Host = "server", Protocol = "RDP", Port = 3389, Clipboard = false, MultiMonitor = false });
        Check(privateRdp.Contains("-clipboard") && !privateRdp.Contains("+clipboard") && !privateRdp.Contains("/multimon") && !privateRdp.Contains("/f"));
        Check(SessionLauncher.ClipboardTranslations.Contains("copy-selection(CLIPBOARD)") && SessionLauncher.ClipboardTranslations.Contains("insert-selection(CLIPBOARD)"));
        Check(!rdp.Any(s => s.StartsWith("/p:") || s.Contains("ignore") || s.StartsWith("/drive:")));
        string share = Path.Combine(Path.GetTempPath(), "rdp shared folder");
        var rdpShare = new Connection { Protocol = "RDP", Host = "rdp.example.invalid", Port = 3389, RdpShareEnabled = true, RdpSharePath = share };
        Check(SessionLauncher.Arguments(rdpShare).Contains("/drive:MGRemote," + share));
        Check(!SessionLauncher.Arguments(rdpShare with { RdpShareEnabled = false }).Any(a => a.StartsWith("/drive:")));
        foreach (string badShare in new[] { "relative/path", share + ",extra", share + "\nother" }) Reject(() => Connections.Validate(rdpShare with { RdpSharePath = badShare }));
        string MakeSource(string plain, string engine = "AES", string mode = "GCM")
        {
            byte[] salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), nonce = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
            byte[] key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("fixture-master", salt, 1000, System.Security.Cryptography.HashAlgorithmName.SHA1, 32);
            Org.BouncyCastle.Crypto.IBlockCipher block = engine switch { "Twofish" => new Org.BouncyCastle.Crypto.Engines.TwofishEngine(), "Serpent" => new Org.BouncyCastle.Crypto.Engines.SerpentEngine(), _ => new Org.BouncyCastle.Crypto.Engines.AesEngine() };
            Org.BouncyCastle.Crypto.Modes.IAeadBlockCipher cipher = mode == "EAX" ? new Org.BouncyCastle.Crypto.Modes.EaxBlockCipher(block) : new Org.BouncyCastle.Crypto.Modes.GcmBlockCipher(block);
            cipher.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(new Org.BouncyCastle.Crypto.Parameters.KeyParameter(key), 128, nonce, salt));
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(plain), output = new byte[cipher.GetOutputSize(bytes.Length)];
            int n = cipher.ProcessBytes(bytes, 0, bytes.Length, output, 0); n += cipher.DoFinal(output, n);
            return Convert.ToBase64String([..salt, ..nonce, ..output[..n]]);
        }
        foreach (string engine in new[] { "AES", "Twofish", "Serpent" }) foreach (string mode in new[] { "GCM", "EAX" }) {
            var root = System.Xml.Linq.XElement.Parse($"<Connections EncryptionEngine='{engine}' BlockCipherMode='{mode}' KdfIterations='1000'/>");
            Check(XmlSecrets.DecryptSource(MakeSource("fixture-password", engine, mode), "fixture-master", root) == "fixture-password");
        }
        var cryptoRoot = System.Xml.Linq.XElement.Parse("<Connections/>");
        void CryptoReject(Action action) { bool denied = false; try { action(); } catch (System.Security.Cryptography.CryptographicException) { denied = true; } Check(denied); }
        CryptoReject(() => XmlSecrets.DecryptSource(MakeSource("fixture-password"), "wrong-master", cryptoRoot));
        byte[] tampered = Convert.FromBase64String(MakeSource("fixture-password")); tampered[^1] ^= 1;
        CryptoReject(() => XmlSecrets.DecryptSource(Convert.ToBase64String(tampered), "fixture-master", cryptoRoot));
        string localEnvelope = XmlSecrets.Protect("fixture-password", "local-master-passphrase");
        Check(XmlSecrets.Unprotect(localEnvelope, "local-master-passphrase") == "fixture-password");
        Check(localEnvelope != XmlSecrets.Protect("fixture-password", "local-master-passphrase"));
        CryptoReject(() => XmlSecrets.Unprotect(localEnvelope, "wrong-local-master"));
        Reject(() => XmlSecrets.Protect("fixture-password", "short"));
        var gateway = new Connection { Host = "server", Protocol = "RDP", Port = 3389, GatewayHost = "gateway.example:443", GatewayMode = "detect" };
        Check(SessionLauncher.Arguments(gateway).Contains("/gateway:g:gateway.example:443,usage-method:detect"));
        foreach (string badGateway in new[] { "gateway,p:secret", "-gateway", "gateway:65536", "gateway\nother" }) Reject(() => Connections.Validate(gateway with { GatewayHost = badGateway }));
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
            File.WriteAllText(path, "<Connections><Node Type='Container' Name='Group' Hostname='server' Colors='Colors16Bit' RedirectSound='LeaveAtRemoteComputer' RedirectClipboard='true' UseConsoleSession='true' Descr='Inherited description'><Node Type='Connection' Name='Options' Protocol='RDP' InheritHostname='true' InheritColors='true' InheritRedirectSound='true' InheritRedirectClipboard='true' InheritUseConsoleSession='true' InheritDescr='true'/></Node></Connections>");
            var preferences = Connections.Import(path).Single();
            Check(preferences.Host == "server" && preferences.Description == "Inherited description");
            Check(preferences.ColorDepth == 16 && preferences.AudioMode == 1 && preferences.Clipboard && preferences.ConsoleSession);
            var preferenceArgs = SessionLauncher.Arguments(preferences);
            Check(preferenceArgs.Contains("/bpp:16") && preferenceArgs.Contains("/audio-mode:1") && preferenceArgs.Contains("/admin") && preferenceArgs.Contains("+clipboard"));
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='Bad port' Protocol='RDP' Hostname='server' Port='invalid'/></Connections>");
            bool invalidXmlRejected = false; try { Connections.Import(path); } catch (InvalidDataException) { invalidXmlRejected = true; } Check(invalidXmlRejected);
            File.WriteAllText(path, "<Connections><Node Type='Connection' Name='Bad boolean' Protocol='RDP' Hostname='server' RedirectClipboard='invalid'/></Connections>");
            invalidXmlRejected = false; try { Connections.Import(path); } catch (InvalidDataException) { invalidXmlRejected = true; } Check(invalidXmlRejected);
            string body = "<Node Type='Connection' Name='Encrypted' Protocol='RDP' Hostname='server' Password='" + MakeSource("fixture-password") + "' RDGatewayUsageMethod='Always' RDGatewayHostname='gateway.example'/>";
            File.WriteAllText(path, "<Connections FullFileEncryption='true'>" + MakeSource(body) + "</Connections>");
            var withSecret = Connections.Import(path, out _, "fixture-master", "local-master-passphrase").Single();
            Check(XmlSecrets.Unprotect(withSecret.EncryptedPassword, "local-master-passphrase") == "fixture-password");
            Check(!System.Text.Json.JsonSerializer.Serialize(withSecret).Contains("fixture-password"));
            Check(withSecret.GatewayHost == "gateway.example" && withSecret.GatewayMode == "direct");
            Check(Connections.Import(path, out _, "fixture-master").Single().EncryptedPassword == "");
            File.WriteAllText(path, "<Connections xmlns:n='urn:fixture' FullFileEncryption='true'>" + MakeSource(body.Replace("<Node ", "<n:Node ")) + "</Connections>");
            Check(Connections.Import(path, out _, "fixture-master").Single().Host == "server");
            CryptoReject(() => Connections.Import(path, out _, "wrong-master", "local-master-passphrase"));
            File.WriteAllText(path, "<Connections FullFileEncryption='true'>" + MakeSource("<!DOCTYPE bad><Node/>") + "</Connections>");
            bool innerDtdBlocked = false; try { Connections.Import(path, out _, "fixture-master"); } catch (System.Xml.XmlException) { innerDtdBlocked = true; } Check(innerDtdBlocked);
            File.WriteAllText(path, "<Connections FullFileEncryption='true'><Node Protocol='RDP' Hostname='server'/></Connections>");
            bool encryptedBlocked = false; try { Connections.Import(path); } catch (System.Security.Cryptography.CryptographicException) { encryptedBlocked = true; } Check(encryptedBlocked);
            File.WriteAllText(path, "<Connections><Node Type='Container' Name='Empty'><Node Type='Container' Name='Nested'/></Node></Connections>");
            Check(Connections.Import(path, out _, out var emptyFolders).Count == 0 && emptyFolders.Length == 2);
            Check(emptyFolders[1].SequenceEqual(new[] { "Empty", "Nested" }));
            var draft = new Connection { IsDraft = true }; Connections.Validate(draft, allowDraft: true); Reject(() => SessionLauncher.Arguments(draft));
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { new Connection { Host = "server" } }));
            Check(Connections.LoadStore(path).Items.Single().Host == "server" && Connections.LoadStore(path).Folders.Length == 0);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new[] { new Connection { Host = "server", FolderPath = ["Legacy", "Nested"] } }));
            Check(Connections.LoadStore(path).Folders.Length == 2);
            File.WriteAllText(path, "{\"Version\":99,\"Items\":[],\"Folders\":[]}");
            bool futureStoreBlocked = false; try { Connections.LoadStore(path); } catch (InvalidDataException) { futureStoreBlocked = true; } Check(futureStoreBlocked);
            File.WriteAllText(path, "<!DOCTYPE Connections [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><Connections>&secret;</Connections>");
            bool blocked = false; try { Connections.Import(path); } catch (System.Xml.XmlException) { blocked = true; } Check(blocked);
        } finally { File.Delete(path); }
        if (OperatingSystem.IsLinux()) count += VerifyCredentialBroker().GetAwaiter().GetResult();
        Console.WriteLine($"PASS: {count} Linux metadata/argument/import security checks; no remote connections.");
    }
    private static async Task<int> VerifyCredentialBroker()
    {
        int count = 0;
        void Check(bool ok) { if (!ok) throw new Exception("Credential broker offline check failed at " + count); count++; }
        string binary = Path.Combine(AppContext.BaseDirectory, "MGRemoteLinux");
        System.Diagnostics.ProcessStartInfo Setup(CredentialBroker broker, string protocol, params string[] arguments)
        {
            var terminal = SessionLauncher.Terminal(1, "/usr/bin/ssh", SessionLauncher.Arguments(new Connection { Host = "server", User = "alice" })); broker.Configure(terminal, protocol);
            if (protocol == "SSH") Check(terminal.ArgumentList.Contains("StrictHostKeyChecking=yes") && !terminal.ArgumentList.Contains("StrictHostKeyChecking=ask"));
            var start = new System.Diagnostics.ProcessStartInfo(arguments[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in arguments.Skip(1)) start.ArgumentList.Add(arg);
            foreach (string key in new[] { "MGREMOTE_CREDENTIAL_SOCKET", "MGREMOTE_CREDENTIAL_TOKEN" }) start.Environment[key] = terminal.Environment[key];
            Check(!string.Join(" ", terminal.ArgumentList).Contains("fixture-password") && !terminal.Environment.Values.Contains("fixture-password"));
            return start;
        }
        async Task<(int Code, string Output)> Run(System.Diagnostics.ProcessStartInfo start)
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            try {
                string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                return (process.ExitCode, output);
            } catch { if (!process.HasExited) process.Kill(true); throw; }
        }
        var ssh = new Connection { Host = "server", User = "alice" };
        string path;
        using (var broker = new CredentialBroker(ssh, "fixture-password")) {
            var invalid = Setup(broker, "SSH", Path.Combine(AppContext.BaseDirectory, "mgremote-askpass"), "alice@server's password:");
            path = invalid.Environment["MGREMOTE_CREDENTIAL_SOCKET"]!;
            Check((File.GetUnixFileMode(Path.GetDirectoryName(path)!) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0);
            invalid.Environment["MGREMOTE_CREDENTIAL_TOKEN"] = new string('0', 64);
            var badToken = await Run(invalid); Check(badToken.Code != 0 && !badToken.Output.Contains("fixture-password"));
            var otherHost = await Run(Setup(broker, "SSH", binary, "--credential-askpass", "alice@bastion's password:")); Check(otherHost.Code != 0);
            var keyPhrase = await Run(Setup(broker, "SSH", binary, "--credential-askpass", "Enter passphrase for key '/private':")); Check(keyPhrase.Code != 0);
            var password = await Run(Setup(broker, "SSH", Path.Combine(AppContext.BaseDirectory, "mgremote-askpass"), "alice@server's password:")); Check(password.Code == 0 && password.Output.TrimEnd() == "fixture-password");
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(3)); Check(!File.Exists(path));
        }
        Check(!Directory.Exists(Path.GetDirectoryName(path)));
        using (var broker = new CredentialBroker(ssh with { Protocol = "RDP" }, "fixture-password")) {
            var delivered = await Run(Setup(broker, "RDP", binary, "--credential-rdp", binary, "--credential-stdin-fixture"));
            Check(delivered.Code == 0 && !delivered.Output.Contains("fixture-password"));
            await broker.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var cancelled = new CredentialBroker(ssh, "fixture-password"); cancelled.Dispose(); await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(3)); Check(true);
        return count;
    }

}
