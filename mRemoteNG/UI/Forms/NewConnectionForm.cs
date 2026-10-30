// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Windows.Forms;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol;
using mRemoteNG.Connection.Protocol.RDP;
using mRemoteNG.Connection.Protocol.Terminal;
using mRemoteNG.Security;

namespace mRemoteNG.UI.Forms;

internal sealed class NewConnectionForm : Form
{
    private readonly ConnectionInfo _connection;
    private readonly TextBox _name = new() { Name = "ConnectionName", MaxLength = 256 };
    private readonly ComboBox _protocol = new() { Name = "Protocol", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _host = new() { Name = "Hostname", MaxLength = 253 };
    private readonly NumericUpDown _port = new() { Name = "Port", Minimum = 1, Maximum = 65535, Value = 3389 };
    private readonly TextBox _user = new() { Name = "Username", MaxLength = 256 };
    private readonly TextBox _domain = new() { Name = "Domain", MaxLength = 256 };
    private readonly TextBox _identity = new() { Name = "SshPrivateKey", MaxLength = 1024, Dock = DockStyle.Fill };
    private readonly ComboBox _tunnel = new() { Name = "SshTunnel", DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _rdpClipboard = new() { Name = "RdpClipboard", Text = "Presse-papiers bidirectionnel", AutoSize = true, Checked = true };
    private readonly Label _guidance = new() { AutoSize = true };
    private readonly Label _error = new() { Name = "ValidationError", AutoSize = true, ForeColor = Color.Firebrick, AccessibleRole = AccessibleRole.Alert };
    private readonly Button _keys = new() { Text = "Clés et profils SSH…", AutoSize = true };
    private int _defaultPort = 3389;

    public NewConnectionForm(ConnectionInfo connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Text = "MGRemote — Nouvelle connexion";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 520);
        ShowInTaskbar = false;
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        DesktopAppearance.Configure(this, new Size(540, 440));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10), BackColor = SystemColors.Control };
        var create = new Button { Name = "CreateConnection", Text = "Créer la connexion", AutoSize = true,
            BackColor = Color.FromArgb(0, 103, 192), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 5, 10, 5) };
        create.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Annuler", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(10, 5, 10, 5) };
        buttons.Controls.Add(create);
        buttons.Controls.Add(cancel);
        AcceptButton = create;
        CancelButton = cancel;

        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 18, 24, 12) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, GrowStyle = TableLayoutPanelGrowStyle.AddRows };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label AddRow(string label, Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var caption = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 18, 8) };
            fields.Controls.Add(caption, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 5, 0, 5);
            fields.Controls.Add(control, 1, row);
            return caption;
        }
        void AddNote(Control control)
        {
            int row = fields.RowCount++;
            fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(0, 10, 0, 8);
            fields.Controls.Add(control, 0, row);
            fields.SetColumnSpan(control, 2);
        }
        AddNote(new Label { Text = "Choisissez un protocole, puis indiquez le serveur à joindre.", AutoSize = true });
        _protocol.Items.AddRange(new object[] { "RDP — Bureau à distance Windows", "SSH — OpenSSH Windows", "VNC — via tunnel SSH" });
        AddRow("Nom (facultatif)", _name);
        AddRow("Protocole", _protocol);
        AddRow("Serveur / adresse IP", _host);
        AddRow("Port", _port);
        AddRow("Utilisateur (facultatif)", _user);
        AddRow("Domaine RDP (facultatif)", _domain);
        _tunnel.Items.Add("Aucun — connexion directe");
        if (mRemoteNG.App.Runtime.ConnectionsService.ConnectionTreeModel != null)
            foreach (string name in mRemoteNG.Tools.SshTunnelTypeConverter.SshTunnels)
                if (!string.IsNullOrEmpty(name)) _tunnel.Items.Add(name);
        _tunnel.SelectedIndex = 0;
        AddRow("Tunnel SSH / bastion", _tunnel);
        var identityPanel = new Panel { Height = _identity.PreferredHeight };
        var browse = new Button { Text = "Parcourir…", Dock = DockStyle.Right, Width = 95 };
        browse.Click += (_, _) => {
            using var picker = new OpenFileDialog { Title = "Clé privée OpenSSH / PEM (pas le fichier .pub)", CheckFileExists = true };
            if (picker.ShowDialog(this) == DialogResult.OK) _identity.Text = picker.FileName;
        };
        identityPanel.Controls.Add(_identity); identityPanel.Controls.Add(browse);
        var identityLabel = AddRow("Clé privée SSH (facultatif)", identityPanel);
        var sharing = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var sharingButton = new Button { Text = "Fichiers et lecteurs…", AutoSize = true };
        sharing.Controls.Add(_rdpClipboard); sharing.Controls.Add(sharingButton);
        sharingButton.Click += (_, _) => {
            _connection.Inheritance.RedirectClipboard = false;
            _connection.RedirectClipboard = _rdpClipboard.Checked;
            using var dialog = new RdpSharingForm(_connection);
            if (dialog.ShowDialog(this) == DialogResult.OK) _rdpClipboard.Checked = _connection.RedirectClipboard;
        };
        AddNote(sharing);
        AddNote(_guidance);
        AddNote(_keys);
        AddNote(_error);
        viewport.Controls.Add(fields);
        Controls.Add(viewport);
        Controls.Add(buttons);
        viewport.ClientSizeChanged += (_, _) => {
            int width = Math.Max(100, viewport.ClientSize.Width - viewport.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            _guidance.MaximumSize = new Size(width, 0);
            _error.MaximumSize = new Size(width, 0);
        };
        _protocol.SelectedIndexChanged += (_, _) => {
            bool ssh = _protocol.SelectedIndex == 1;
            int nextPort = ssh ? 22 : _protocol.SelectedIndex == 2 ? 5900 : 3389;
            if (_port.Value == _defaultPort) _port.Value = nextPort;
            _defaultPort = nextPort;
            _domain.Enabled = _protocol.SelectedIndex == 0;
            _keys.Visible = ssh;
            identityPanel.Visible = identityLabel.Visible = ssh;
            sharing.Visible = _protocol.SelectedIndex == 0;
            _guidance.Text = ssh
                ? "Clé privée OpenSSH/PEM. Pour une clé chiffrée : chargez-la dans ssh-agent via Clés et profils, ou utilisez le clic droit SSH — console Windows. Pour un profil : alias, utilisateur vide et port du profil. Vérifiez l’empreinte du serveur."
                : _protocol.SelectedIndex == 2
                ? "VNC : choisissez un bastion SSH pour chiffrer le transport. Configurez la clé facultative du bastion dans sa connexion SSH."
                : "Authentification du serveur et NLA/CredSSP activés, même via un tunnel. Le certificat RDP doit rester valide. Le mot de passe sera demandé à la connexion.";
        };
        _protocol.SelectedIndex = 0;
        _keys.Click += (_, _) => {
            using var profiles = new SshProfileForm(_identity.Text.Trim()); profiles.ShowDialog(this);
            if (profiles.CreatedAlias == null) return;
            _host.Text = profiles.CreatedAlias; _port.Value = profiles.CreatedPort; _user.Clear(); _identity.Clear();
            if (string.IsNullOrWhiteSpace(_name.Text)) _name.Text = profiles.CreatedAlias;
        };
        create.Click += (_, _) => { if (TryCreateConnection()) DialogResult = DialogResult.OK; };
        Shown += (_, _) => _host.Focus();
    }

    private bool TryCreateConnection()
    {
        string host = _host.Text.Trim();
        string user = _user.Text.Trim();
        bool ssh = _protocol.SelectedIndex == 1;
        string identityOptions = ssh && !string.IsNullOrWhiteSpace(_identity.Text) ? "-i \"" + _identity.Text.Trim() + "\"" : string.Empty;
        _error.Text = string.Empty;
        try
        {
            if (host.Length == 0) throw new ArgumentException("Indiquez le serveur, son adresse IP ou un alias SSH.");
            if (_protocol.SelectedIndex == 2 && _tunnel.SelectedIndex == 0)
                throw new ArgumentException("Choisissez un tunnel SSH pour protéger cette nouvelle connexion VNC.");
            if (ssh) _ = WindowsSecurityPolicy.BuildOpenSshArguments(host, user, (int)_port.Value, identityOptions);
            else if (Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown)
                throw new ArgumentException("Indiquez un nom de serveur ou une adresse IP, sans URL ni numéro de port.");
            if (user.Any(char.IsControl) || _domain.Text.Any(char.IsControl))
                throw new ArgumentException("L’utilisateur et le domaine contiennent des caractères non valides.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { _error.Text = ex.Message; _host.Focus(); return false; }

        // These explicit choices must not be replaced by inherited folder settings.
        var inheritance = _connection.Inheritance;
        inheritance.Protocol = inheritance.Port = inheritance.Username = inheritance.Domain = inheritance.Password = false;
        inheritance.TerminalBackend = inheritance.RDPAuthenticationLevel = inheritance.UseCredSsp = false;
        inheritance.SSHOptions = false;
        inheritance.SSHTunnelConnectionName = false;
        _connection.Name = string.IsNullOrWhiteSpace(_name.Text) ? host : _name.Text.Trim();
        _connection.Hostname = host;
        _connection.Protocol = ssh ? ProtocolType.SSH2 : _protocol.SelectedIndex == 2 ? ProtocolType.VNC : ProtocolType.RDP;
        _connection.Port = (int)_port.Value;
        _connection.Username = user;
        _connection.Domain = _protocol.SelectedIndex == 0 ? _domain.Text.Trim() : string.Empty;
        _connection.Password = string.Empty;
        _connection.TerminalBackend = TerminalBackend.WindowsTerminal;
        _connection.SSHOptions = identityOptions;
        _connection.SSHTunnelConnectionName = _tunnel.SelectedIndex > 0 ? _tunnel.SelectedItem.ToString() : string.Empty;
        _connection.RDPAuthenticationLevel = AuthenticationLevel.AuthRequired;
        _connection.UseCredSsp = true;
        if (_protocol.SelectedIndex == 0) { _connection.Inheritance.RedirectClipboard = false; _connection.RedirectClipboard = _rdpClipboard.Checked; }
        return true;
    }
}
