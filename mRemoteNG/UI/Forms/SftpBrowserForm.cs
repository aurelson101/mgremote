// MGRemote modification. GPL-2.0; see COPYING.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using mRemoteNG.App;
using mRemoteNG.Connection;
using mRemoteNG.Security;

namespace mRemoteNG.UI.Forms;

internal sealed class SshFileTransferForm : Form
{
    private const string RemoteFormat = "MGRemote.Sftp.Selection";
    private sealed record RemoteSelection(SshFileTransferForm Owner, string[] Paths);
    private sealed record Job(string Source, string Destination, bool Upload, bool Overwrite, ListViewItem Row);
    private readonly ConnectionInfo _info;
    private readonly TextBox _localPath = new() { Name = "TransferLocalPath", Dock = DockStyle.Fill };
    private readonly TextBox _remotePath = new() { Name = "TransferRemotePath", Dock = DockStyle.Fill };
    private readonly ListView _local = FileList("SftpLocalFiles"), _remote = FileList("SftpRemoteFiles");
    private readonly ListView _queue = new() { Name = "SftpQueue", View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
    private readonly Label _status = new() { Name = "TransferStatus", AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Connectez-vous, puis glissez vos fichiers entre les panneaux. Ctrl et Maj : sélection multiple." };
    private readonly Button _connect = new() { Name = "ConnectSftp", Text = "Connexion", AutoSize = true };
    private readonly Button _upload = new() { Name = "StartSshTransfer", Text = "Envoyer →", AutoSize = true };
    private readonly Button _download = new() { Name = "DownloadSftp", Text = "← Télécharger", AutoSize = true };
    private readonly Button _cancel = new() { Name = "CancelSftp", Text = "Annuler", AutoSize = true, Enabled = false };
    private readonly CheckBox _overwrite = new() { Name = "SftpOverwrite", Text = "Remplacer les fichiers existants", AutoSize = true };
    private readonly ComboBox _authentication = new() { Name = "SftpAuthentication", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Maximum = 1000 };
    private readonly Queue<Job> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _activeCancellation;
    private SftpBrowserSession _session;
    private OpenSshTunnel _tunnel;
    private Task _operation = Task.CompletedTask, _transfers = Task.CompletedTask;
    private bool _working, _queueRunning, _closing, _allowClose;
    private int _localGeneration;
    private RemoteSelection _copiedRemote;
    private RemoteSelection _draggedRemote;
    private readonly string _dragToken = Guid.NewGuid().ToString("N");

    public SshFileTransferForm(ConnectionInfo connection)
    {
        _info = connection.Clone(); Text = "MGRemote — SFTP — " + _info.Name;
        ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1080, 690); DesktopAppearance.Configure(this, new Size(760, 510));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _authentication.Items.AddRange(new object[] { "Profil SSH", "Mot de passe" }); _authentication.SelectedIndex = 0;
        var browseLocal = new Button { Text = "Dossier local…", AutoSize = true };
        browseLocal.Click += async (_, _) => { if (_working || _queueRunning) return; using var picker = new FolderBrowserDialog { SelectedPath = _localPath.Text }; if (picker.ShowDialog(this) == DialogResult.OK) await BrowseLocalAsync(picker.SelectedPath); };
        toolbar.Controls.AddRange(new Control[] { _connect, _authentication, browseLocal, _upload, _download, _cancel, _overwrite }); root.Controls.Add(toolbar, 0, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6, Size = new Size(1060, 570), SplitterDistance = 410, Panel1MinSize = 180, Panel2MinSize = 80 };
        var panes = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, Size = new Size(1060, 400), SplitterDistance = 527, Panel1MinSize = 220, Panel2MinSize = 220 };
        panes.Panel1.Controls.Add(Pane("Site local", _localPath, _local, false));
        panes.Panel2.Controls.Add(Pane("Site distant — " + _info.Hostname, _remotePath, _remote, true)); split.Panel1.Controls.Add(panes);
        _queue.Columns.Add("Sens", 90); _queue.Columns.Add("Fichier / dossier", 240); _queue.Columns.Add("Destination", 300); _queue.Columns.Add("État", 330);
        var queuePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        queuePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); queuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        queuePanel.Controls.Add(new Label { Text = "File d’attente des transferts", AutoSize = true }, 0, 0); queuePanel.Controls.Add(_queue, 0, 1);
        split.Panel2.Controls.Add(queuePanel); root.Controls.Add(split, 0, 1); root.Controls.Add(_progress, 0, 2); root.Controls.Add(_status, 0, 3); Controls.Add(root);
        _localPath.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _connect.Click += async (_, _) => { if (_working || _queueRunning) return; _operation = ConnectAsync(); await _operation; };
        _upload.Click += (_, _) => Enqueue(Selected(_local), _remotePath.Text, true);
        _download.Click += (_, _) => Enqueue(Selected(_remote), _localPath.Text, false);
        _cancel.Click += (_, _) => CancelTransfers();
        _local.ItemDrag += (_, _) => { var files = Selected(_local); if (files.Length > 0) { var data = new DataObject(); data.SetData(DataFormats.FileDrop, files); _local.DoDragDrop(data, DragDropEffects.Copy); } };
        _remote.ItemDrag += (_, _) => {
            var files = Selected(_remote); if (files.Length == 0) return;
            _draggedRemote = new RemoteSelection(this, files);
            using var token = new MemoryStream(Encoding.ASCII.GetBytes(_dragToken));
            var data = new DataObject(); data.SetData(RemoteFormat, false, token);
            try { _remote.DoDragDrop(data, DragDropEffects.Copy); } finally { _draggedRemote = null; }
        };
        void RemoteEffect(DragEventArgs e) => e.Effect = _session != null && !_working && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        void LocalEffect(DragEventArgs e) => e.Effect = RemoteData(e.Data) != null && !_working ? DragDropEffects.Copy : DragDropEffects.None;
        _remote.DragEnter += (_, e) => RemoteEffect(e); _remote.DragOver += (_, e) => RemoteEffect(e);
        _local.DragEnter += (_, e) => LocalEffect(e); _local.DragOver += (_, e) => LocalEffect(e);
        _remote.DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) Enqueue(paths, DropDirectory(_remote, _remotePath.Text, e), true); };
        _local.DragDrop += (_, e) => { if (RemoteData(e.Data) is { } selection) Enqueue(selection.Paths, DropDirectory(_local, _localPath.Text, e), false); };
        _local.KeyDown += (_, e) => {
            if (e.Control && e.KeyCode == Keys.C) {
                var paths = Selected(_local); if (paths.Length > 0) { var items = new System.Collections.Specialized.StringCollection(); items.AddRange(paths); try { Clipboard.SetFileDropList(items); } catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Presse-papiers occupé."; } } e.SuppressKeyPress = true;
            } else if (e.Control && e.KeyCode == Keys.V && _copiedRemote != null) { Enqueue(_copiedRemote.Paths, _localPath.Text, false); e.SuppressKeyPress = true; }
        };
        _remote.KeyDown += (_, e) => {
            if (e.Control && e.KeyCode == Keys.C) { _copiedRemote = new RemoteSelection(this, Selected(_remote)); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.V) { try { Enqueue(Clipboard.GetFileDropList().Cast<string>().ToArray(), _remotePath.Text, true); } catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Presse-papiers occupé."; } e.SuppressKeyPress = true; }
        };
        Shown += async (_, _) => await BrowseLocalAsync(_localPath.Text);
        FormClosing += async (_, e) => {
            if (_allowClose) return;
            e.Cancel = true; if (_closing) return; _closing = true; _lifetime.Cancel(); CancelTransfers(); Enabled = false;
            try { await Task.WhenAll(_operation, _transfers); } catch (Exception) { /* Wait for workers before disposing their connection. */ }
            _session?.Dispose(); _tunnel?.Dispose(); _info.Password = ""; _lifetime.Dispose(); _allowClose = true; Close();
        };
    }
    private static ListView FileList(string name)
    {
        var list = new ListView { Name = name, Dock = DockStyle.Fill, View = View.Details, MultiSelect = true, FullRowSelect = true, HideSelection = false, AllowDrop = true };
        list.Columns.Add("Nom", 235); list.Columns.Add("Taille", 90); list.Columns.Add("Type", 80); list.Columns.Add("Modifié le", 140); return list;
    }
    private Control Pane(string title, TextBox path, ListView list, bool remote)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = title, AutoSize = true, Padding = new Padding(0, 5, 0, 5) }, 0, 0);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var up = new Button { Text = "↑", AutoSize = true, AccessibleName = "Dossier parent" }; var refresh = new Button { Text = "Actualiser", AutoSize = true };
        navigation.Controls.Add(path, 0, 0); navigation.Controls.Add(up, 1, 0); navigation.Controls.Add(refresh, 2, 0);
        panel.Controls.Add(navigation, 0, 1); panel.Controls.Add(list, 0, 2);
        async Task Browse(string destination) { if (_working || _queueRunning) return; if (remote) { _operation = BrowseRemoteAsync(destination); await _operation; } else await BrowseLocalAsync(destination); }
        refresh.Click += async (_, _) => await Browse(path.Text);
        up.Click += async (_, _) => { try { await Browse(remote ? SftpBrowserSession.Parent(path.Text) : Directory.GetParent(path.Text)?.FullName ?? path.Text); } catch (Exception ex) { _status.Text = ex.Message; } };
        path.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Browse(path.Text); } };
        list.DoubleClick += async (_, _) => { if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is SftpEntry entry && entry.Directory && !entry.Link) await Browse(entry.Path); };
        return panel;
    }
    private async Task BrowseLocalAsync(string path)
    {
        int generation = ++_localGeneration;
        try {
            string full = Path.GetFullPath(path);
            var entries = await Task.Run(() => new DirectoryInfo(full).EnumerateFileSystemInfos().Take(100001).Select(x => new SftpEntry(x.Name, x.FullName,
                (x.Attributes & FileAttributes.Directory) != 0, (x.Attributes & FileAttributes.ReparsePoint) != 0, x is FileInfo f ? f.Length : 0, x.LastWriteTime)).OrderByDescending(x => x.Directory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(), _lifetime.Token);
            if (_closing || IsDisposed || generation != _localGeneration) return;
            if (entries.Length > 100000) throw new IOException("Ce dossier contient trop d’éléments.");
            Fill(_local, entries, Directory.GetParent(full)?.FullName); _localPath.Text = full;
        } catch (Exception ex) { if (!_closing) _status.Text = ex.Message; }
    }
    private async Task BrowseRemoteAsync(string path)
    {
        if (_session == null) { _status.Text = "Connectez-vous au serveur SFTP."; return; }
        _working = true;
        try { var entries = await _session.ListAsync(path, _lifetime.Token); if (!_closing) { Fill(_remote, entries, path == "/" ? null : SftpBrowserSession.Parent(path)); _remotePath.Text = path; } }
        catch (Exception ex) { if (!_closing) _status.Text = ex.Message; }
        finally { _working = false; }
    }
    private static void Fill(ListView list, SftpEntry[] entries, string parent)
    {
        list.BeginUpdate();
        try {
            list.Items.Clear(); if (parent != null) list.Items.Add(new ListViewItem(new[] { "..", "", "Dossier", "" }) { Tag = new SftpEntry("..", parent, true, false, 0, default) });
            foreach (var item in entries) list.Items.Add(new ListViewItem(new[] { item.Name, item.Directory ? "" : SizeText(item.Size), item.Link ? "Lien" : item.Directory ? "Dossier" : "Fichier", item.Modified.ToString("g") }) { Tag = item });
        } finally { list.EndUpdate(); }
    }
    private static string[] Selected(ListView list) => list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag).OfType<SftpEntry>().Where(x => x.Name != "..").Select(x => x.Path).ToArray();
    private RemoteSelection RemoteData(IDataObject data)
    {
        if (_draggedRemote == null || data?.GetDataPresent(RemoteFormat, false) != true) return null;
        return data.GetData(RemoteFormat, false) is MemoryStream { Length: <= 128 } bytes && Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\0') == _dragToken ? _draggedRemote : null;
    }
    private static string DropDirectory(ListView list, string fallback, DragEventArgs e) => list.GetItemAt(list.PointToClient(new Point(e.X, e.Y)).X, list.PointToClient(new Point(e.X, e.Y)).Y)?.Tag is SftpEntry { Directory: true, Link: false } folder ? folder.Path : fallback;
    private void Enqueue(string[] paths, string destination, bool upload)
    {
        if (_session == null || _working) { _status.Text = "Connectez-vous et attendez la fin de la navigation avant de transférer."; return; }
        if (paths.Length == 0) { _status.Text = "Sélectionnez un ou plusieurs fichiers/dossiers."; return; }
        if (paths.Length + _queue.Items.Count > 10000) { _status.Text = "File d’attente pleine. Rouvrez la fenêtre après les transferts terminés."; return; }
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase)) {
            var row = new ListViewItem(new[] { upload ? "Envoi →" : "← Réception", upload ? Path.GetFileName(path) : path.Split('/').Last(), destination, "En attente" });
            _queue.Items.Add(row); _pending.Enqueue(new Job(path, destination, upload, _overwrite.Checked, row));
        }
        if (!_queueRunning) _transfers = ProcessQueueAsync();
    }
    private async Task ProcessQueueAsync()
    {
        _queueRunning = true; _cancel.Enabled = true; _connect.Enabled = false;
        try {
            while (_pending.TryDequeue(out var job)) {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _activeCancellation = cancellation; job.Row.SubItems[3].Text = "En cours";
                var progress = new Progress<SftpProgress>(p => { if (!_closing && !IsDisposed) { _status.Text = p.File + " — " + SizeText(p.Bytes) + " / " + SizeText(p.Total); _progress.Value = p.Total == 0 ? 0 : (int)Math.Clamp(p.Bytes * 1000.0 / p.Total, 0, 1000); } });
                try { await _session.TransferAsync(job.Source, job.Destination, job.Upload, job.Overwrite, progress, cancellation.Token); job.Row.SubItems[3].Text = "Terminé"; }
                catch (Exception ex) { job.Row.Tag = ex; job.Row.SubItems[3].Text = cancellation.IsCancellationRequested ? "Annulé" : "Échec : " + ex.Message; if (!_closing) _status.Text = job.Row.SubItems[3].Text; }
                finally { _activeCancellation = null; }
            }
        } finally {
            _working = true;
            try { if (!_closing) { await BrowseLocalAsync(_localPath.Text); await BrowseRemoteAsync(_remotePath.Text); _status.Text = "File d’attente terminée. Consultez l’état de chaque transfert."; } }
            finally { _working = false; _queueRunning = false; _cancel.Enabled = false; _connect.Enabled = true; _progress.Value = 0; }
        }
    }
    private void CancelTransfers() { _activeCancellation?.Cancel(); while (_pending.TryDequeue(out var job)) job.Row.SubItems[3].Text = "Annulé"; }
    private async Task ConnectAsync()
    {
        _working = true; _connect.Enabled = false;
        try {
            _session?.Dispose(); _session = null; _tunnel?.Dispose(); _tunnel = null; _remote.Items.Clear();
            _status.Text = "Lecture de la configuration SSH…"; var settings = await ReadSettingsAsync(_lifetime.Token);
            if (_authentication.SelectedIndex == 1) settings = settings with { Keys = Array.Empty<string>() };
            string password = string.IsNullOrEmpty(_info.Password) ? null : _info.Password;
            if (settings.Keys.Length == 0 && password == null) password = AskPassword("Mot de passe SFTP — " + settings.User) ?? throw new OperationCanceledException();
            string host = settings.Host; int port = settings.Port;
            if (!string.IsNullOrEmpty(_info.SSHTunnelConnectionName)) {
                var bastion = ConnectionInitiator.getSSHConnectionInfoByName(Runtime.ConnectionsService.ConnectionTreeModel.RootNodes, _info.SSHTunnelConnectionName) ?? throw new InvalidOperationException("Le bastion SSH n’existe plus.");
                if (bastion.ConstantID == _info.ConstantID || !string.IsNullOrEmpty(bastion.SSHTunnelConnectionName)) throw new InvalidOperationException("Choisissez un bastion distinct sans tunnel imbriqué.");
                _tunnel = await OpenSshTunnel.StartAsync(bastion, host, port, _lifetime.Token); host = "127.0.0.1"; port = _tunnel.LocalPort;
            }
            _status.Text = "Connexion SFTP…";
            _session = await Task.Run(() => new SftpBrowserSession(host, port, settings.User, password, settings.Keys,
                path => OnUi(() => AskPassword("Phrase secrète — " + Path.GetFileName(path))),
                (algorithm, key) => OnUi(() => TrustHost(settings.Host, settings.Port, algorithm, key))), _lifetime.Token);
            password = null; await _session.ConnectAsync(_lifetime.Token); _remotePath.Text = _session.Home; _working = false;
            await BrowseRemoteAsync(_remotePath.Text); _connect.Text = "Reconnexion"; _status.Text = "Connecté. Glissez-déposez plusieurs fichiers ou dossiers ; les transferts sont des copies.";
        } catch (Exception ex) { _session?.Dispose(); _session = null; _tunnel?.Dispose(); _tunnel = null; if (!_closing) _status.Text = ex is OperationCanceledException ? "Connexion annulée." : ex.Message; }
        finally { _working = false; _connect.Enabled = true; }
    }
    private T OnUi<T>(Func<T> action) => _closing || IsDisposed ? default : InvokeRequired ? (T)Invoke(action) : action();
    private string AskPassword(string title) { using var prompt = new FrmPassword(title, false); var key = prompt.GetKey(this).FirstOrDefault(); if (key == null) return null; using (key) return key.ConvertToUnsecureString(); }
    private bool TrustHost(string host, int port, string algorithm, byte[] key)
    {
        if (_closing) return false;
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant() + ":" + port))), pin = algorithm + " " + Convert.ToBase64String(key);
        using var store = Registry.CurrentUser.CreateSubKey(@"Software\MGRemote\SftpHostKeys"); string existing = store.GetValue(identity) as string;
        if (existing != null) { if (existing == pin) return true; throw new IOException("La clé d’hôte SFTP a changé. Connexion refusée ; vérifiez ce changement avec l’administrateur du serveur."); }
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');
        if (MessageBox.Show(this, host + ":" + port + "\n" + algorithm + "\n" + fingerprint + "\n\nVérifiez cette empreinte auprès de l’administrateur. Faire confiance à cette clé pour les prochaines sessions SFTP ?", "Vérifier la clé d’hôte SFTP", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        store.SetValue(identity, pin, RegistryValueKind.String); return true;
    }
    private sealed record Settings(string Host, int Port, string User, string[] Keys);
    private async Task<Settings> ReadSettingsAsync(CancellationToken token)
    {
        var start = WindowsSecurityPolicy.BuildOpenSshStartInfo(_info.Hostname, _info.Username, _info.Port, _info.SSHOptions);
        start.Arguments = "-G " + start.Arguments; start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Impossible de lire la configuration OpenSSH.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token); string text = await output; await error;
            if (process.ExitCode != 0) throw new IOException("Configuration OpenSSH invalide.");
            string host = _info.Hostname, user = _info.Username; int port = _info.Port; var keys = new List<string>();
            foreach (string line in text.Split('\n')) {
                int split = line.IndexOf(' '); if (split < 0) continue; string name = line[..split], value = line[(split + 1)..].Trim();
                if (name == "hostname") host = value;
                else if (name == "user") user = value;
                else if (name == "port") port = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                else if (name == "identityfile" && value != "none") { if (value.StartsWith("~/")) value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]); if (File.Exists(value)) keys.Add(Path.GetFullPath(value)); }
                else if ((name is "proxycommand" or "proxyjump") && value != "none" && !string.IsNullOrEmpty(value) && string.IsNullOrEmpty(_info.SSHTunnelConnectionName)) throw new IOException("Pour SFTP, sélectionnez le bastion dans le champ Tunnel SSH de cette connexion.");
            }
            WindowsSecurityPolicy.BuildSshArguments(host, user, port); return new Settings(host.Trim('[', ']'), port, user, keys.ToArray());
        } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
    private static string SizeText(long size) => size < 1024 ? size + " o" : size < 1048576 ? (size / 1024.0).ToString("N1") + " Ko" : size < 1073741824 ? (size / 1048576.0).ToString("N1") + " Mo" : (size / 1073741824.0).ToString("N1") + " Go";
}
