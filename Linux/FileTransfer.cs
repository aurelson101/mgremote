// SFTP-backed transfers through the installed OpenSSH client. GPL-2.0.
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MGRemote.Linux;

public sealed record TransferPlan(string Executable, List<string> Arguments, string LocalTarget);

public static class FileTransfer
{
    public static List<string> Arguments(Connection connection, string local, string remote, bool download, bool recursive)
    {
        Connections.Validate(connection);
        if (connection.Protocol != "SSH") throw new ArgumentException("Le transfert nécessite une connexion SSH.");
        if (string.IsNullOrWhiteSpace(local) || !Path.IsPathFullyQualified(local)) throw new ArgumentException("Choisissez un chemin local absolu.");
        if (string.IsNullOrWhiteSpace(remote) || remote.Any(char.IsControl) || remote.IndexOfAny(['*', '?', '[', ']']) >= 0 ||
            !(remote.StartsWith('/') || Regex.IsMatch(remote, @"\A[A-Za-z]:/"))) throw new ArgumentException("Chemin distant absolu requis, sans caractères de contrôle ni jokers.");
        if (local.Any(char.IsControl)) throw new ArgumentException("Chemin local invalide.");
        var args = SessionLauncher.Arguments(connection, true); string host = args[^1]; args.RemoveAt(args.Count - 1);
        if (recursive) args.Add("-r");
        args.Add("--");
        string endpoint = host + ":" + remote;
        args.Add(download ? endpoint : Path.GetFullPath(local)); args.Add(download ? Path.GetFullPath(local) : endpoint);
        return args;
    }
    public static void ValidateUpload(string path, bool recursive, CancellationToken cancellation = default)
    {
        FileSystemInfo root = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!root.Exists) throw new IOException("Le fichier ou dossier local n’existe pas.");
        void Check(FileSystemInfo item) { cancellation.ThrowIfCancellationRequested(); if (item.LinkTarget != null || (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Liens symboliques/jonctions refusés pour l’envoi : " + item.Name); }
        Check(root);
        if (root is not DirectoryInfo directory) return;
        if (!recursive) throw new ArgumentException("Activez le mode dossier/récursif pour envoyer un dossier.");
        var stack = new Stack<IEnumerator<FileSystemInfo>>(); stack.Push(directory.EnumerateFileSystemInfos().GetEnumerator()); int entries = 0;
        try {
            while (stack.Count > 0) {
                cancellation.ThrowIfCancellationRequested();
                if (!stack.Peek().MoveNext()) { stack.Pop().Dispose(); continue; }
                var entry = stack.Peek().Current; Check(entry);
                if (++entries > 1000000) throw new IOException("Dossier trop volumineux : plus d’un million d’entrées.");
                if (entry is DirectoryInfo child) {
                    if (stack.Count >= 128) throw new IOException("Arborescence trop profonde.");
                    stack.Push(child.EnumerateFileSystemInfos().GetEnumerator());
                }
            }
        } finally { while (stack.Count > 0) stack.Pop().Dispose(); }
    }
    public static async Task<TransferPlan> Prepare(Connection connection, string local, string remote, bool download, bool recursive, CancellationToken cancellation)
    {
        _ = Arguments(connection, local, remote, download, recursive);
        SessionLauncher.ValidateIdentity(connection);
        string executable = SessionLauncher.Find("scp"), ssh = SessionLauncher.Find("ssh");
        using (var version = new Process { StartInfo = new ProcessStartInfo(ssh, "-V") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true } }) {
            version.Start(); var error = version.StandardError.ReadToEndAsync(cancellation); var output = version.StandardOutput.ReadToEndAsync(cancellation);
            try { await version.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(5), cancellation); }
            finally { if (!version.HasExited) version.Kill(entireProcessTree: true); }
            var match = Regex.Match(await error + await output, @"OpenSSH_(\d+)\.");
            if (!match.Success || int.Parse(match.Groups[1].Value) < 9) throw new IOException("OpenSSH 9 ou plus récent est requis pour utiliser SFTP sans ancien protocole SCP.");
        }
        cancellation.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(local);
        if (download) {
            if (!Directory.Exists(target) || new DirectoryInfo(target).LinkTarget != null) throw new IOException("Choisissez un dossier local existant, sans lien symbolique.");
            target = Path.Combine(target, "MGRemote-transfer-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        } else ValidateUpload(target, recursive, cancellation);
        return new TransferPlan(executable, Arguments(connection, target, remote, download, recursive), target);
    }
}
