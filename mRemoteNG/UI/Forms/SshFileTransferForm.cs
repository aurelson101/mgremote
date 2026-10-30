// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Security;

namespace mRemoteNG.UI.Forms;

internal sealed class SshFileTransferForm : Form
{
    private CancellationTokenSource _cancellation;
    private Process _process;
    private bool _busy;
    public SshFileTransferForm(ConnectionInfo connection)
    {
        var info = connection.Clone();
        Text = "MGRemote — Transfert SSH (SFTP)"; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(700, 430); DesktopAppearance.Configure(this, new Size(560, 390));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        void Row(string label, Control control, Button button = null) {
            int row = layout.RowCount++;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, row);
            row = layout.RowCount++; control.Dock = DockStyle.Fill; layout.Controls.Add(control, 0, row);
            if (button == null) layout.SetColumnSpan(control, 2); else layout.Controls.Add(button, 1, row);
        }
        var direction = new ComboBox { Name = "TransferDirection", DropDownStyle = ComboBoxStyle.DropDownList };
        direction.Items.AddRange(new object[] { "Envoyer : PC → serveur", "Télécharger : serveur → PC" }); direction.SelectedIndex = 0;
        Row(info.Name + " — " + info.Hostname + " (SFTP via Windows OpenSSH)", direction);
        var local = new TextBox { Name = "TransferLocalPath", AllowDrop = true };
        var browse = new Button { Text = "Parcourir…", AutoSize = true };
        var folder = new CheckBox { Name = "TransferFolder", Text = "Dossier complet (récursif)", AutoSize = true };
        browse.Click += (_, _) => {
            if (direction.SelectedIndex == 1 || folder.Checked) {
                using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK) local.Text = picker.SelectedPath;
            } else { using var picker = new OpenFileDialog { CheckFileExists = true }; if (picker.ShowDialog(this) == DialogResult.OK) local.Text = picker.FileName; }
        };
        void SelectLocal(string path) { direction.SelectedIndex = 0; local.Text = path; folder.Checked = Directory.Exists(path); }
        local.DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        local.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths) SelectLocal(paths[0]); };
        Row("Fichier/dossier local ; pour télécharger : dossier de destination", local, browse);
        var remote = new TextBox { Name = "TransferRemotePath", PlaceholderText = "/home/user/Documents ou C:/Users/user/Documents" };
        Row("Chemin distant absolu (à l’envoi : dossier existant ; au téléchargement : fichier/dossier)", remote);
        int optionsRow = layout.RowCount++; layout.Controls.Add(folder, 0, optionsRow);
        var paste = new Button { Text = "Fichier du presse-papiers", AutoSize = true };
        layout.Controls.Add(paste, 1, optionsRow);
        var status = new Label { Name = "TransferStatus", AutoSize = true, MaximumSize = new Size(640, 0),
            Text = "La console Windows gère l’authentification et la progression. À l’envoi, les fichiers de même nom peuvent être remplacés. Au téléchargement, un nouveau sous-dossier sera créé. Les fichiers partiels sont conservés en cas d’annulation." };
        int statusRow = layout.RowCount++; layout.Controls.Add(status, 0, statusRow); layout.SetColumnSpan(status, 2);
        paste.Click += (_, _) => {
            try { var paths = Clipboard.GetFileDropList(); if (paths.Count == 1) SelectLocal(paths[0]); else status.Text = "Copiez un fichier ou un dossier à la fois ; pour plusieurs fichiers, sélectionnez leur dossier."; }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "Le presse-papiers est occupé. Réessayez."; }
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var transfer = new Button { Name = "StartSshTransfer", Text = "Transférer", AutoSize = true };
        var cancel = new Button { Text = "Fermer", AutoSize = true };
        actions.Controls.Add(transfer); actions.Controls.Add(cancel);
        int actionRow = layout.RowCount++; layout.Controls.Add(actions, 0, actionRow); layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        cancel.Click += (_, _) => { if (_busy) _cancellation?.Cancel(); else Close(); };
        FormClosing += (_, _) => _cancellation?.Cancel();
        transfer.Click += async (_, _) => {
            if (_busy) return;
            bool upload = direction.SelectedIndex == 0;
            string localPath = local.Text.Trim(), remotePath = remote.Text;
            bool recursive = folder.Checked;
            _busy = true; _cancellation = new CancellationTokenSource(); var token = _cancellation.Token;
            foreach (Control control in new Control[] { direction, local, remote, browse, folder, paste, transfer }) control.Enabled = false;
            cancel.Text = "Annuler le transfert";
            OpenSshTunnel tunnel = null;
            try {
                // Validate all input before creating folders or contacting a bastion.
                var start = SshFileTransfer.BuildStartInfo(info.Hostname, info.Username, info.Port, info.SSHOptions, localPath, remotePath, upload, recursive);
                if (upload) await Task.Run(() => SshFileTransfer.ValidateUploadTree(localPath, token), token);
                token.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(info.SSHTunnelConnectionName)) {
                    var bastion = ConnectionInitiator.getSSHConnectionInfoByName(Runtime.ConnectionsService.ConnectionTreeModel.RootNodes, info.SSHTunnelConnectionName)
                        ?? throw new InvalidOperationException("Le bastion SSH n’existe plus.");
                    if (bastion.ConstantID == info.ConstantID || !string.IsNullOrEmpty(bastion.SSHTunnelConnectionName)) throw new InvalidOperationException("Choisissez un bastion distinct sans tunnel imbriqué.");
                    string destination = await OpenSshTunnel.ResolveSshHostnameAsync(info, token);
                    tunnel = await OpenSshTunnel.StartAsync(bastion, destination, info.Port, token);
                    start = SshFileTransfer.BuildStartInfo(info.Hostname, info.Username, info.Port, info.SSHOptions, localPath, remotePath, upload, recursive, tunnel.LocalPort, destination);
                }
                token.ThrowIfCancellationRequested();
                if (!upload) {
                    localPath = Path.Combine(localPath, "MGRemote-transfer-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(localPath);
                    start.ArgumentList[start.ArgumentList.Count - 1] = localPath.Replace('\\', '/');
                }
                _process = Process.Start(start) ?? throw new InvalidOperationException("Impossible de démarrer le transfert.");
                using var job = OpenSshTunnel.ProtectProcessLifetime(_process);
                status.Text = "Transfert en cours dans la console Windows. Ne fermez pas la fenêtre avant la fin.";
                await _process.WaitForExitAsync(token);
                if (_process.ExitCode != 0) throw new IOException("Le transfert a échoué ou a été interrompu. Consultez la console et les fichiers partiels.");
                if (!IsDisposed) status.Text = "Transfert terminé. " + (!upload ? "Dossier local : " + localPath : "");
            }
            catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Transfert annulé. Les fichiers partiels sont conservés."; }
            catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
            finally {
                try { if (_process != null && !_process.HasExited) _process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* The kill-on-close job is also disposed above. */ }
                _process?.Dispose(); _process = null; tunnel?.Dispose(); _cancellation.Dispose(); _cancellation = null; _busy = false;
                if (!IsDisposed) { foreach (Control control in new Control[] { direction, local, remote, browse, folder, paste, transfer }) control.Enabled = true; cancel.Text = "Fermer"; }
            }
        };
        layout.ClientSizeChanged += (_, _) => status.MaximumSize = new Size(Math.Max(200, layout.ClientSize.Width - 48), 0);
    }
}
