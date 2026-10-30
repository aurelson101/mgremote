// MGRemote Linux prototype. GPL-2.0; see ../COPYING.txt.
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;

namespace MGRemote.Linux;

internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "--credential-askpass" or "--credential-rdp") { Environment.ExitCode = CredentialBroker.RunHelper(args).GetAwaiter().GetResult(); return; }
        if (args.Contains("--credential-stdin-fixture")) { Environment.ExitCode = Console.ReadLine() == "fixture-password" && Environment.GetEnvironmentVariable("MGREMOTE_CREDENTIAL_TOKEN") == null ? 0 : 2; return; }
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        if (args.Contains("--terminal-fixture")) { Console.Write("\x1b[32mMGRemote embedded terminal fixture\x1b[0m\r\n"); while (Console.ReadLine() is string line) Console.WriteLine(line); return; }
        string? serialPlan = args.FirstOrDefault(a => a.StartsWith("--serial-plan=", StringComparison.Ordinal));
        if (serialPlan != null) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SessionLauncher.Arguments(new Connection { Protocol = "Serial", Host = serialPlan[14..] }))); return; }
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
public sealed class App : Application
{
    internal static bool Smoke => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--smoke-ui="));
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new MainWindow();
            if (Smoke) Avalonia.Threading.DispatcherTimer.RunOnce(async () => {
                try {
                    var window = desktop.MainWindow;
                    ((MainWindow)window).VerifyProtocolPortEditing();
                    await ((MainWindow)window).VerifyEmbeddedSessions();
                    if (window.Bounds.Width < 760 || window.Bounds.Height < 500) throw new Exception("Window layout failed.");
                    using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
                    image.Render((Visual)window.Content!);
                    image.Save(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..]);
                    Console.WriteLine("PASS: Linux prototype GUI rendered; user configuration untouched."); desktop.Shutdown();
                } catch (Exception ex) { Console.Error.WriteLine(ex); desktop.Shutdown(1); }
            }, TimeSpan.FromSeconds(2));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<Connection> _connections = [];
    private readonly TreeView _list = new() { MinWidth = 220 };
    private string _encryptedPassword = "";
    private readonly TextBox _gateway = new() { PlaceholderText = "Passerelle.example:443 (facultatif)" };
    private readonly ComboBox _gatewayMode = new() { ItemsSource = new[] { "direct", "detect" }, SelectedIndex = 0 };
    private readonly TextBox _folder = new();
    private readonly TextBox _description = new() { AcceptsReturn = true, MinHeight = 56, MaxHeight = 120, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _colors = new() { ItemsSource = new[] { 8, 15, 16, 24, 32 }, SelectedIndex = 4 };
    private readonly ComboBox _audio = new() { ItemsSource = new[] { "Sur cet ordinateur", "Sur le serveur", "Désactivé" }, SelectedIndex = 2 };
    private readonly CheckBox _console = new() { Content = "Session d’administration RDP (/admin)" };
    private readonly ObservableCollection<SessionTab> _tabs = [];
    private readonly Avalonia.Controls.Primitives.TabStrip _tabBar = new();
    private readonly Grid _sessionArea = new();
    private readonly Dictionary<Connection, TreeViewItem> _nodes = new(ReferenceEqualityComparer.Instance);
    private string[] _selectedFolder = [];
    private sealed record SessionTab(string Name, Control Body, EmbeddedSession? Host = null, bool Closable = true, CredentialBroker? Credentials = null);
    private readonly TextBox _name = new(), _host = new(), _user = new(), _domain = new(), _key = new();
    private readonly TextBox _jump = new() { PlaceholderText = "Alias Host dans ~/.ssh/config" }, _sharePath = new();
    private readonly CheckBox _share = new() { Content = "Partager ce dossier RDP en lecture/écriture" };
    private readonly StackPanel _rdpFields = new() { Spacing = 5, IsVisible = false };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly ComboBox _protocol = new() { ItemsSource = new[] { "SSH", "RDP", "Serial" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _baud = new() { Minimum = 50, Maximum = 4000000, Value = 9600 }, _bits = new() { Minimum = 5, Maximum = 8, Value = 8 }, _stop = new() { Minimum = 1, Maximum = 2, Value = 1 };
    private readonly ComboBox _parity = new() { ItemsSource = new[] { "none", "even", "odd" }, SelectedIndex = 0 }, _flow = new() { ItemsSource = new[] { "none", "hardware", "software" }, SelectedIndex = 0 };
    private readonly StackPanel _serialFields = new() { Spacing = 8, IsVisible = false };
    private readonly TextBox _search = new() { Watermark = "Rechercher une connexion…" };
    private readonly Button _sftp;
    private readonly CheckBox _clipboard = new() { Content = "Presse-papiers RDP bidirectionnel (texte)" };
    private readonly CheckBox _multi = new() { Content = "RDP sur plusieurs écrans" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private Connection? _editing;
    private bool _storeLoaded = true, _closing;
    public MainWindow()
    {
        Title = "MGRemote — Linux Desktop 0.2.3"; Width = 1280; Height = 850; MinWidth = 900; MinHeight = 600; FontSize = 12;
        Background = Brush.Parse("#eef1f5"); Foreground = Brush.Parse("#1f2937");
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = Background };
        var menus = new Menu();
        MenuItem Entry(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
        MenuItem Group(string title, params MenuItem[] items) => new() { Header = title, ItemsSource = items };
        menus.ItemsSource = new[] {
            Group("Fichier", Entry("Nouvelle connexion", New), Entry("Dupliquer la connexion", Duplicate), Entry("Importer mRemoteNG XML…", async () => await Import()), Entry("Enregistrer", Save), Entry("Quitter", Close)),
            Group("Sessions", Entry("Connecter", () => Launch(false)), Entry("Transfert SFTP", () => Launch(true)), Entry("Ouvrir en fenêtre externe", External), Entry("Fermer la session", () => CloseSession(_tabBar.SelectedItem as SessionTab))),
            Group("Affichage", Entry("Accueil", () => _tabBar.SelectedItem = _tabs.FirstOrDefault())),
            Group("Outils", Entry("Console série", () => { New(); _protocol.SelectedIndex = 2; _host.Focus(); }), Entry("Choisir une clé SSH…", async () => await ChooseKey())),
            Group("Aide", Entry("Informations", () => _tabBar.SelectedItem = _tabs.FirstOrDefault()))
        };
        root.Children.Add(menus);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 5) };
        toolbar.Children.Add(Button("＋ Connexion", New)); toolbar.Children.Add(Button("Connecter", () => Launch(false)));
        _sftp = Button("SFTP", () => Launch(true)); toolbar.Children.Add(_sftp); toolbar.Children.Add(Button("Importer…", async () => await Import()));
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,5,*") }; Grid.SetRow(body, 2);
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("3*,5,4*") };
        var treePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Background = Brushes.White };
        treePanel.Children.Add(new TextBlock { Text = "Connexions", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8) });
        _search.Margin = new Thickness(6); Grid.SetRow(_search, 1); treePanel.Children.Add(_search);
        Grid.SetRow(_list, 2); treePanel.Children.Add(_list); sidebar.Children.Add(treePanel);
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is TreeViewItem item) { if (item.Tag is Connection connection) { _selectedFolder = connection.FolderPath; Edit(connection); } else if (item.Tag is string[] path) { _selectedFolder = path; _editing = null; _name.Text = _host.Text = ""; _folder.Text = string.Join(" > ", path); } } };
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is TreeViewItem { Tag: Connection }) Launch(false); };
        _list.ContextMenu = new ContextMenu { ItemsSource = new[] { Entry("Connecter", () => Launch(false)), Entry("Modifier", () => _name.Focus()), Entry("Dupliquer", Duplicate), Entry("SFTP", () => Launch(true)), Entry("Supprimer", async () => await Delete()) } };
        _search.TextChanged += (_, _) => Filter();
        var horizontal = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(horizontal, 1); sidebar.Children.Add(horizontal);
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Background = Brushes.White };
        editor.Children.Add(new TextBlock { Text = "Configuration", FontWeight = FontWeight.SemiBold, Margin = new Thickness(8) });
        var fields = new StackPanel { Spacing = 5, Margin = new Thickness(6) };
        var displayFields = new StackPanel { Spacing = 5 };
        fields.Children.Add(new Expander { Header = "Affichage", IsExpanded = true, Content = displayFields, HorizontalAlignment = HorizontalAlignment.Stretch });
        Add(displayFields, "Description", _description);
        Add(displayFields, "Nom", _name); Add(displayFields, "Dossier", _folder); _folder.PlaceholderText = "Groupe > Sous-groupe";
        var connectionFields = new StackPanel { Spacing = 5 };
        fields.Children.Add(new Expander { Header = "Connexion", IsExpanded = true, Content = connectionFields, HorizontalAlignment = HorizontalAlignment.Stretch });
        Add(connectionFields, "Protocole", _protocol); Add(connectionFields, "Hôte / IP / port série", _host); Add(connectionFields, "Port réseau", _port);
        Add(connectionFields, "Utilisateur", _user); Add(connectionFields, "Domaine RDP", _domain); Add(connectionFields, "Clé privée SSH", _key);
        Add(connectionFields, "Bastion SSH", _jump);
        connectionFields.Children.Add(Button("Choisir une clé…", async () => await ChooseKey()));
        Add(_serialFields, "Bauds", _baud); Add(_serialFields, "Bits", _bits); Add(_serialFields, "Parité", _parity); Add(_serialFields, "Stop", _stop); Add(_serialFields, "Flux", _flow); fields.Children.Add(_serialFields);
        fields.Children.Add(_clipboard); fields.Children.Add(_multi);
        _rdpFields.Children.Add(_share); Add(_rdpFields, "Dossier partagé", _sharePath); _rdpFields.Children.Add(Button("Choisir le dossier RDP…", async () => await ChooseRdpFolder()));
        Add(_rdpFields, "Passerelle RDP", _gateway); Add(_rdpFields, "Mode passerelle", _gatewayMode);
        Add(_rdpFields, "Couleurs (bits)", _colors); Add(_rdpFields, "Son distant", _audio); _rdpFields.Children.Add(_console);
        _rdpFields.Children.Add(new TextBlock { Text = "Le serveur pourra lire et modifier ce dossier. Accès distant : \\" + "\\tsclient\\MGRemote. Aucun partage automatique des lecteurs.", TextWrapping = TextWrapping.Wrap }); fields.Children.Add(new Expander { Header = "Options RDP", IsExpanded = false, Content = _rdpFields, HorizontalAlignment = HorizontalAlignment.Stretch });
        fields.Children.Add(Button("Secret importé…", async () => await RevealImportedSecret()));
        var scroll = new ScrollViewer { Content = fields }; Grid.SetRow(scroll, 1); editor.Children.Add(scroll);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6), Spacing = 5 }; editActions.Children.Add(Button("Enregistrer", Save)); editActions.Children.Add(Button("Supprimer", async () => await Delete()));
        Grid.SetRow(editActions, 2); editor.Children.Add(editActions); Grid.SetRow(editor, 2); sidebar.Children.Add(editor);
        body.Children.Add(sidebar);
        var vertical = new GridSplitter { Width = 5, VerticalAlignment = VerticalAlignment.Stretch }; Grid.SetColumn(vertical, 1); body.Children.Add(vertical);
        var sessions = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = Brush.Parse("#f7f8fa") };
        _tabBar.ItemsSource = _tabs;
        _tabBar.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SessionTab>((tab, _) => {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = tab!.Name, VerticalAlignment = VerticalAlignment.Center });
            if (tab.Closable) { var close = new Button { Content = "×", Padding = new Thickness(5, 0) }; close.Click += (_, _) => CloseSession(tab); header.Children.Add(close); }
            return header;
        });
        _tabBar.SelectionChanged += (_, _) => { foreach (var tab in _tabs) tab.Body.IsVisible = ReferenceEquals(tab, _tabBar.SelectedItem); };
        sessions.Children.Add(_tabBar); Grid.SetRow(_sessionArea, 1); sessions.Children.Add(_sessionArea);
        Grid.SetColumn(sessions, 2); body.Children.Add(sessions); root.Children.Add(body);
        var welcome = new Border { Padding = new Thickness(28), Child = new TextBlock { Text = "MGRemote\n\nSélectionnez une connexion pour modifier ses propriétés.\nDouble-cliquez pour ouvrir une session dans un onglet.\n\nSSH / SFTP : clés OpenSSH, vérification de clé d’hôte, Ctrl+Maj+C/V.\nRDP : authentification NLA et vérification du certificat.\nSérie : paramètres du câble console et fermeture avec l’onglet.\n\nLa parité complète avec Windows reste en développement : VNC,\nlecteurs RDP et gestion avancée des bastions\nne sont pas encore équivalents. Le multi-écran RDP utilise le mode externe.", FontSize = 16, TextWrapping = TextWrapping.Wrap } };
        AddSession(new SessionTab("General", welcome, null, false));
        Grid.SetRow(_status, 3); _status.Margin = new Thickness(8); root.Children.Add(_status); Content = root;
        Closed += (_, _) => { _closing = true; foreach (var tab in _tabs.ToArray()) { tab.Host?.Dispose(); tab.Credentials?.Dispose(); (tab.Body as IDisposable)?.Dispose(); } };
        _protocol.SelectionChanged += (_, _) => {
            UpdateProtocol();
        };
        try { if (!App.Smoke) foreach (var c in Connections.Load()) _connections.Add(c); _status.Text = "Prêt — OpenSSH, FreeRDP et un terminal Linux sont requis pour les sessions."; }
        catch (Exception ex) { _storeLoaded = false; _status.Text = "Chargement : " + ex.Message + " — Écriture bloquée pour préserver le fichier existant."; }
        foreach (var input in new[] { _description, _name, _host, _user, _domain, _key, _search, _folder, _jump, _sharePath }) { input.Background = Brushes.White; input.Foreground = Foreground; input.FontSize = 12; }
        _list.Background = Brushes.White; _list.Foreground = Foreground; Filter();
        New();
    }
    private static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 4), Background = Brush.Parse("#2563eb"), Foreground = Brushes.White };
        button.Click += (_, _) => action(); return button;
    }
    private static void Add(Panel panel, string label, Control control) { var row = new Grid { ColumnDefinitions = new ColumnDefinitions("115,*") }; row.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(control, 1); row.Children.Add(control); panel.Children.Add(row); }
    private void New() { _encryptedPassword = ""; _gateway.Text = ""; _gatewayMode.SelectedIndex = 0; _description.Text = ""; _colors.SelectedItem = 32; _audio.SelectedIndex = 2; _console.IsChecked = false; _editing = null; _list.SelectedItem = null; _name.Text = "Nouvelle connexion"; _host.Text = _user.Text = _domain.Text = _key.Text = ""; _protocol.SelectedIndex = 0; _port.Value = 22; _clipboard.IsChecked = _multi.IsChecked = _share.IsChecked = false; _jump.Text = _sharePath.Text = ""; _folder.Text = string.Join(" > ", _selectedFolder); _baud.Value = 9600; _bits.Value = 8; _stop.Value = 1; _parity.SelectedIndex = _flow.SelectedIndex = 0; UpdateProtocol(); }
    private void UpdateProtocol()
    {
        bool ssh = _protocol.SelectedIndex == 0, rdp = _protocol.SelectedIndex == 1, serial = _protocol.SelectedIndex == 2;
        _key.IsEnabled = _jump.IsEnabled = ssh; _domain.IsEnabled = rdp; _clipboard.IsVisible = _multi.IsVisible = rdp; _rdpFields.IsVisible = rdp; _sftp.IsEnabled = ssh;
        _serialFields.IsVisible = serial; _port.IsEnabled = _user.IsEnabled = !serial;
        _host.Watermark = serial ? "/dev/ttyUSB0 ou /dev/serial/by-id/…" : "Serveur, adresse IP ou alias SSH";
        if (!serial && _port.Value is 22 or 3389) _port.Value = ssh ? 22 : 3389;
    }
    private void Filter() => RebuildTree();
    private void Edit(Connection c)
    {
        _encryptedPassword = c.EncryptedPassword; _gateway.Text = c.GatewayHost; _gatewayMode.SelectedItem = c.GatewayMode;
        _description.Text = c.Description; _colors.SelectedItem = c.ColorDepth; _audio.SelectedIndex = c.AudioMode; _console.IsChecked = c.ConsoleSession;
        _editing = c; _selectedFolder = c.FolderPath; _folder.Text = string.Join(" > ", c.FolderPath); _name.Text = c.Name; _host.Text = c.Host; _protocol.SelectedIndex = c.Protocol == "Serial" ? 2 : c.Protocol == "SSH" ? 0 : 1;
        _port.Value = c.Port; _user.Text = c.User; _domain.Text = c.Domain; _key.Text = c.Identity; _clipboard.IsChecked = c.Clipboard; _multi.IsChecked = c.MultiMonitor;
        _baud.Value = c.BaudRate; _bits.Value = c.DataBits; _stop.Value = c.StopBits; _parity.SelectedItem = c.Parity; _flow.SelectedItem = c.FlowControl;
        _jump.Text = c.JumpHost; _share.IsChecked = c.RdpShareEnabled; _sharePath.Text = c.RdpSharePath;
    }
    private Connection Read() => new() { EncryptedPassword = _encryptedPassword, GatewayHost = _gateway.Text?.Trim() ?? "", GatewayMode = _gatewayMode.SelectedItem?.ToString() ?? "direct", Description = _description.Text ?? "", ColorDepth = _colors.SelectedItem is int depth ? depth : 32, AudioMode = _audio.SelectedIndex, ConsoleSession = _console.IsChecked == true, Name = _name.Text?.Trim() ?? "", Host = _host.Text?.Trim() ?? "", Protocol = _protocol.SelectedItem?.ToString() ?? "", Port = (int)(_port.Value ?? 0), User = _user.Text?.Trim() ?? "", Domain = _domain.Text?.Trim() ?? "", Identity = _key.Text?.Trim() ?? "", Clipboard = _clipboard.IsChecked == true, MultiMonitor = _multi.IsChecked == true, BaudRate = (int)(_baud.Value ?? 0), DataBits = (int)(_bits.Value ?? 0), StopBits = (int)(_stop.Value ?? 0), Parity = _parity.SelectedItem?.ToString() ?? "", FlowControl = _flow.SelectedItem?.ToString() ?? "", FolderPath = ReadFolderPath(), JumpHost = _jump.Text?.Trim() ?? "", RdpShareEnabled = _share.IsChecked == true, RdpSharePath = _sharePath.Text?.Trim() ?? "" };
    internal void VerifyProtocolPortEditing()
    {
        _editing = new Connection(); _protocol.SelectedIndex = 0; _port.Value = 22;
        _protocol.SelectedIndex = 1;
        if (_port.Value != 3389) throw new Exception("Editing SSH to RDP did not update the default port.");
        _port.Value = 3391; _protocol.SelectedIndex = 0;
        if (_port.Value != 3391) throw new Exception("Changing protocol overwrote a custom port.");
        _protocol.SelectedIndex = 2;
        if (!_serialFields.IsVisible || _sftp.IsEnabled) throw new Exception("Serial UI mode is invalid.");
        var fixture = new Connection { Name = "Options", Host = "server", Protocol = "RDP", Port = 3389, Description = "Description importée", ColorDepth = 16, AudioMode = 1, ConsoleSession = true, Clipboard = true };
        Edit(fixture); var edited = Read();
        if (edited.Description != fixture.Description || edited.ColorDepth != 16 || edited.AudioMode != 1 || !edited.ConsoleSession || !edited.Clipboard) throw new Exception("RDP property editing lost imported values.");
        New(); if (Read().Description != "" || Read().AudioMode != 2 || Read().ConsoleSession) throw new Exception("New connection retained previous properties.");
        string previousStatus = _status.Text ?? ""; _storeLoaded = false; Save();
        if (!_status.Text!.Contains("non chargé")) throw new Exception("Failed-load persistence guard is missing.");
        _storeLoaded = true; _status.Text = previousStatus;
        New();
    }
    private void Save()
    {
        try {
            if (!_storeLoaded) throw new IOException("Fichier existant non chargé : corrigez-le puis relancez MGRemote. Enregistrement bloqué pour préserver vos données.");
            var item = Read(); Connections.Validate(item);
            var next = _connections.ToList(); int index = _editing == null ? -1 : next.FindIndex(c => ReferenceEquals(c, _editing));
            if (index < 0) next.Add(item); else next[index] = item;
            Connections.Save(next); _connections.Clear(); foreach (var c in next) _connections.Add(c);
            Filter(); if (_nodes.TryGetValue(item, out var node)) _list.SelectedItem = node; Edit(item); _status.Text = "Connexion enregistrée ; les secrets importés restent chiffrés.";
        } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task ChooseKey()
    {
        try { var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Clé privée SSH — ne partagez jamais ce fichier", AllowMultiple = false }); if (files.Count > 0) _key.Text = files[0].TryGetLocalPath(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task Import()
    {
        try {
            if (!_storeLoaded) throw new IOException("Fichier existant non chargé : import bloqué pour préserver vos données. Corrigez-le puis relancez l’application.");
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Importer mRemoteNG XML", AllowMultiple = false });
            if (files.Count == 0) return;
            var keys = await AskImportKeys(); if (keys == null) return;
            var result = await Task.Run(() => { var items = Connections.Import(files[0].TryGetLocalPath() ?? throw new IOException("Fichier local requis."), out int ignored, keys.Source, keys.Local); return (Items: items, Ignored: ignored); });
            var imported = result.Items;
            Connections.Save(_connections.Concat(imported)); foreach (var c in imported) _connections.Add(c);
            Filter(); _status.Text = $"{imported.Count} connexions importées ; {result.Ignored} protocoles non pris en charge ignorés. Héritage résolu ; secrets réencryptés si demandés. Certaines options Windows restent non prises en charge. Vérifiez les valeurs avant connexion.";
        } catch (Exception ex) { _status.Text = "Import annulé, connexions existantes conservées : " + ex.Message; }
    }
}
