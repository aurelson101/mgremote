// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace mRemoteNG.Security
{
    public sealed class TemporarySshKey : IDisposable
    {
        private readonly string directory;
        private readonly object disposeLock = new object();
        private FileStream lease;
        public string FilePath { get; private set; }

        private TemporarySshKey(string directory, FileStream lease)
        {
            this.directory = directory;
            this.lease = lease;
            FilePath = Path.Combine(directory, "key.ppk");
        }

        public static TemporarySshKey Create(string privateKey)
        {
            if (string.IsNullOrWhiteSpace(privateKey)) throw new ArgumentException("Empty SSH key.");
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MGRemote", "TemporaryKeys");
            DirectorySecurity security = new DirectorySecurity();
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            DirectoryInfo rootInfo = new DirectoryInfo(root);
            if (rootInfo.Exists && (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing a redirected SSH key directory.");
            rootInfo.Create();
            rootInfo.SetAccessControl(security);
            CleanupAbandoned(root, TimeSpan.FromDays(1));
            string directory = Path.Combine(root, "key-" + Guid.NewGuid().ToString("N"));
            new DirectoryInfo(directory).Create(security);
            FileStream lease = new FileStream(Path.Combine(directory, "lease"), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None);
            TemporarySshKey result = new TemporarySshKey(directory, lease);
            try
            {
                using (FileStream stream = new FileStream(result.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(privateKey);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        public static void CleanupAbandoned(string root, TimeSpan minimumAge)
        {
            foreach (string candidate in Directory.GetDirectories(root, "key-*", SearchOption.TopDirectoryOnly))
            {
                DirectoryInfo info = new DirectoryInfo(candidate);
                Guid id;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !Guid.TryParseExact(info.Name.Substring(4), "N", out id) ||
                    DateTime.UtcNow - info.LastWriteTimeUtc < minimumAge) continue;
                try
                {
                    // A live session holds this exclusive lease, even if it is more than a day old.
                    using (FileStream claim = new FileStream(Path.Combine(candidate, "lease"),
                        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        File.Delete(Path.Combine(candidate, "key.ppk"));
                    File.Delete(Path.Combine(candidate, "lease"));
                    Directory.Delete(candidate, false);
                }
                catch (IOException) { /* Active session or concurrent cleanup: leave it alone. */ }
                catch (UnauthorizedAccessException) { /* Never widen permissions to clean up. */ }
            }
        }

        public void Dispose()
        {
            lock (disposeLock)
            {
                if (lease == null) return;
                try { File.Delete(FilePath); }
                finally { lease.Dispose(); lease = null; }
                File.Delete(Path.Combine(directory, "lease"));
                Directory.Delete(directory, false);
            }
        }
    }
}
