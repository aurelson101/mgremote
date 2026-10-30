using System;
using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.Messages;
using mRemoteNG.Resources.Language;

namespace mRemoteNG.Connection.Protocol.Terminal
{
    [SupportedOSPlatform("windows")]
    public class ProtocolTerminal(ConnectionInfo connectionInfo, bool forceSsh = false) : ProtocolBase
    {
        #region Private Fields

        private IntPtr _handle;
        private readonly ConnectionInfo _connectionInfo = connectionInfo;
        private readonly bool _forceSsh = forceSsh;
        private mRemoteNG.UI.Controls.SshTerminal _consoleControl;

        #endregion

        #region Public Methods

        public override bool Connect()
        {
            try
            {
                Runtime.MessageCollector?.AddMessage(MessageClass.InformationMsg, "Attempting to start Terminal session.", true);

                string hostname = _connectionInfo.Hostname.Trim();
                bool useLocalHost = !_forceSsh && (hostname.Length == 0 || hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase));

                string processExe;
                string arguments;

                if (!useLocalHost)
                {
                    // Remote session: launch the OpenSSH client (ssh.exe) DIRECTLY.
                    //
                    // The previous implementation ran "cmd.exe /K ssh <host>", concatenating the
                    // attacker-controllable Hostname/Username connection fields into a string that
                    // cmd.exe then re-parsed. That allowed command injection through shell
                    // metacharacters (& | < > ^) — see issue #3335. Invoking ssh.exe directly means
                    // no shell interprets the arguments, and BuildSshArguments rejects any value that
                    // could be mis-parsed as an additional ssh argument.
                    string sshExe = FindSshExe();
                    if (sshExe == null)
                    {
                        Runtime.MessageCollector?.AddMessage(MessageClass.ErrorMsg,
                            "Windows OpenSSH client (ssh.exe) was not found. " +
                            "Please install the OpenSSH Client optional feature via Settings > Apps > Optional Features.", true);
                        return false;
                    }

                    processExe = sshExe;
                    arguments = mRemoteNG.Security.WindowsSecurityPolicy.BuildOpenSshArguments(
                        _connectionInfo.Hostname, _connectionInfo.Username, _connectionInfo.Port, _connectionInfo.SSHOptions);
                    if (InterfaceControl.SSHTunnelInfo != null && InterfaceControl.OriginalInfo is ConnectionInfo original)
                        arguments = mRemoteNG.Security.WindowsSecurityPolicy.BuildTunneledSshArguments(original.Hostname, original.Username,
                            original.Port, original.SSHOptions, _connectionInfo.Port, InterfaceControl.SshTunnelHostKeyName ?? original.Hostname);
                    arguments = "-tt " + arguments;
                }
                else
                {
                    // Local session: open the system command processor. No user-controlled input is
                    // passed, so there is nothing to inject.
                    processExe = Environment.GetEnvironmentVariable("COMSPEC") ?? @"C:\Windows\System32\cmd.exe";
                    arguments = "/K";
                }

                _consoleControl = new mRemoteNG.UI.Controls.SshTerminal
                {
                    Dock = DockStyle.Fill, BackColor = ColorTranslator.FromHtml("#181a1f"),
                    ForeColor = Color.White
                };
                Control = _consoleControl;
                InterfaceControl.Controls.Add(_consoleControl);
                _handle = _consoleControl.Handle;
                _consoleControl.StartProcess(processExe, arguments);

                Resize(this, new EventArgs());
                base.Connect();
                return true;
            }
            catch (Exception ex)
            {
                ReleaseConsole();
                Runtime.MessageCollector?.AddExceptionMessage(Language.ConnectionFailed, ex);
                return false;
            }
        }

        private void ReleaseConsole()
        {
            var console = _consoleControl;
            _consoleControl = null;
            Control = null;
            _handle = IntPtr.Zero;
            if (console == null) return;
            try { if (console.IsProcessRunning) console.StopProcess(); }
            finally { console.Dispose(); }
        }

        public override void Close()
        {
            var console = _consoleControl;
            if (console != null && !console.IsDisposed && console.IsHandleCreated && console.InvokeRequired)
            {
                try { console.BeginInvoke(new Action(Close)); }
                catch (InvalidOperationException) { base.Close(); }
                return;
            }
            try { ReleaseConsole(); }
            finally { base.Close(); }
        }

        public override void Focus()
        {
            try
            {
                _consoleControl?.FocusInput();
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(Language.IntAppFocusFailed, ex);
            }
        }

        protected override void Resize(object sender, EventArgs e)
        {
            try
            {
                if (InterfaceControl.Size == Size.Empty) return;
                if (_consoleControl != null && !_consoleControl.IsDisposed)
                    _consoleControl.Bounds = InterfaceControl.DisplayRectangle;
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionMessage(Language.IntAppResizeFailed, ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Builds the argument string passed to ssh.exe. The hostname and username come straight from
        /// the (potentially malicious) connections file, so both are validated: any value containing
        /// whitespace/control characters or starting with '-' is rejected, because ssh.exe would parse
        /// such a value as one or more additional arguments (e.g. -oProxyCommand=...) rather than as the
        /// target. ssh.exe is launched directly with no intervening shell, so shell metacharacters
        /// (&amp; | &lt; &gt; ^) carry no special meaning and cannot execute commands.
        /// </summary>
        private static string BuildSshArguments(string rawHostname, string rawUsername, int port)
        {
            return mRemoteNG.Security.WindowsSecurityPolicy.BuildSshArguments(rawHostname, rawUsername, port);
        }

        /// <summary>
        /// Returns true only for values that are safe to place on the ssh.exe command line as a single
        /// token: non-empty, no whitespace or control characters, no double quotes, and not starting
        /// with '-' (which ssh would treat as an option switch). Double quotes are rejected because
        /// Windows argument parsing (CommandLineToArgvW) strips them, so a value such as
        /// "-oProxyCommand=..." does not literally start with '-' here yet reaches ssh.exe as an argv
        /// token that does — re-enabling option injection.
        /// </summary>
        private static bool IsSafeSshToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            if (value[0] == '-')
                return false;

            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c) || c == '"')
                    return false;
            }

            return true;
        }

        private static string FindSshExe()
        {
            // Try the standard Windows OpenSSH location first
            string systemSsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "OpenSSH", "ssh.exe");

            if (File.Exists(systemSsh))
                return systemSsh;

            // Do not load an untrusted executable from a user-writable PATH directory.
            return null;
        }

        #endregion

        #region Enumerations

        public enum Defaults
        {
            Port = 22
        }

        #endregion
    }
}
// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
