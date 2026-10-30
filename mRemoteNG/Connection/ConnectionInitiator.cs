using System;
using System.Collections.Generic;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Container;
using mRemoteNG.Messages;
using mRemoteNG.Properties;
using mRemoteNG.UI.Forms;
using mRemoteNG.UI.Panels;
using mRemoteNG.UI.Tabs;
using mRemoteNG.UI.Window;
using WeifenLuo.WinFormsUI.Docking;
using mRemoteNG.Resources.Language;
using System.Runtime.Versioning;
using mRemoteNG.Connection.Protocol.Terminal;
using System.Threading;
using System.Linq;

namespace mRemoteNG.Connection
{
    [SupportedOSPlatform("windows")]
    public class ConnectionInitiator : IConnectionInitiator
    {
        private readonly PanelAdder _panelAdder = new();
        private readonly List<string> _activeConnections = [];

        public IEnumerable<string> ActiveConnections => _activeConnections;

        public bool SwitchToOpenConnection(ConnectionInfo connectionInfo)
        {
            InterfaceControl interfaceControl = FindConnectionContainer(connectionInfo);
            if (interfaceControl == null) return false;
            ConnectionTab connT = (ConnectionTab)interfaceControl.FindForm();
            connT?.Focus();
            ConnectionTab findForm = (ConnectionTab)interfaceControl.FindForm();
            findForm?.Show(findForm.DockPanel);
            return true;
        }

        public void OpenConnection(
            ContainerInfo containerInfo,
            ConnectionInfo.Force force = ConnectionInfo.Force.None,
            ConnectionWindow conForm = null,
            DockPane targetPane = null,
            int? targetContentIndex = null)
        {
            if (containerInfo == null || containerInfo.Children.Count == 0)
                return;

            foreach (ConnectionInfo child in containerInfo.Children)
            {
                if (child is ContainerInfo childAsContainer)
                    OpenConnection(childAsContainer, force, conForm, targetPane, targetContentIndex);
                else
                    OpenConnection(child, force, conForm, targetPane, targetContentIndex);
            }
        }

        // async is necessary so UI can update while OpenConnection waits for tunnel connection to get ready in case of connection through SSH tunnel
        public async void OpenConnection(
            ConnectionInfo connectionInfo,
            ConnectionInfo.Force force = ConnectionInfo.Force.None,
            ConnectionWindow conForm = null,
            DockPane targetPane = null,
            int? targetContentIndex = null,
            DockContent? targetPanePlaceholder = null)
        {
            if (connectionInfo == null)
                return;

            OpenSshTunnel pendingTunnel = null;
            try
            {
                await Runtime.PluginService.ResolveConnectionAddressAsync(connectionInfo);

                if (string.IsNullOrEmpty(connectionInfo.Hostname))
                {
                    if (!ProtocolFeature.SupportBlankHostname(connectionInfo.Protocol))
                    {
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, Language.ConnectionOpenFailedNoHostname);
                        ShowConnectionError(Language.ConnectionOpenFailedNoHostname);
                        return;
                    }

                    if (string.IsNullOrEmpty(connectionInfo.Name))
                    {
                        connectionInfo.Name = "localhost";
                    }
                }

                StartPreConnectionExternalApp(connectionInfo);

                if (!force.HasFlag(ConnectionInfo.Force.DoNotJump))
                {
                    if (SwitchToOpenConnection(connectionInfo))
                        return;
                }

                ProtocolFactory protocolFactory = new();
                string connectionPanel = SetConnectionPanel(connectionInfo, force);
                if (string.IsNullOrEmpty(connectionPanel)) return;
                ConnectionWindow connectionForm = SetConnectionForm(conForm, connectionPanel);
                Control connectionContainer = null;

                // Handle connection through SSH tunnel:
                // in case of connection through SSH tunnel, connectionInfo gets cloned, so that modification of its name, hostname and port do not modify the original connection info
                // connectionInfoOriginal points to the original connection info in either case, for where its needed later on.
                ConnectionInfo connectionInfoOriginal = connectionInfo;
                ConnectionInfo connectionInfoSshTunnel = null; // SSH tunnel connection info will be set if SSH tunnel connection is configured, can be found and connected.
                string sshTunnelHostKeyName = null;
                if (!string.IsNullOrEmpty(connectionInfoOriginal.SSHTunnelConnectionName))
                {
                    // Find the connection info specified as SSH tunnel in the connections tree
                    connectionInfoSshTunnel = getSSHConnectionInfoByName(Runtime.ConnectionsService.ConnectionTreeModel.RootNodes, connectionInfoOriginal.SSHTunnelConnectionName);
                    if (connectionInfoSshTunnel == null)
                    {
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, string.Format(Language.SshTunnelConfigProblem, connectionInfoOriginal.Name, connectionInfoOriginal.SSHTunnelConnectionName));
                        return;
                    }
                    if (connectionInfoSshTunnel.ConstantID == connectionInfoOriginal.ConstantID || !string.IsNullOrEmpty(connectionInfoSshTunnel.SSHTunnelConnectionName))
                        throw new InvalidOperationException("Choisissez un bastion SSH distinct, sans tunnel imbriqué. Pour plusieurs sauts, utilisez un profil OpenSSH ProxyJump.");
                    if (connectionInfoSshTunnel.Protocol == ProtocolType.SSH2)
                    {
                        if (connectionInfoOriginal.Protocol is not (ProtocolType.RDP or ProtocolType.SSH2 or ProtocolType.VNC))
                            throw new InvalidOperationException("Le tunnel OpenSSH intégré prend en charge SSH2, RDP et VNC.");
                        string destination = connectionInfoOriginal.Hostname;
                        using var startupCancellation = new CancellationTokenSource();
                        EventHandler cancel = (_, _) => startupCancellation.Cancel();
                        FormClosedEventHandler cancelMain = (_, _) => startupCancellation.Cancel();
                        connectionForm.Disposed += cancel;
                        FrmMain.Default.FormClosed += cancelMain;
                        try {
                            if (connectionInfoOriginal.Protocol == ProtocolType.SSH2)
                                destination = await OpenSshTunnel.ResolveSshHostnameAsync(connectionInfoOriginal, startupCancellation.Token);
                            pendingTunnel = await OpenSshTunnel.StartAsync(connectionInfoSshTunnel, destination, connectionInfoOriginal.Port, startupCancellation.Token);
                        }
                        finally { connectionForm.Disposed -= cancel; FrmMain.Default.FormClosed -= cancelMain; }
                        if (connectionForm.IsDisposed || FrmMain.Default.IsClosing) return;
                        sshTunnelHostKeyName = destination;
                        connectionInfo = connectionInfoOriginal.Clone();
                        connectionInfo.Inheritance.Port = false;
                        connectionInfo.Name += " via " + connectionInfoSshTunnel.Name;
                        connectionInfo.Hostname = "127.0.0.1";
                        connectionInfo.Port = pendingTunnel.LocalPort;
                    }
                    else
                    {
                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        $"SSH Tunnel connection '{connectionInfoOriginal.SSHTunnelConnectionName}' configured for '{connectionInfoOriginal.Name}' found. Finding free local port for use as local tunnel port ...");
                    // determine a free local port to use as local tunnel port
                    System.Net.Sockets.TcpListener l = new(System.Net.IPAddress.Loopback, 0);
                    l.Start();
                    int localSshTunnelPort = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
                    l.Stop();
                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        $"{localSshTunnelPort} will be used as local tunnel port. Establishing SSH connection to '{connectionInfoSshTunnel.Hostname}' with additional tunnel options for target connection ...");

                    // clone SSH tunnel connection as tunnel options will be added to it, and those changes shall not be saved to the configuration
                    connectionInfoSshTunnel = connectionInfoSshTunnel.Clone();
                    connectionInfoSshTunnel.SSHOptions += " -L " + localSshTunnelPort + ":" + connectionInfoOriginal.Hostname + ":" + connectionInfoOriginal.Port;

                    // clone target connection info as its hostname will be changed to localhost and port to local tunnel port to establish connection through tunnel, and those changes shall not be saved to the configuration
                    connectionInfo = connectionInfoOriginal.Clone();
                    connectionInfo.Name += " via " + connectionInfoSshTunnel.Name;
                    connectionInfo.Hostname = "localhost";
                    connectionInfo.Port = localSshTunnelPort;

                    // connect the SSH connection to setup the tunnel
                    ProtocolBase protocolSshTunnel = protocolFactory.CreateProtocol(connectionInfoSshTunnel);
                    if (!(protocolSshTunnel is PuttyBase puttyBaseSshTunnel))
                    {
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                            string.Format(Language.SshTunnelIsNotPutty, connectionInfoOriginal.Name, connectionInfoSshTunnel.Name));
                        return;
                    }

                    SetConnectionFormEventHandlers(protocolSshTunnel, connectionForm);
                    SetConnectionEventHandlers(protocolSshTunnel);
                    connectionContainer = SetConnectionContainer(connectionInfo, connectionForm, targetPane, targetContentIndex);
                    BuildConnectionInterfaceController(connectionInfoSshTunnel, protocolSshTunnel, connectionContainer);
                    protocolSshTunnel.InterfaceControl.OriginalInfo = connectionInfoSshTunnel;

                    if (protocolSshTunnel.Initialize() == false)
                    {
                        protocolSshTunnel.Close();
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                            string.Format(Language.SshTunnelNotInitialized, connectionInfoOriginal.Name, connectionInfoSshTunnel.Name));
                        return;
                    }

                    if (protocolSshTunnel.Connect() == false)
                    {
                        protocolSshTunnel.Close();
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                            string.Format(Language.SshTunnelNotConnected, connectionInfoOriginal.Name, connectionInfoSshTunnel.Name));
                        return;
                    }

                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        "Putty started for SSH connection for tunnel. Waiting for local tunnel port to become available ...");

                    // wait until SSH tunnel connection is ready, by checking if local port can be connected to, but max 60 sec.
                    System.Net.Sockets.Socket testsock = new(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                    System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    while (stopwatch.ElapsedMilliseconds < 60000)
                    {
                        // confirm that SSH connection is still active
                        // works only if putty is connfigured to always close window on exit
                        // else, if connection attempt fails, window remains open and putty process remains running, and we cannot know that connection is already doomed
                        // in this case the timeout will expire and the log message below will be created
                        // awkward for user as he has already acknowledged the putty popup some seconds again when the below notification comes....
                        if (!puttyBaseSshTunnel.isRunning())
                        {
                            protocolSshTunnel.Close();
                            Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                                string.Format(Language.SshTunnelFailed, connectionInfoOriginal.Name, connectionInfoSshTunnel.Name));
                            return;
                        }

                        try
                        {
                            testsock.Connect(System.Net.IPAddress.Loopback, localSshTunnelPort);
                            testsock.Close();
                            break;
                        }
                        catch
                        {
                            await System.Threading.Tasks.Task.Delay(1000);
                        }
                    }

                    if (stopwatch.ElapsedMilliseconds >= 60000)
                    {
                        protocolSshTunnel.Close();
                        Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg,
                            string.Format(Language.SshTunnelPortNotReadyInTime, connectionInfoOriginal.Name, connectionInfoSshTunnel.Name));
                        return;
                    }

                    Runtime.MessageCollector.AddMessage(MessageClass.DebugMsg,
                        "Local tunnel port is now available. Hiding putty display and setting up target connection via local tunnel port ...");

                    // hide the display of the SSH tunnel connection which has been shown until this time, such that password can be entered if required or errors be seen
                    // it stays invisible in the container however which will be reused for the actual connection and such that if the container is closed the SSH tunnel connection is closed as well
                    protocolSshTunnel.InterfaceControl.Hide();
                    }
                }

                ProtocolBase newProtocol = protocolFactory.CreateProtocol(connectionInfo);
                SetConnectionFormEventHandlers(newProtocol, connectionForm);
                SetConnectionEventHandlers(newProtocol);
                // in case of connection through SSH tunnel the container is already defined and must be use, else it needs to be created here
                if (connectionContainer == null) connectionContainer = SetConnectionContainer(connectionInfo, connectionForm, targetPane, targetContentIndex);
                BuildConnectionInterfaceController(connectionInfo, newProtocol, connectionContainer);
                if (pendingTunnel != null)
                {
                    var ownedTunnel = pendingTunnel;
                    connectionContainer.Disposed += (_, _) => ownedTunnel.Dispose();
                    newProtocol.Closed += _ => ownedTunnel.Dispose();
                }
                // in case of connection through SSH tunnel the connectionInfo was modified but connectionInfoOriginal in all cases retains the original info
                // and is stored in interface control for further use
                newProtocol.InterfaceControl.OriginalInfo = connectionInfoOriginal;
                // SSH tunnel connection is stored in Interface Control to be used in log messages etc
                newProtocol.InterfaceControl.SSHTunnelInfo = connectionInfoSshTunnel;
                newProtocol.InterfaceControl.SshTunnelHostKeyName = sshTunnelHostKeyName;

                newProtocol.Force = force;

                if (newProtocol.Initialize() == false)
                {
                    newProtocol.Close();
                    ShowConnectionError("Impossible d’initialiser cette connexion. Consultez le panneau Notifications pour le détail de l’erreur.");
                    return;
                }

                if (newProtocol.Connect() == false)
                {
                    newProtocol.Close();
                    ShowConnectionError("Impossible de démarrer cette connexion. Consultez le panneau Notifications pour le détail de l’erreur.");
                    return;
                }

                connectionInfoOriginal.OpenConnections.Add(newProtocol);
                _activeConnections.Add(connectionInfo.ConstantID);
                FrmMain.Default.SelectedConnection = connectionInfo;
                pendingTunnel = null; // The protocol/container now own its lifetime.
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.ConnectionOpenFailed, ex);
                ShowConnectionError("Impossible d’ouvrir la connexion : " + ex.Message);
            }
            finally
            {
                pendingTunnel?.Dispose();
                if (targetPanePlaceholder is { IsDisposed: false })
                    targetPanePlaceholder.Close();
            }
        }

        // recursively traverse the tree to find ConnectionInfo of a specific name
        internal static ConnectionInfo getSSHConnectionInfoByName(IEnumerable<ConnectionInfo> rootnodes, string SSHTunnelConnectionName)
        {
            static IEnumerable<ConnectionInfo> Enumerate(IEnumerable<ConnectionInfo> nodes) {
                foreach (var node in nodes) {
                    if (node is ContainerInfo container) { foreach (var child in Enumerate(container.Children)) yield return child; }
                    else yield return node;
                }
            }
            var matches = Enumerate(rootnodes).Where(node => node.Name == SSHTunnelConnectionName && node.Protocol == ProtocolType.SSH2).Take(2).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Plusieurs bastions SSH portent ce nom. Renommez-les pour sélectionner un bastion sans ambiguïté.");
            return matches.FirstOrDefault();
        }

        #region Private
        private static void StartPreConnectionExternalApp(ConnectionInfo connectionInfo)
        {
            if (connectionInfo.PreExtApp == "") return;
            Tools.ExternalTool extA = Runtime.ExternalToolsService.GetExtAppByName(connectionInfo.PreExtApp);
            extA?.Start(connectionInfo);
        }

        private static InterfaceControl FindConnectionContainer(ConnectionInfo connectionInfo)
        {
            if (connectionInfo.OpenConnections.Count <= 0) return null;
            for (int i = 0; i <= Runtime.WindowList.Count - 1; i++)
            {
                // the new structure is ConnectionWindow.Controls[0].ActiveDocument.Controls[0]
                //                                       DockPanel                  InterfaceControl
                if (!(Runtime.WindowList[i] is ConnectionWindow connectionWindow)) continue;
                if (connectionWindow.Controls.Count < 1) continue;
                if (!(connectionWindow.Controls[0] is DockPanel cwDp)) continue;
                foreach (IDockContent dockContent in cwDp.Documents)
                {
                    ConnectionTab tab = (ConnectionTab)dockContent;
                    InterfaceControl ic = InterfaceControl.FindInterfaceControl(tab);
                    if (ic == null) continue;
                    if (ic.Info == connectionInfo || ic.OriginalInfo == connectionInfo)
                        return ic;
                }
            }

            return null;
        }

        private static void ShowConnectionError(string message)
        {
            var owner = Application.OpenForms.OfType<FrmMain>().FirstOrDefault();
            if (owner == null || owner.IsDisposed || owner.Disposing) return;
            MessageBox.Show(owner, message, "MGRemote — Connexion", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private static string SetConnectionPanel(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            if (connectionInfo.Panel != "" && !force.HasFlag(ConnectionInfo.Force.OverridePanel) && !Properties.OptionsTabsPanelsPage.Default.AlwaysShowPanelSelectionDlg)
                return connectionInfo.Panel;

            if (!Properties.OptionsTabsPanelsPage.Default.AlwaysShowPanelSelectionDlg)
            {
                return PanelAdder.DefaultPanelName;
            }

            FrmChoosePanel frmPnl = new();
            return frmPnl.ShowDialog() == DialogResult.OK
                ? frmPnl.Panel
                : null;
        }

        private ConnectionWindow SetConnectionForm(ConnectionWindow conForm, string connectionPanel)
        {
            ConnectionWindow connectionForm = conForm ?? Runtime.WindowList.FromString(connectionPanel) as ConnectionWindow;

            if (connectionForm == null)
            {
                connectionForm = _panelAdder.AddPanel(connectionPanel, showImmediately: true);
            }
            else
            {
                connectionForm.Show(FrmMain.Default.pnlDock);
            }

            connectionForm.Focus();
            return connectionForm;
        }

        private static Control SetConnectionContainer(ConnectionInfo connectionInfo, ConnectionWindow connectionForm, DockPane targetPane = null, int? targetContentIndex = null)
        {
            Control connectionContainer = connectionForm.AddConnectionTab(connectionInfo, targetPane, targetContentIndex);

            if (connectionInfo.Protocol != ProtocolType.IntApp) return connectionContainer;

            Tools.ExternalTool extT = Runtime.ExternalToolsService.GetExtAppByName(connectionInfo.ExtApp);

            if (extT == null) return connectionContainer;

            if (extT.Icon != null)
                ((ConnectionTab)connectionContainer).Icon = extT.Icon;

            return connectionContainer;
        }

        private static void SetConnectionFormEventHandlers(ProtocolBase newProtocol, Form connectionForm)
        {
            newProtocol.Closed += ((ConnectionWindow)connectionForm).Prot_Event_Closed;
        }

        private void SetConnectionEventHandlers(ProtocolBase newProtocol)
        {
            newProtocol.Disconnected += Prot_Event_Disconnected;
            newProtocol.Connected += Prot_Event_Connected;
            newProtocol.Closed += Prot_Event_Closed;
            newProtocol.ErrorOccured += Prot_Event_ErrorOccured;
        }

        private static void BuildConnectionInterfaceController(ConnectionInfo connectionInfo,
                                                               ProtocolBase newProtocol,
                                                               Control connectionContainer)
        {
            newProtocol.InterfaceControl = new InterfaceControl(connectionContainer, newProtocol, connectionInfo);
        }

        #endregion

        #region Event handlers

        private static void Prot_Event_Disconnected(object sender, string disconnectedMessage, int? reasonCode)
        {
            try
            {
                ProtocolBase prot = (ProtocolBase)sender;
                MessageClass msgClass = MessageClass.InformationMsg;

                if (prot.InterfaceControl.Info.Protocol == ProtocolType.RDP)
                {
                    if (reasonCode > 3)
                    {
                        msgClass = MessageClass.WarningMsg;
                    }
                }

                string strHostname = prot.InterfaceControl.OriginalInfo.Hostname;
                if (prot.InterfaceControl.SSHTunnelInfo != null)
                {
                    strHostname += " via SSH Tunnel " + prot.InterfaceControl.SSHTunnelInfo.Name;
                }
                Runtime.MessageCollector.AddMessage(msgClass,
                                                    string.Format(
                                                                  Language.ProtocolEventDisconnected,
                                                                  disconnectedMessage,
                                                                  strHostname,
                                                                  prot.InterfaceControl.Info.Protocol.ToString()));
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.ProtocolEventDisconnectFailed, ex);
            }
        }

        private void Prot_Event_Closed(object sender)
        {
            try
            {
                ProtocolBase prot = (ProtocolBase)sender;
                Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.ConnenctionCloseEvent, true);
                string connDetail;
                if (prot.InterfaceControl.OriginalInfo.Hostname == "" && prot.InterfaceControl.Info.Protocol == ProtocolType.IntApp)
                    connDetail = prot.InterfaceControl.Info.ExtApp;
                else if (prot.InterfaceControl.OriginalInfo.Hostname != "")
                    connDetail = prot.InterfaceControl.OriginalInfo.Hostname;
                else
                    connDetail = "UNKNOWN";

                Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, string.Format(Language.ConnenctionClosedByUser, connDetail, prot.InterfaceControl.Info.Protocol, Environment.UserName));
                prot.InterfaceControl.OriginalInfo.OpenConnections.Remove(prot);
                if (_activeConnections.Contains(prot.InterfaceControl.Info.ConstantID))
                    _activeConnections.Remove(prot.InterfaceControl.Info.ConstantID);

                if (prot.InterfaceControl.Info.PostExtApp == "") return;
                Tools.ExternalTool extA = Runtime.ExternalToolsService.GetExtAppByName(prot.InterfaceControl.Info.PostExtApp);
                extA?.Start(prot.InterfaceControl.OriginalInfo);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.ConnenctionCloseEventFailed, ex);
            }
        }

        private static void Prot_Event_Connected(object sender)
        {
            ProtocolBase prot = (ProtocolBase)sender;
            Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg, Language.ConnectionEventConnected,
                                                true);
            Runtime.MessageCollector.AddMessage(MessageClass.InformationMsg,
                                                string.Format(Language.ConnectionEventConnectedDetail,
                                                              prot.InterfaceControl.OriginalInfo.Hostname,
                                                              prot.InterfaceControl.Info.Protocol, Environment.UserName,
                                                              prot.InterfaceControl.Info.Description,
                                                              prot.InterfaceControl.Info.UserField));
        }

        private static void Prot_Event_ErrorOccured(object sender, string errorMessage, int? errorCode)
        {
            try
            {
                ProtocolBase prot = (ProtocolBase)sender;

                string msg = string.Format(
                                        Language.ConnectionEventErrorOccured,
                                        errorMessage,
                                        prot.InterfaceControl.OriginalInfo.Hostname,
                                        errorCode?.ToString() ?? "-");
                Runtime.MessageCollector.AddMessage(MessageClass.WarningMsg, msg);
            }
            catch (Exception ex)
            {
                Runtime.MessageCollector.AddExceptionStackTrace(Language.ConnectionFailed, ex);
            }
        }

        #endregion
    }
}
