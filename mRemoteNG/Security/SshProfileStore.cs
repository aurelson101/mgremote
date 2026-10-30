// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace mRemoteNG.Security
{
    public static class SshProfileStore
    {
        public static string CreateProfile(string alias, string hostname, string username, int port,
            string identityFile, string certificateFile, string jumpHost, string sshDirectory)
        {
            if (!Regex.IsMatch(alias ?? "", "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\z"))
                throw new ArgumentException("Use a profile alias of 1-64 letters, digits, underscores or hyphens.");
            WindowsSecurityPolicy.BuildSshArguments(hostname, username, port);
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("An SSH username is required.");
            if (!string.IsNullOrEmpty(jumpHost) && !WindowsSecurityPolicy.IsSafeSshToken(jumpHost))
                throw new ArgumentException("The jump-host alias contains unsupported characters.");
            ConfigPath(sshDirectory);
            string profileDirectory = Path.Combine(sshDirectory, "mgremote");
            string profilePath = Path.Combine(profileDirectory, alias + ".conf");
            string configPath = Path.Combine(sshDirectory, "config");
            var text = new StringBuilder();
            text.AppendLine("Host " + alias);
            text.AppendLine("    HostName " + hostname);
            text.AppendLine("    User " + username);
            text.AppendLine("    Port " + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            text.AppendLine("    StrictHostKeyChecking ask");
            text.AppendLine("    ForwardAgent no");
            text.AppendLine("    Compression no");
            text.AppendLine("    PermitLocalCommand no");
            if (!string.IsNullOrWhiteSpace(identityFile))
            {
                ValidateExistingPrivateKey(identityFile);
                text.AppendLine("    IdentityFile \"" + ConfigPath(identityFile) + "\"");
                text.AppendLine("    IdentitiesOnly yes");
            }
            if (!string.IsNullOrWhiteSpace(certificateFile))
            {
                if (!File.Exists(certificateFile)) throw new FileNotFoundException("The SSH certificate does not exist.");
                text.AppendLine("    CertificateFile \"" + ConfigPath(certificateFile) + "\"");
            }
            if (!string.IsNullOrEmpty(jumpHost)) text.AppendLine("    ProxyJump " + jumpHost);
            Directory.CreateDirectory(profileDirectory);
            var utf8 = new UTF8Encoding(false);
            // The new profile is never allowed to replace an existing profile.
            using (var stream = new FileStream(profilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, utf8)) writer.Write(text.ToString());
            try
            {
                // Lock against concurrent writers; preserve the original bytes in a unique backup.
                using (var config = new FileStream(configPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
                {
                    byte[] original;
                    using (var snapshot = new MemoryStream()) { config.CopyTo(snapshot); original = snapshot.ToArray(); }
                    if (original.Length >= 2 && ((original[0] == 0xff && original[1] == 0xfe) || (original[0] == 0xfe && original[1] == 0xff)))
                        throw new IOException("OpenSSH config must use UTF-8 or ASCII, not UTF-16/32. The original file has not been changed.");
                    if (original.Length > 0)
                    {
                        string backupPath = configPath + ".mgremote-" + Guid.NewGuid().ToString("N") + ".bak";
                        using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { backup.Write(original, 0, original.Length); backup.Flush(true); }
                    }
                    byte[] include = utf8.GetBytes("Include \"" + ConfigPath(profilePath) + "\"\r\n");
                    int offset = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf ? 3 : 0;
                    try
                    {
                        config.Position = 0;
                        config.Write(include, 0, include.Length);
                        config.Write(original, offset, original.Length - offset);
                        config.SetLength(config.Position);
                        config.Flush(true);
                    }
                    catch
                    {
                        config.Position = 0;
                        config.Write(original, 0, original.Length);
                        config.SetLength(original.Length);
                        config.Flush(true);
                        throw;
                    }
                }
                return profilePath;
            }
            catch { File.Delete(profilePath); throw; }
        }

        public static void ValidateExistingPrivateKey(string path) => ValidateExistingPrivateKey(path, false);

        public static void ValidateExistingPrivateKey(string path, bool allowPutty)
        {
            ConfigPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("The OpenSSH private key does not exist.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Select a private-key file without filesystem redirection.");
            if (!allowPutty && string.Equals(Path.GetExtension(path), ".ppk", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("OpenSSH cannot use a PuTTY .ppk key. Convert it to OpenSSH format, or use the PuTTY backend.");
            // Inspect only the format marker, never load/log the key material or passphrase.
            var marker = new byte[80];
            string header;
            using (var stream = File.OpenRead(path))
            {
                int length = 0;
                while (length < marker.Length)
                {
                    int next = stream.ReadByte();
                    if (next < 0 || next == '\n') break;
                    marker[length++] = (byte)next;
                }
                header = Encoding.ASCII.GetString(marker, 0, length).TrimEnd('\r');
            }
            if (!(header.StartsWith("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal) ||
                header.StartsWith("-----BEGIN RSA PRIVATE KEY-----", StringComparison.Ordinal) ||
                header.StartsWith("-----BEGIN EC PRIVATE KEY-----", StringComparison.Ordinal) ||
                header.StartsWith("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) ||
                header.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal) ||
                (allowPutty && (header.StartsWith("PuTTY-User-Key-File-2:", StringComparison.Ordinal) || header.StartsWith("PuTTY-User-Key-File-3:", StringComparison.Ordinal)))))
                throw new ArgumentException("Select a private key in OpenSSH/PEM format, not its .pub public key.");
            string user = WindowsIdentity.GetCurrent().User.Value;
            var security = new FileInfo(path).GetAccessControl();
            string owner = security.GetOwner(typeof(SecurityIdentifier)).Value;
            if (owner != user && owner != "S-1-5-18" && owner != "S-1-5-32-544")
                throw new IOException("The private key belongs to another account. Select a key owned by your Windows account.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                string sid = rule.IdentityReference.Value;
                if (rule.AccessControlType == AccessControlType.Allow && sid != user && sid != "S-1-5-18" && sid != "S-1-5-32-544")
                    throw new IOException("The private key grants access to other accounts. Restrict its Windows permissions before using it; MGRemote does not alter existing keys.");
            }
        }

        public static void ValidateNewKeyPath(string path)
        {
            ConfigPath(path);
            if (File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".pub"))
                throw new IOException("The key or public-key path already exists. Existing keys are never replaced.");
            var parent = new DirectoryInfo(Path.GetDirectoryName(path));
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose an existing private directory without redirection.");
            string user = WindowsIdentity.GetCurrent().User.Value;
            foreach (FileSystemAccessRule rule in parent.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                string sid = rule.IdentityReference.Value;
                if (rule.AccessControlType == AccessControlType.Allow && sid != user && sid != "S-1-5-18" && sid != "S-1-5-32-544")
                    throw new IOException("The key directory grants access to other accounts. Choose a private directory.");
            }
        }

        private static string ConfigPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.IndexOfAny(new[] { '"', '\r', '\n', '\t', '%', '$', '`' }) >= 0)
                throw new ArgumentException("Use an absolute SSH path without quotes, control characters or expansion characters.");
            return path.Replace('\\', '/');
        }
    }
}
