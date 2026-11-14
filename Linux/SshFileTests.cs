using System.Security.Cryptography;

namespace MGRemote.Linux;

internal static class SshFileTests
{
    internal static void Run()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-ssh-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string file = Path.Combine(directory, "fixture.pem");
        int count = 0;
        void Reject(Action action) { try { action(); } catch (IOException) { count++; return; } throw new Exception("Unsafe SSH file was accepted."); }
        void Write(string text) { File.WriteAllText(file, text); File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        try {
            using var rsa = RSA.Create(2048);
            string pem = rsa.ExportRSAPrivateKeyPem();
            foreach (string valid in new[] { pem, rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportEncryptedPkcs8PrivateKeyPem("synthetic fixture phrase", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)), pem.Replace("\n", "\r\n"), pem.Replace("-----BEGIN RSA PRIVATE KEY-----\n", "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-256-CBC,00000000000000000000000000000000\n\n") }) {
                Write(valid); SessionLauncher.ValidateIdentityFile(file); count++;
            }
            foreach (string invalid in new[] { "", "-----BEGIN OPENSSH PRIVATE KEY-----\ntruncated", pem.Replace("-----END RSA PRIVATE KEY-----", ""), pem.Replace("-----END RSA PRIVATE KEY-----", "-----END EC PRIVATE KEY-----"), pem.Replace(pem.Split('\n')[1], "!invalid!"), pem + "\n" + pem, rsa.ExportSubjectPublicKeyInfoPem(), new string('x', 1048577) }) {
                Write(invalid); Reject(() => SessionLauncher.ValidateIdentityFile(file));
            }
            Write(pem); File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Reject(() => SessionLauncher.ValidateIdentityFile(file)); Write(pem);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string shorthand = "~/" + Path.GetRelativePath(home, file);
            SessionLauncher.ValidateIdentity(new Connection { Host = "fixture.example.invalid", Identity = shorthand }); count++;
            foreach (bool sftp in new[] { false, true }) {
                var args = SessionLauncher.Arguments(new Connection { Host = "fixture.example.invalid", Identity = "~/.ssh/ia", CertificateFile = "~/.ssh/ia-cert.pub" }, sftp);
                if (args[args.IndexOf("-i") + 1] != Path.Combine(home, ".ssh", "ia") || !args.Contains("CertificateFile=\"" + Path.Combine(home, ".ssh", "ia-cert.pub") + "\"")) throw new Exception("Home shorthand reached the SSH/SFTP command without expansion.");
                count++;
            }
            var transfer = FileTransfer.Arguments(new Connection { Host = "fixture.example.invalid", Identity = "~/.ssh/ia" }, Path.Combine(directory, "upload"), "/remote/file", false, false);
            if (transfer[transfer.IndexOf("-i") + 1] != Path.Combine(home, ".ssh", "ia")) throw new Exception("Home shorthand reached file transfer without expansion.");
            count++;
            Reject(() => SessionLauncher.ValidateIdentityFile("relative-key"));
            string link = Path.Combine(directory, "link"); File.CreateSymbolicLink(link, file);
            SessionLauncher.ValidateIdentityFile(link); count++;
            string parent = Path.Combine(directory, "linked-parent"); Directory.CreateSymbolicLink(parent, directory);
            string throughParent = Path.Combine(parent, "fixture.pem");
            SessionLauncher.ValidateIdentityFile(throughParent); count++;
            string relative = Path.Combine(directory, "relative-link"); File.CreateSymbolicLink(relative, "fixture.pem");
            SessionLauncher.ValidateIdentityFile(relative); count++;
            Reject(() => ProfileEncryption.ReadProfileText(link));
            Reject(() => ProfileEncryption.ReadProfileText(throughParent));
            string broken = Path.Combine(directory, "broken"); File.CreateSymbolicLink(broken, "missing-key");
            Reject(() => SessionLauncher.ValidateIdentityFile(broken));
            string loop = Path.Combine(directory, "loop"); File.CreateSymbolicLink(loop, "loop");
            Reject(() => SessionLauncher.ValidateIdentityFile(loop));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            Reject(() => SessionLauncher.ValidateIdentityFile(link));
            Reject(() => SessionLauncher.ValidateIdentityFile(throughParent));
            Write("-----BEGIN OPENSSH PRIVATE KEY-----\ntruncated");
            Reject(() => SessionLauncher.ValidateIdentityFile(link));
            var connection = new Connection { Host = "fixture.example.invalid", CertificateFile = file };
            Write("ssh-ed25519-cert-v01@openssh.com AAAA fixture"); SessionLauncher.ValidateIdentity(connection); count++;
            SessionLauncher.ValidateIdentity(connection with { CertificateFile = shorthand }); count++;
            Write(new string('x', 65537)); Reject(() => SessionLauncher.ValidateIdentity(connection));
            Write("ssh-ed25519-cert-v01@openssh.com AAAA fixture");
            SessionLauncher.ValidateIdentity(connection with { CertificateFile = link }); count++;
            SessionLauncher.ValidateIdentity(connection with { CertificateFile = throughParent }); count++;
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            Reject(() => SessionLauncher.ValidateIdentity(connection));
            Reject(() => SessionLauncher.ValidateIdentity(connection with { CertificateFile = link }));
            Reject(() => SessionLauncher.ValidateIdentity(connection with { CertificateFile = Path.Combine(parent, "fixture.pem") }));
            Console.WriteLine($"PASS: {count} bounded SSH key/certificate checks, linked keys/folders accepted, unsafe targets/broken links/loops rejected, profiles still reject links; generated fixtures only.");
        } finally { Directory.Delete(directory, recursive: true); }
    }
}
