using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private readonly ComboBox _icon = new() { ItemsSource = new[] { "Auto", "Windows", "Linux", "Network", "Serial" }, SelectedIndex = 0 };
    private readonly TextBox _certificate = new() { PlaceholderText = "Certificat OpenSSH -cert.pub (facultatif)" };
    private readonly CheckBox _rdpFileClipboard = new() { Content = "Fichiers bidirectionnels (FreeRDP 3)" };
    private readonly CheckBox _tunnel = new() { Content = "Activer une redirection locale SSH" };
    private readonly NumericUpDown _tunnelLocalPort = new() { Minimum = 1024, Maximum = 65535, Value = 13389 };
    private readonly TextBox _tunnelHost = new() { Text = "localhost" };
    private readonly NumericUpDown _tunnelRemotePort = new() { Minimum = 1, Maximum = 65535, Value = 3389 };
    private readonly ComboBox _resolution = new() { ItemsSource = new[] { "Adapter à la fenêtre", "Résolution fixe", "Plein écran" }, SelectedIndex = 0 };
    private readonly NumericUpDown _rdpWidth = new() { Minimum = 200, Maximum = 8192, Value = 1280 };
    private readonly NumericUpDown _rdpHeight = new() { Minimum = 200, Maximum = 8192, Value = 720 };
    private readonly Expander _sshSection = new() { Header = "SSH · Authentification et tunnel", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Expander _rdpSection = new() { Header = "RDP · Affichage et redirections", IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly List<Expander> _propertyGroups = [];
    private readonly TextBlock _configurationTitle = new() { Text = "Configuration", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 4), TextTrimming = TextTrimming.CharacterEllipsis };

    private void AddPropertyGroup(Panel parent, string title, Control content)
    {
        var group = new Expander { Header = title, IsExpanded = true, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        CompactCategory(group); _propertyGroups.Add(group); parent.Children.Add(group);
    }
    private Control BuildSshProperties()
    {
        var fields = new StackPanel { Spacing = 0 };
        _key.PlaceholderText = "Facultative : mot de passe ou agent SSH";
        Add(fields, "Clé privée (facultative)", BrowseField(_key, "Choisir une clé privée SSH", async () => await ChooseKey()));
        Add(fields, "Certificat SSH", BrowseField(_certificate, "Choisir un certificat SSH", async () => {
            try {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Certificat public OpenSSH", AllowMultiple = false });
                if (files.Count > 0) _certificate.Text = files[0].TryGetLocalPath();
            } catch (Exception ex) { _status.Text = ex.Message; }
        }));
        Add(fields, "Bastion / saut", _jump);
        fields.Children.Add(new TextBlock { Text = "Alias OpenSSH : utilisateur, port et clé du bastion se règlent dans ~/.ssh/config.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(7, 4) });
        fields.Children.Add(_tunnel);
        Add(fields, "Port local", _tunnelLocalPort); Add(fields, "Hôte distant", _tunnelHost); Add(fields, "Port distant", _tunnelRemotePort);
        fields.Children.Add(new TextBlock { Text = "Écoute sur 127.0.0.1 uniquement, pendant la session SSH. L’hôte distant est vu depuis le serveur SSH. SFTP n’ouvre pas un second tunnel.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, FontSize = 11, Margin = new Thickness(7, 4) });
        _tunnel.IsCheckedChanged += (_, _) => UpdateAdvancedProperties();
        return fields;
    }
    private Control PropertyToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 1) };
        bar.Classes.Add("toolstrip");
        bar.Children.Add(Button("Tout déplier", () => { foreach (var group in _propertyGroups) group.IsExpanded = true; }));
        bar.Children.Add(Button("Tout replier", () => { foreach (var group in _propertyGroups) group.IsExpanded = false; }));
        return bar;
    }
    private void UpdateAdvancedProperties()
    {
        bool ssh = _protocol.SelectedIndex == 0, rdp = _protocol.SelectedIndex == 1;
        _sshSection.IsVisible = ssh; _rdpSection.IsVisible = rdp;
        _tunnelLocalPort.IsEnabled = _tunnelHost.IsEnabled = _tunnelRemotePort.IsEnabled = ssh && _tunnel.IsChecked == true;
        _rdpWidth.IsEnabled = _rdpHeight.IsEnabled = rdp && _resolution.SelectedIndex == 1 && _multi.IsChecked != true;
        _resolution.IsEnabled = rdp && _multi.IsChecked != true;
    }
    private void ResetAdvancedProperties()
    {
        _icon.SelectedItem = "Auto"; _certificate.Text = ""; _tunnel.IsChecked = false; _rdpFileClipboard.IsChecked = false;
        _tunnelLocalPort.Value = 13389; _tunnelHost.Text = "localhost"; _tunnelRemotePort.Value = 3389;
        _resolution.SelectedIndex = 0; _rdpWidth.Value = 1280; _rdpHeight.Value = 720; _configurationTitle.Text = "Configuration · Nouvelle connexion";
        UpdateAdvancedProperties();
    }
    private void EditAdvancedProperties(Connection c)
    {
        _icon.SelectedItem = c.Icon; _certificate.Text = c.CertificateFile; _tunnel.IsChecked = c.TunnelEnabled; _rdpFileClipboard.IsChecked = c.RdpFileClipboard;
        _tunnelLocalPort.Value = c.TunnelLocalPort; _tunnelHost.Text = c.TunnelRemoteHost; _tunnelRemotePort.Value = c.TunnelRemotePort;
        _resolution.SelectedIndex = c.RdpResolution switch { "fixed" => 1, "fullscreen" => 2, _ => 0 };
        _rdpWidth.Value = c.RdpWidth; _rdpHeight.Value = c.RdpHeight; _configurationTitle.Text = "Configuration · " + c.Name;
        UpdateAdvancedProperties();
    }
    private Connection ReadAdvancedProperties(Connection c) => c with {
        Icon = _icon.SelectedItem?.ToString() ?? "Auto", CertificateFile = _certificate.Text?.Trim() ?? "", RdpFileClipboard = c.Protocol == "RDP" && c.Clipboard && _rdpFileClipboard.IsChecked == true,
        TunnelEnabled = c.Protocol == "SSH" && _tunnel.IsChecked == true, TunnelLocalPort = (int)(_tunnelLocalPort.Value ?? 0), TunnelRemoteHost = _tunnelHost.Text?.Trim() ?? "", TunnelRemotePort = (int)(_tunnelRemotePort.Value ?? 0),
        RdpResolution = _resolution.SelectedIndex switch { 1 => "fixed", 2 => "fullscreen", _ => "dynamic" }, RdpWidth = (int)(_rdpWidth.Value ?? 0), RdpHeight = (int)(_rdpHeight.Value ?? 0)
    };
    private void InstallShortcuts()
    {
        KeyDown += (_, e) => {
            if (e.KeyModifiers != KeyModifiers.Control) return;
            // Let native terminals and transfer panels own their keyboard input.
            if (e.Source is EmbeddedSession || _sessionArea.IsKeyboardFocusWithin) return;
            if (e.Key == Key.S) { Save(); e.Handled = true; }
            else if (e.Key == Key.N) { NewConnectionFromTree(); e.Handled = true; }
        };
    }
    internal void PrepareSmokePreview()
    {
        if (!App.Smoke) return;
        _connections.Add(new Connection { Name = "Windows Server 2025", Host = "windows.example.invalid", Protocol = "RDP", Port = 3389, User = "administrateur", FolderPath = ["Serveurs"], Clipboard = true });
        _connections.Add(new Connection { Name = "Ubuntu · Production", Host = "ubuntu.example.invalid", User = "admin", FolderPath = ["Serveurs"] });
        _connections.Add(new Connection { Name = "Switch · Réseau", Host = "switch.example.invalid", User = "operateur", FolderPath = ["Réseau"] });
        Filter(); _list.SelectedItem = _nodes[_connections[0]];
        _propertyGroups[0].IsExpanded = false;
        _status.Text = "Aperçu avec connexions fictives — aucune connexion distante ouverte.";
    }
}
