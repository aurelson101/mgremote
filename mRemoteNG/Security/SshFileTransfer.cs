// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace mRemoteNG.Security;

public static class SshFileTransfer
{
    public static void ValidateUploadTree(string path, CancellationToken cancellationToken)
    {
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(path));
        while (pending.Count > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            string item = pending.Pop();
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Les liens/jonctions ne sont pas suivis lors d’un envoi. Choisissez leur cible explicitement.");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(item)) pending.Push(child);
        }
    }

    public static ProcessStartInfo BuildStartInfo(string hostname, string username, int port, string sshOptions,
        string localPath, string remotePath, bool upload, bool recursive, int tunnelPort = 0, string hostKeyName = null)
    {
        WindowsSecurityPolicy.BuildOpenSshArguments(hostname, username, port, sshOptions);
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "scp.exe");
        if (!File.Exists(executable) || FileVersionInfo.GetVersionInfo(executable).FileMajorPart < 9)
            throw new InvalidOperationException("Le transfert nécessite le client Windows OpenSSH 9 ou ultérieur (SFTP). Aucun ancien mode SCP n’est activé.");
        localPath = Path.GetFullPath(localPath ?? string.Empty);
        if (upload ? !File.Exists(localPath) && !Directory.Exists(localPath) : !Directory.Exists(localPath))
            throw new ArgumentException("Choisissez un fichier/dossier local existant.");
        if ((File.GetAttributes(localPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Choisissez un chemin local sans lien ni jonction.");
        if (upload && Directory.Exists(localPath) && !recursive) throw new ArgumentException("Activez le transfert de dossier pour envoyer ce répertoire.");
        remotePath = (remotePath ?? string.Empty).Replace('\\', '/');
        bool absolute = remotePath.StartsWith('/') || remotePath.Length > 2 && char.IsAsciiLetter(remotePath[0]) && remotePath[1] == ':' && remotePath[2] == '/';
        if (!absolute || remotePath.Any(char.IsControl) || remotePath.IndexOfAny(new[] { '*', '?', '[', ']' }) >= 0)
            throw new ArgumentException("Indiquez un chemin distant absolu sans caractères de contrôle ni jokers (exemple : /home/user/Documents ou C:/Users/user/Documents).");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        void Option(string option) { start.ArgumentList.Add("-o"); start.ArgumentList.Add(option); }
        foreach (string option in new[] { "StrictHostKeyChecking=ask", "ForwardAgent=no", "PermitLocalCommand=no", "Compression=no" }) Option(option);
        var tokens = WindowsSecurityPolicy.GetSafePuttyOptions(sshOptions);
        for (int i = 0; i < tokens.Length; i++) {
            if (tokens[i] != "-i") throw new ArgumentException("Pour SFTP, utilisez uniquement une clé -i ou un profil OpenSSH.");
            start.ArgumentList.Add("-i"); start.ArgumentList.Add(Path.GetFullPath(tokens[++i]));
        }
        if (tokens.Length > 0) Option("IdentitiesOnly=yes");
        if (tunnelPort != 0) {
            WindowsSecurityPolicy.BuildTunneledSshArguments(hostname, username, port, sshOptions, tunnelPort, hostKeyName);
            Option("HostName=127.0.0.1");
            Option("HostKeyAlias=" + (port == 22 ? hostKeyName.Trim('[', ']') : "[" + hostKeyName.Trim('[', ']') + "]:" + port));
            Option("ProxyCommand=none"); Option("ProxyJump=none");
        }
        start.ArgumentList.Add("-P"); start.ArgumentList.Add((tunnelPort == 0 ? port : tunnelPort).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (recursive) start.ArgumentList.Add("-r");
        start.ArgumentList.Add("--");
        string host = hostname.Trim('[', ']'); if (host.Contains(':')) host = "[" + host + "]";
        string remote = (string.IsNullOrEmpty(username) ? "" : username + "@") + host + ":" + remotePath;
        string local = localPath.Replace('\\', '/');
        start.ArgumentList.Add(upload ? local : remote); start.ArgumentList.Add(upload ? remote : local);
        return start;
    }
}
