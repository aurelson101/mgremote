// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace mRemoteNG.Security
{
    public static class WindowsSecurityPolicy
    {
        // Enable only after a dedicated, trusted MGRemote release channel is implemented.
        public static bool AllowAutomaticUpdates { get { return false; } }

        public static void ValidateRdp(int authenticationLevel, bool useCredSsp)
        {
            if (authenticationLevel != 1 || !useCredSsp)
                throw new InvalidOperationException(
                    "MGRemote requires RDP server authentication and NLA. Set Authentication to " +
                    "'Do not connect if authentication fails' and enable CredSSP in this connection or its parent folder.");
        }

        public static string BuildSshArguments(string rawHostname, string rawUsername, int port)
        {
            string hostname = (rawHostname ?? "").Trim();
            string username = (rawUsername ?? "").Trim();
            if (!IsSafeSshToken(hostname) || (username.Length > 0 && !IsSafeSshToken(username)))
                throw new ArgumentException("SSH hostname or username contains unsupported characters. Use a trusted OpenSSH config alias for complex profiles.");
            if (port < 1 || port > 65535)
                throw new ArgumentOutOfRangeException("port");

            // Options precede the destination and override weaker user-config defaults.
            // 'ask' permits first-use verification but refuses changed host keys.
            return "-o StrictHostKeyChecking=ask -o ForwardAgent=no -o PermitLocalCommand=no -o Compression=no -p " +
                port.ToString(CultureInfo.InvariantCulture) +
                (username.Length == 0 ? " " : " -l " + username + " ") + hostname;
        }

        public static string BuildOpenSshArguments(string hostname, string username, int port, string options)
        {
            string arguments = BuildSshArguments(hostname, username, port);
            if (string.IsNullOrWhiteSpace(options)) return arguments;
            var tokens = GetSafePuttyOptions(options);
            var identities = new List<string>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] != "-i" || i + 1 >= tokens.Length)
                    throw new ArgumentException("Windows OpenSSH connection options support only -i for private keys. Use an SSH profile for forwarding, bastions and other settings.");
                string path = Path.GetFullPath(tokens[++i]);
                SshProfileStore.ValidateExistingPrivateKey(path);
                identities.Add("-i \"" + path + "\"");
            }
            return string.Join(" ", identities) + " -o IdentitiesOnly=yes " + arguments;
        }

        public static string BuildSshTunnelArguments(string bastion, string user, int sshPort, string options, string destination, int destinationPort, int localPort)
        {
            if (!IsSafeSshToken(destination) || Uri.CheckHostName(destination.Trim('[', ']')) == UriHostNameType.Unknown ||
                destinationPort < 1 || destinationPort > 65535 || localPort < 1 || localPort > 65535)
                throw new ArgumentException("Destination ou port de tunnel SSH invalide.");
            string host = destination.Trim('[', ']');
            if (host.Contains(':')) host = "[" + host + "]";
            return "-N -T -o ExitOnForwardFailure=yes -o GatewayPorts=no -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -L 127.0.0.1:" +
                localPort.ToString(CultureInfo.InvariantCulture) + ":" + host + ":" + destinationPort.ToString(CultureInfo.InvariantCulture) + " " +
                BuildOpenSshArguments(bastion, user, sshPort, options);
        }

        public static string BuildTunneledSshArguments(string originalHost, string user, int originalPort, string options, int localPort, string hostKeyName)
        {
            if (!IsSafeSshToken(hostKeyName) || originalPort < 1 || originalPort > 65535) throw new ArgumentException("Nom ou port de serveur SSH invalide pour la vérification de clé d’hôte.");
            string alias = hostKeyName.Trim('[', ']');
            if (originalPort != 22) alias = "[" + alias + "]:" + originalPort.ToString(CultureInfo.InvariantCulture);
            return "-o HostName=127.0.0.1 -o HostKeyAlias=" + alias + " -o ProxyCommand=none -o ProxyJump=none " +
                BuildOpenSshArguments(originalHost, user, localPort, options);
        }

        public static ProcessStartInfo BuildOpenSshStartInfo(string hostname, string username, int port, string options)
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Installez la fonctionnalité Windows Client OpenSSH.");
            return new ProcessStartInfo(executable, BuildOpenSshArguments(hostname, username, port, options))
            {
                UseShellExecute = false, CreateNoWindow = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
        }

        public static bool IsSafeSshToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value[0] == '-') return false;
            foreach (char c in value)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                      (c >= '0' && c <= '9') || "._-:[]".IndexOf(c) >= 0)) return false;
            }
            return true;
        }

        public static string[] GetSafePuttyOptions(string rawOptions)
            => GetSafePuttyOptions(rawOptions, true);

        // The editor may inspect stale paths to replace/remove them. Launching always
        // uses the default overload, which requires the identity file to exist.
        public static string[] GetSafePuttyOptions(string rawOptions, bool validateIdentityFiles)
        {
            List<string> tokens = new List<string>();
            foreach (Match match in Regex.Matches(rawOptions ?? "", "\"[^\"]*\"|\\S+"))
            {
                string token = match.Value;
                if (token.StartsWith("\"") && token.EndsWith("\"")) token = token.Substring(1, token.Length - 2);
                if (token.IndexOf('"') >= 0 || token.IndexOf('\'') >= 0 || token.IndexOf('\r') >= 0 || token.IndexOf('\n') >= 0)
                    throw new ArgumentException("Invalid PuTTY option quoting.");
                tokens.Add(token);
            }
            for (int i = 0; i < tokens.Count; i++)
            {
                string option = tokens[i];
                if (option == "-N" || option == "-v" || option == "-agent" || option == "-a") continue;
                if (option != "-i" && option != "-L" && option != "-R" && option != "-D")
                    throw new ArgumentException("MGRemote allows only -i, -L, -R, -D, -N, -v, -agent and -a in PuTTY SSH options. Use OpenSSH config for other profiles.");
                if (++i >= tokens.Count || tokens[i].Length == 0 || tokens[i][0] == '-')
                    throw new ArgumentException("Missing PuTTY option value.");
                if (option == "-i")
                {
                    if (!Path.IsPathRooted(tokens[i]) || (validateIdentityFiles && !File.Exists(tokens[i])))
                        throw new ArgumentException("The SSH identity must be an existing absolute file path.");
                }
                else if (!Regex.IsMatch(tokens[i], "^[A-Za-z0-9:.\\[\\]_-]+$"))
                    throw new ArgumentException("Invalid SSH forwarding specification.");
            }
            return tokens.ToArray();
        }
    }
}
