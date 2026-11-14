// MGRemote modification. GPL-2.0; see COPYING.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace mRemoteNG.Security;

internal sealed record SftpEntry(string Name, string Path, bool Directory, bool Link, long Size, DateTime Modified);
internal sealed record SftpProgress(string File, long Bytes, long Total);

// One worker at a time owns the connection; neither listing nor transfers block WinForms.
internal sealed class SftpBrowserSession : IDisposable
{
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private readonly SftpClient _client;
    private readonly Renci.SshNet.ConnectionInfo _connection;
    private readonly List<PrivateKeyFile> _keys = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string Home => _client.WorkingDirectory;

    public SftpBrowserSession(string host, int port, string user, string password, string[] identities,
        Func<string, string> passphrase, Func<string, byte[], bool> trust)
    {
        try {
            var methods = new List<AuthenticationMethod>();
            foreach (string path in identities.Distinct(StringComparer.OrdinalIgnoreCase)) {
                SshProfileStore.ValidateExistingPrivateKey(path);
                PrivateKeyFile key;
                try { key = new PrivateKeyFile(path); }
                catch (SshPassPhraseNullOrEmptyException) { key = new PrivateKeyFile(path, passphrase(path) ?? throw new OperationCanceledException()); }
                _keys.Add(key);
            }
            if (_keys.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(user, _keys.ToArray()));
            if (password != null) methods.Add(new PasswordAuthenticationMethod(user, password));
            if (methods.Count == 0) throw new InvalidOperationException("Indiquez un mot de passe ou une clé SSH pour cette session SFTP.");
            _connection = new Renci.SshNet.ConnectionInfo(host, port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
            _client = new SftpClient(_connection) { OperationTimeout = TimeSpan.FromSeconds(20), KeepAliveInterval = TimeSpan.FromSeconds(30) };
            _client.HostKeyReceived += (_, e) => { e.CanTrust = false; e.CanTrust = trust(e.HostKeyName, e.HostKey); };
        } catch { Dispose(); throw; }
    }

    public Task ConnectAsync(CancellationToken token) => _client.ConnectAsync(token);
    private async Task<T> Work<T>(Func<T> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => { token.ThrowIfCancellationRequested(); return action(); }, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task<SftpEntry[]> ListAsync(string path, CancellationToken token) => Work(() => {
        ValidateRemoteDirectory(path);
        var entries = new List<SftpEntry>();
        foreach (var file in _client.ListDirectory(path)) {
            token.ThrowIfCancellationRequested();
            if (file.Name is "." or "..") continue;
            if (entries.Count >= 100000) throw new IOException("Ce dossier contient trop d’éléments. Ouvrez un sous-dossier plus précis.");
            entries.Add(new SftpEntry(file.Name, Join(path, file.Name), file.IsDirectory, file.IsSymbolicLink, file.Length, file.LastWriteTime));
        }
        return entries.OrderByDescending(x => x.Directory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }, token);

    public Task TransferAsync(string source, string destinationDirectory, bool upload, bool overwrite,
        IProgress<SftpProgress> progress, CancellationToken token) => Work(() => {
        int entries = 0;
        if (upload) {
            SshFileTransfer.ValidateUploadTree(source, token);
            ValidateRemoteDirectory(destinationDirectory);
            Upload(source, Join(destinationDirectory, Path.GetFileName(Path.TrimEndingDirectorySeparator(source))), 0);
        } else {
            ValidateLocalDirectory(destinationDirectory);
            ValidateRemoteDirectory(Parent(source));
            Download(source, destinationDirectory, 0);
        }
        return true;

        void Bound(int depth) { token.ThrowIfCancellationRequested(); if (depth > 128 || ++entries > 100000) throw new IOException("Arborescence trop volumineuse ou trop profonde pour ce transfert."); }
        void Upload(string local, string remote, int depth) {
            Bound(depth); ValidateLocalPath(local);
            var attributes = File.GetAttributes(local);
            if ((attributes & FileAttributes.Directory) != 0) {
                if (!_client.Exists(remote)) _client.CreateDirectory(remote);
                ValidateRemoteDirectory(remote);
                foreach (string child in Directory.EnumerateFileSystemEntries(local)) Upload(child, Join(remote, Path.GetFileName(child)), depth + 1);
                return;
            }
            if (_client.Exists(remote)) {
                var old = Inspect(remote);
                if (old.IsSymbolicLink || !old.IsRegularFile) throw new IOException("La destination distante n’est pas un fichier ordinaire : " + remote);
                if (!overwrite) throw new IOException("Le fichier existe déjà (remplacement désactivé) : " + remote);
            }
            string temporary = remote + ".mgremote-" + Guid.NewGuid().ToString("N") + ".part";
            string backup = null;
            try {
                using (var input = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    var report = Reporter(local, input.Length);
                    _client.UploadFileAsync(input, temporary, false, new InlineProgress<UploadFileProgressReport>(p => report((ulong)p.TotalBytesUploaded)), token).GetAwaiter().GetResult();
                }
                token.ThrowIfCancellationRequested();
                if (_client.Exists(remote)) {
                    var existing = Inspect(remote);
                    if (!overwrite || existing.IsSymbolicLink || !existing.IsRegularFile) throw new IOException("La destination distante existe ou a changé.");
                    backup = remote + ".mgremote-" + Guid.NewGuid().ToString("N") + ".backup";
                    _client.RenameFile(remote, backup);
                }
                try { _client.RenameFile(temporary, remote); }
                catch { if (backup != null) { _client.RenameFile(backup, remote); backup = null; } throw; }
                if (backup != null) { _client.DeleteFile(backup); backup = null; }
            } finally {
                try { if (_client.Exists(temporary)) _client.DeleteFile(temporary); } catch (Exception ex) when (ex is SshException or IOException) { /* Only this transfer's random staging file may remain. */ }
            }
        }
        void Download(string remote, string localDirectory, int depth) {
            Bound(depth);
            var file = Inspect(remote);
            if (file.IsSymbolicLink || (!file.IsDirectory && !file.IsRegularFile)) throw new IOException("Les liens et fichiers spéciaux distants ne sont pas téléchargés : " + remote);
            string local = LocalChild(localDirectory, file.Name);
            if (file.IsDirectory) {
                if (File.Exists(local)) throw new IOException("Un fichier local porte déjà le nom du dossier : " + local);
                if (Directory.Exists(local)) ValidateLocalDirectory(local);
                else { ValidateLocalDirectory(localDirectory); Directory.CreateDirectory(local); }
                foreach (var child in _client.ListDirectory(remote)) {
                    token.ThrowIfCancellationRequested();
                    if (child.Name is not "." and not "..") Download(Join(remote, child.Name), local, depth + 1);
                }
                return;
            }
            ValidateLocalDirectory(localDirectory);
            if (Directory.Exists(local)) throw new IOException("Un dossier local porte déjà ce nom : " + local);
            if (File.Exists(local)) { ValidateLocalPath(local); if (!overwrite) throw new IOException("Le fichier existe déjà (remplacement désactivé) : " + local); }
            string temporary = Path.Combine(localDirectory, ".mgremote-" + Guid.NewGuid().ToString("N") + ".part");
            try {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var report = Reporter(remote, file.Length);
                    _client.DownloadFileAsync(remote, output, new InlineProgress<DownloadFileProgressReport>(p => report((ulong)p.TotalBytesDownloaded)), token).GetAwaiter().GetResult();
                }
                token.ThrowIfCancellationRequested();
                ValidateLocalDirectory(localDirectory);
                if (File.Exists(local)) ValidateLocalPath(local);
                File.Move(temporary, local, overwrite);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Action<ulong> Reporter(string path, long length) {
            var clock = Stopwatch.StartNew(); progress?.Report(new SftpProgress(path, 0, length));
            return count => { lock (clock) { if (clock.ElapsedMilliseconds >= 100 || count == (ulong)length) { progress?.Report(new SftpProgress(path, (long)Math.Min(count, (ulong)long.MaxValue), length)); clock.Restart(); } } };
        }
    }, token);

    private void ValidateRemoteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Contains('\\') ||
            !(path.StartsWith('/') || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1..3] == ":/") ||
            path.Split('/').Any(x => x is "." or "..")) throw new IOException("Choisissez un chemin distant absolu sans segments . ou ...");
        string current = path;
        while (!string.IsNullOrEmpty(current) && current != ".") {
            var file = Inspect(current);
            if (!file.IsDirectory || file.IsSymbolicLink) throw new IOException("Choisissez un dossier distant sans lien symbolique : " + current);
            string parent = Parent(current); if (parent == current) break; current = parent;
        }
    }
    private ISftpFile Inspect(string path)
    {
        string parent = Parent(path);
        if (parent == path) return _client.Get(path);
        string name = path.TrimEnd('/').Split('/').Last();
        return _client.ListDirectory(parent).FirstOrDefault(x => x.Name == name) ?? throw new IOException("L’élément distant n’existe plus : " + path);
    }
    internal static string Join(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw new IOException("Nom distant non valide.");
        return directory.TrimEnd('/') + "/" + name;
    }
    internal static string Parent(string path) { path = path.TrimEnd('/'); if (path.Length == 2 && path[1] == ':') return path + "/"; int i = path.LastIndexOf('/'); return i <= 0 ? "/" : i == 2 && path[1] == ':' ? path[..3] : path[..i]; }
    internal static string LocalChild(string directory, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl))
            throw new IOException("Nom distant incompatible avec Windows : " + name);
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
            throw new IOException("Nom de périphérique Windows refusé : " + name);
        string root = Path.GetFullPath(directory);
        string result = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(result), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Le nom distant sort du dossier choisi.");
        return result;
    }
    internal static void ValidateLocalDirectory(string path) { ValidateLocalPath(path); if (!Directory.Exists(path)) throw new IOException("Le dossier local n’existe pas."); }
    internal static void ValidateLocalPath(string path)
    {
        string current = Path.GetFullPath(path);
        while (current != null) {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Les liens et jonctions ne sont pas suivis : " + current);
            current = Path.GetDirectoryName(current);
        }
    }
    public void Dispose() { _client?.Dispose(); foreach (var method in _connection?.AuthenticationMethods ?? Array.Empty<AuthenticationMethod>()) method.Dispose(); foreach (var key in _keys) key.Dispose(); }
}
