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
            string link = Path.Combine(directory, "link"); File.CreateSymbolicLink(link, file);
            Reject(() => SessionLauncher.ValidateIdentityFile(link));
            string parent = Path.Combine(directory, "linked-parent"); Directory.CreateSymbolicLink(parent, directory);
            Reject(() => SessionLauncher.ValidateIdentityFile(Path.Combine(parent, "fixture.pem")));
            var connection = new Connection { Host = "fixture.example.invalid", CertificateFile = file };
            Write("ssh-ed25519-cert-v01@openssh.com AAAA fixture"); SessionLauncher.ValidateIdentity(connection); count++;
            Write(new string('x', 65537)); Reject(() => SessionLauncher.ValidateIdentity(connection));
            Write("ssh-ed25519-cert-v01@openssh.com AAAA fixture");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
            Reject(() => SessionLauncher.ValidateIdentity(connection));
            Reject(() => SessionLauncher.ValidateIdentity(connection with { CertificateFile = link }));
            Reject(() => SessionLauncher.ValidateIdentity(connection with { CertificateFile = Path.Combine(parent, "fixture.pem") }));
            Console.WriteLine($"PASS: {count} bounded SSH key/certificate checks, complete PEM armor, encrypted formats, permissions, linked files/directories; generated fixtures only.");
        } finally { Directory.Delete(directory, recursive: true); }
    }
}
