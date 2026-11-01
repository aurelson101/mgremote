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
        if (args.Length > 0 && args[0] == "--session-runner") { Environment.ExitCode = SessionExit.Run(args); return; }
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        if (args.Contains("--terminal-fixture")) {
            string? report = args.FirstOrDefault(a => a.StartsWith("--fixture-output="));
            Console.Write("\x1b[32mMGRemote embedded terminal fixture\x1b[0m\r\n");
            while (Console.ReadLine() is string line) { if (report != null) File.AppendAllText(report["--fixture-output=".Length..], line + "\n"); Console.WriteLine(line); } return;
        }
        string? serialPlan = args.FirstOrDefault(a => a.StartsWith("--serial-plan=", StringComparison.Ordinal));
        if (serialPlan != null) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(SessionLauncher.Arguments(new Connection { Protocol = "Serial", Host = serialPlan[14..] }))); return; }
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
public sealed class App : Application
{
    internal static bool Smoke => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--smoke-ui="));
    public override void Initialize()
    {
        var theme = new FluentTheme();
        theme.Palettes[Avalonia.Styling.ThemeVariant.Light] = new ColorPaletteResources { Accent = Color.Parse("#62788d") };
        Styles.Add(theme); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; DesktopAppearance.Apply(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new MainWindow();
            if (Smoke) Avalonia.Threading.DispatcherTimer.RunOnce(async () => {
                try {
                    var window = desktop.MainWindow;
                    if (!Environment.GetCommandLineArgs().Contains("--smoke-sessions")) {
                        ((MainWindow)window).VerifyProtocolPortEditing();
                        ((MainWindow)window).VerifyTreeActions();
                        await ((MainWindow)window).VerifyTreeState();
                        await ((MainWindow)window).VerifyProfileUi();
                        await ((MainWindow)window).VerifyPortableProfileUi();
                        await ((MainWindow)window).VerifyKeySelection();
                        await ((MainWindow)window).VerifyProfileWorkflowUi();
                        await ((MainWindow)window).VerifySessionTransferTools();
                        ((MainWindow)window).VerifyTabClosing();
                        await ((MainWindow)window).VerifyDesktopUi();
                        await ((MainWindow)window).VerifyEmbeddedSessions();
                    }
                    await ((MainWindow)window).VerifySessionStability();
                    ((MainWindow)window).PrepareSmokePreview();
                    await Task.Delay(150);
                    if (window.Bounds.Width < 760 || window.Bounds.Height < 500) throw new Exception("Window layout failed.");
                    using var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)(window.Bounds.Width * window.RenderScaling), (int)(window.Bounds.Height * window.RenderScaling)), new Vector(96 * window.RenderScaling, 96 * window.RenderScaling));
                    image.Render((Visual)window.Content!);
                    image.Save(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..], new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    await ((MainWindow)window).VerifyCompactLayout(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..].Replace(".png", "-compact.png"));
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
    private readonly TextBox _description = new() { AcceptsReturn = true, MinHeight = 42, MaxHeight = 64, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _colors = new() { ItemsSource = new[] { 8, 15, 16, 24, 32 }, SelectedIndex = 4 };
    private readonly ComboBox _audio = new() { ItemsSource = new[] { "Sur cet ordinateur", "Sur le serveur", "Désactivé" }, SelectedIndex = 2 };
    private readonly CheckBox _console = new() { Content = "Session d’administration RDP (/admin)" };
    private readonly ObservableCollection<SessionTab> _tabs = [];
    private readonly Avalonia.Controls.Primitives.TabStrip _tabBar = new() { Height = 30, FontSize = 12 };
    private readonly Grid _sessionArea = new();
    private readonly Dictionary<Connection, TreeViewItem> _nodes = new(ReferenceEqualityComparer.Instance);
    private string[] _selectedFolder = [];
    private sealed record SessionTab(string Name, Control Body, EmbeddedSession? Host = null, bool Closable = true, CredentialBroker? Credentials = null, Connection? Connection = null);
    private readonly TextBox _name = new(), _host = new(), _user = new(), _domain = new(), _key = new();
    private readonly TextBox _jump = new() { PlaceholderText = "Alias Host dans ~/.ssh/config" }, _sharePath = new();
    private readonly CheckBox _share = new() { Content = "Partager ce dossier RDP en lecture/écriture" };
    private readonly StackPanel _rdpFields = new() { Spacing = 0, IsVisible = false };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly ComboBox _protocol = new() { ItemsSource = new[] { "SSH", "RDP", "Serial" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _baud = new() { Minimum = 50, Maximum = 4000000, Value = 9600 }, _bits = new() { Minimum = 5, Maximum = 8, Value = 8 }, _stop = new() { Minimum = 1, Maximum = 2, Value = 1 };
    private readonly ComboBox _parity = new() { ItemsSource = new[] { "none", "even", "odd" }, SelectedIndex = 0 }, _flow = new() { ItemsSource = new[] { "none", "hardware", "software" }, SelectedIndex = 0 };
    private readonly StackPanel _serialFields = new() { Spacing = 0, IsVisible = false };
    private readonly TextBox _search = new() { PlaceholderText = "Rechercher une connexion…" };
    private readonly Button _sftp, _connect;
    private readonly CheckBox _clipboard = new() { Content = "Presse-papiers RDP bidirectionnel" };
    private readonly CheckBox _multi = new() { Content = "RDP sur plusieurs écrans" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private Connection? _editing;
    private bool _storeLoaded = true, _closing;
    public MainWindow()
    {
        _defaultProfilePath = _dataPath;
        Title = "MGRemote — Linux Desktop 0.2.20"; Width = 1280; Height = 850; MinWidth = 900; MinHeight = 600; FontSize = 13;
        Background = Workspace; Foreground = DesktopAppearance.Text;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = Workspace };
        var menus = new Menu(); menus.Classes.Add("desktop-menu");
        MenuItem Entry(string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); return item; }
        MenuItem Group(string title, params MenuItem[] items) => new() { Header = title, ItemsSource = items };
        menus.ItemsSource = new[] {
            Group("Fichier", Entry("Nouvelle connexion", NewConnectionFromTree), Entry("Dupliquer la connexion", Duplicate), Entry("Importer mRemoteNG XML…", async () => await Import()), _importProfileItem, _exportProfileItem, Entry("Enregistrer", Save), Entry("Changer / gérer les profils…", async () => await SwitchProfile()), Entry("Chiffrer / déverrouiller le profil…", async () => await ManageProfile()), _lockProfileItem, Entry("Quitter", Close)),
            Group("Sessions", Entry("Connecter", () => Launch(false)), Entry("Transfert SFTP", () => Launch(true)), Entry("Ouvrir en fenêtre externe", External), Entry("Fermer la session", () => CloseSession(_tabBar.SelectedItem as SessionTab))),
            Group("Affichage", Entry("Accueil", () => _tabBar.SelectedItem = _tabs.FirstOrDefault())),
            Group("Outils", Entry("Console série", () => { New(); _protocol.SelectedIndex = 2; _host.Focus(); }), Entry("Choisir une clé SSH…", async () => await ChooseKey())),
            Group("Aide", Entry("Guide d’utilisation", ShowHelp), Entry("À propos…", async () => await ShowAbout()))
        };
        root.Children.Add(menus);
        _lockProfileItem.Click += async (_, _) => await LockProfile();
        _exportProfileItem.Click += async (_, _) => await ExportPortableProfile(); _importProfileItem.Click += async (_, _) => await ImportPortableProfile();
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(8, 6) }; toolbar.Classes.Add("toolstrip");
        toolbar.Children.Add(ToolButton("Connexion", "Nouvelle connexion · Ctrl+N", "M2 2H13V18H2Z M6 7H17 M11 2V12", NewConnectionFromTree));
        toolbar.Children.Add(ToolButton("Dossier", "Nouveau dossier", "M2 5H8L10 7H20V18H2Z", async () => await CreateFolderDialog()));
        toolbar.Children.Add(new Border { Width = 1, Height = 18, Background = PanelLine, Margin = new Thickness(5, 0) });
        _connect = ToolButton("Connecter", "Ouvrir la connexion sélectionnée", "M6 3L17 10L6 17Z", () => Launch(false)); _connect.Classes.Add("primary-action"); toolbar.Children.Add(_connect);
        _sftp = ToolButton("SFTP", "Transfert de fichiers", "M3 5H19 M15 1L19 5L15 9 M19 15H3 M7 11L3 15L7 19", () => Launch(true)); toolbar.Children.Add(_sftp);
        toolbar.Children.Add(ToolButton("Importer", "Importer des connexions mRemoteNG", "M10 2V13 M5 8L10 13L15 8 M2 14V19H18V14", async () => await Import()));
        Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("360,4,*") }; Grid.SetRow(body, 2);
        body.ColumnDefinitions[0].MinWidth = 320; body.ColumnDefinitions[2].MinWidth = 420;
        var sidebar = new Grid { RowDefinitions = new RowDefinitions("2*,4,3*") };
        var treePanel = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = Brushes.White };
        treePanel.Children.Add(DockTitle("Connexions", true));
        var treeStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(3, 1), Background = Brush.Parse("#f6f6f8") }; treeStrip.Classes.Add("toolstrip");
        void Strip(string tip, string path, Action action) { var b = Button("", action); b.Content = ToolbarGlyph(path); b.MinWidth = 28; b.Padding = new Thickness(5, 3); ToolTip.SetTip(b, tip); Avalonia.Automation.AutomationProperties.SetName(b, tip); treeStrip.Children.Add(b); }
        Strip("Nouvelle connexion", "M2 2H13V18H2Z M6 7H17 M11 2V12", NewConnectionFromTree); Strip("Nouveau dossier", "M2 5H8L10 7H20V18H2Z", async () => await CreateFolderDialog());
        void SetExpanded(bool open) { foreach (var node in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(_list).OfType<TreeViewItem>().ToArray()) node.IsExpanded = open; }
        Strip("Tout développer", "M3 10H17 M10 3V17", () => SetExpanded(true)); Strip("Tout réduire", "M3 10H17", () => SetExpanded(false));
        Grid.SetRow(treeStrip, 1); treePanel.Children.Add(treeStrip);
        _search.Margin = new Thickness(0); _search.MinHeight = 26; _search.FontSize = 13; _search.BorderThickness = new Thickness(0, 1, 0, 0); _search.CornerRadius = new CornerRadius(0); Grid.SetRow(_search, 3); treePanel.Children.Add(_search);
        Grid.SetRow(_list, 2); treePanel.Children.Add(_list); sidebar.Children.Add(treePanel);
        _list.SelectionChanged += (_, _) => {
            if (_rebuildingTree) return;
            var item = _list.SelectedItem as TreeViewItem;
            var previous = _editing;
            if (!PrepareEditorChange()) return;
            if (item != null) {
                if (item.Tag is Connection connection) {
                    if (ReferenceEquals(connection, previous)) connection = _editing ?? connection;
                    _rebuildingTree = true;
                    try { if (_nodes.TryGetValue(connection, out var live)) _list.SelectedItem = live; }
                    finally { _rebuildingTree = false; }
                    Edit(connection);
                }
                else if (item.Tag is string[] path) {
                    _rebuildingTree = true;
                    try { if (_folderNodes.TryGetValue(FolderKey(path), out var live)) _list.SelectedItem = live; }
                    finally { _rebuildingTree = false; }
                    _hydratingEditor = true;
                    try { _selectedFolder = path; _editing = null; _name.Text = _host.Text = ""; _folder.Text = string.Join(" > ", path); }
                    finally { _hydratingEditor = false; }
                }
            }
        };
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is TreeViewItem { Tag: Connection }) Launch(false); };
        InstallTreeMenu();
        _search.TextChanged += (_, _) => RequestFilter();
        var horizontal = new GridSplitter { Height = 4, Background = PanelLine, HorizontalAlignment = HorizontalAlignment.Stretch }; Grid.SetRow(horizontal, 1); sidebar.Children.Add(horizontal);
        var editor = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Background = Brushes.White };
        _profileEditor = editor;
        editor.Classes.Add("properties"); _configurationTitle.Foreground = Brush.Parse("#2b3a55"); _configurationTitle.FontWeight = FontWeight.Normal; editor.Children.Add(DockCaption(_configurationTitle, false));
        var propertyToolbar = PropertyToolbar(); Grid.SetRow(propertyToolbar, 1); editor.Children.Add(propertyToolbar);
        var fields = new StackPanel { Spacing = 0 };
        var displayFields = new StackPanel { Spacing = 0 };
        AddPropertyGroup(fields, "Général", displayFields);
        Add(displayFields, "Nom", _name); Add(displayFields, "Dossier", _folder); _folder.PlaceholderText = "Groupe > Sous-groupe";
        Add(displayFields, "Icône", _icon); Add(displayFields, "Description", _description);
        var connectionFields = new StackPanel { Spacing = 0 };
        AddPropertyGroup(fields, "Connexion", connectionFields);
        Add(connectionFields, "Protocole", _protocol); Add(connectionFields, "Hôte / IP / série", _host); Add(connectionFields, "Port réseau", _port);
        Add(connectionFields, "Utilisateur", _user); Add(connectionFields, "Domaine RDP", _domain);
        _sshSection.Content = BuildSshProperties(); CompactCategory(_sshSection); _propertyGroups.Add(_sshSection); fields.Children.Add(_sshSection);
        Add(_serialFields, "Bauds", _baud); Add(_serialFields, "Bits", _bits); Add(_serialFields, "Parité", _parity); Add(_serialFields, "Stop", _stop); Add(_serialFields, "Flux", _flow); fields.Children.Add(_serialFields);
        _rdpFields.Children.Add(_clipboard); _rdpFields.Children.Add(_rdpFileClipboard); _rdpFields.Children.Add(_multi);
        _rdpFileClipboard.IsEnabled = _clipboard.IsChecked == true;
        ToolTip.SetTip(_rdpFileClipboard, "Nécessite xfreerdp3. Pour les dossiers et les clients plus anciens, utilisez le dossier partagé. Les stratégies du serveur peuvent limiter les échanges.");
        _clipboard.IsCheckedChanged += (_, _) => { _rdpFileClipboard.IsEnabled = _clipboard.IsChecked == true; if (_clipboard.IsChecked != true) _rdpFileClipboard.IsChecked = false; };
        Add(_rdpFields, "Résolution", _resolution); Add(_rdpFields, "Largeur", _rdpWidth); Add(_rdpFields, "Hauteur", _rdpHeight);
        _resolution.SelectionChanged += (_, _) => UpdateAdvancedProperties(); _multi.IsCheckedChanged += (_, _) => UpdateAdvancedProperties();
        _rdpFields.Children.Add(_share); Add(_rdpFields, "Dossier partagé", BrowseField(_sharePath, "Choisir le dossier partagé RDP", async () => await ChooseRdpFolder()));
        Add(_rdpFields, "Passerelle RDP", _gateway); Add(_rdpFields, "Mode passerelle", _gatewayMode);
        Add(_rdpFields, "Couleurs (bits)", _colors); Add(_rdpFields, "Son distant", _audio); _rdpFields.Children.Add(_console);
        _rdpFields.Children.Add(new TextBlock { Text = "Le serveur pourra lire et modifier ce dossier. Accès distant : \\" + "\\tsclient\\MGRemote. Aucun partage automatique des lecteurs.", TextWrapping = TextWrapping.Wrap });
        _rdpSection.Content = _rdpFields; CompactCategory(_rdpSection); _propertyGroups.Add(_rdpSection); fields.Children.Add(_rdpSection);
        fields.Children.Add(Button("Secret importé…", async () => await RevealImportedSecret()));
        var scroll = new ScrollViewer { Content = fields, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); editor.Children.Add(scroll);
        var editActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(5, 4), Spacing = 4 }; editActions.Classes.Add("toolstrip"); editActions.Children.Add(Button("Enregistrer", Save)); editActions.Children.Add(Button("Supprimer", async () => await Delete()));
        Grid.SetRow(editActions, 3); editor.Children.Add(editActions); Grid.SetRow(editor, 2); sidebar.Children.Add(editor);
        body.Children.Add(sidebar);
        var vertical = new GridSplitter { Width = 4, Background = PanelLine, VerticalAlignment = VerticalAlignment.Stretch }; Grid.SetColumn(vertical, 1); body.Children.Add(vertical);
        var sessions = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Background = Workspace };
        _tabBar.ItemsSource = _tabs;
        _tabBar.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SessionTab>((tab, _) => {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(new TextBlock { Text = tab!.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (tab.Closable) { var close = new Button { Content = "×", Padding = new Thickness(5, 0) }; ToolTip.SetTip(close, "Fermer « " + tab.Name + " »"); Avalonia.Automation.AutomationProperties.SetName(close, "Fermer « " + tab.Name + " »"); close.Click += (_, e) => { e.Handled = true; CloseSession(tab); }; header.Children.Add(close); }
            return header;
        });
        _tabBar.SelectionChanged += (_, _) => { foreach (var tab in _tabs) tab.Body.IsVisible = ReferenceEquals(tab, _tabBar.SelectedItem); };
        sessions.Children.Add(new Border { Background = DesktopAppearance.Chrome, BorderBrush = PanelLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = _tabBar }); Grid.SetRow(_sessionArea, 1); sessions.Children.Add(_sessionArea);
        Grid.SetColumn(sessions, 2); body.Children.Add(sessions); root.Children.Add(body);
        AddSession(new SessionTab("Accueil", EmptyWorkspace(), null, false));
        var statusTip = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
        statusTip.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(TextBlock.Text)) { Source = _status }); ToolTip.SetTip(_status, statusTip);
        _status.Margin = new Thickness(10, 0); _status.FontSize = 12;
        var statusBar = new Border { Background = Brushes.White, BorderBrush = PanelLine, BorderThickness = new Thickness(0, 1, 0, 0), Child = new StackPanel { Children = { DockTitle("Notifications"), new Border { Height = 28, Child = _status } } } };
        Grid.SetRow(statusBar, 3); root.Children.Add(statusBar); Content = root;
        Closed += (_, _) => { _closing = true; ForgetTreeState(); _profileKey?.Dispose(); foreach (var tab in _tabs.ToArray()) { tab.Host?.Dispose(); tab.Credentials?.Dispose(); (tab.Body as IDisposable)?.Dispose(); } CleanupSmokeStore(); };
        _protocol.SelectionChanged += (_, _) => {
            UpdateProtocol();
        };
        if (App.Smoke) _status.Text = "Prêt — validation locale.";
        else { _storeLoaded = false; Opened += async (_, _) => await InitializeProfile(); }
        foreach (var input in new[] { _description, _name, _host, _user, _domain, _key, _search, _folder, _jump, _sharePath }) { input.Background = Brushes.White; input.Foreground = Foreground; input.FontSize = 13; }
        _list.Background = Brushes.White; _list.Foreground = Foreground; Filter();
        InstallShortcuts(); New(); UpdateProfileActions(); InstallAutoSave();
    }
    private static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0), Padding = new Thickness(8, 3), MinHeight = 26 };
        button.Click += (_, _) => action(); return button;
    }
    private static void Add(Panel panel, string label, Control control)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("128,*"), MinHeight = 30 };
        row.Children.Add(new Border { Background = Brush.Parse("#f5f6f8"), BorderBrush = PanelLine, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(8, 3), Child = new TextBlock { Text = label, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } });
        ToolTip.SetTip(row.Children[0], label);
        control.HorizontalAlignment = HorizontalAlignment.Stretch; control.VerticalAlignment = VerticalAlignment.Stretch; Grid.SetColumn(control, 1); row.Children.Add(control);
        panel.Children.Add(new Border { BorderBrush = Brush.Parse("#e7eaee"), BorderThickness = new Thickness(0, 0, 0, 1), Child = row });
    }
    private void New()
    {
        if (!PrepareEditorChange()) return;
        _hydratingEditor = true;
        try { _encryptedPassword = ""; _gateway.Text = ""; _gatewayMode.SelectedIndex = 0; _description.Text = ""; _colors.SelectedItem = 32; _audio.SelectedIndex = 2; _console.IsChecked = false; _editing = null; _list.SelectedItem = null; _name.Text = "Nouvelle connexion"; _host.Text = _user.Text = _domain.Text = _key.Text = ""; _protocol.SelectedIndex = 0; _port.Value = 22; _clipboard.IsChecked = _multi.IsChecked = _share.IsChecked = false; _jump.Text = _sharePath.Text = ""; _folder.Text = string.Join(" > ", _selectedFolder); _baud.Value = 9600; _bits.Value = 8; _stop.Value = 1; _parity.SelectedIndex = _flow.SelectedIndex = 0; ResetAdvancedProperties(); UpdateProtocol(); }
        finally { _hydratingEditor = false; }
    }
    private void UpdateProtocol()
    {
        bool ssh = _protocol.SelectedIndex == 0, rdp = _protocol.SelectedIndex == 1, serial = _protocol.SelectedIndex == 2;
        _key.IsEnabled = _jump.IsEnabled = ssh; _domain.IsEnabled = rdp; _clipboard.IsVisible = _multi.IsVisible = rdp; _rdpFields.IsVisible = rdp; _connect.IsEnabled = _storeLoaded && !_profileBusy; _sftp.IsEnabled = ssh && _connect.IsEnabled;
        UpdateAdvancedProperties();
        _serialFields.IsVisible = serial; _port.IsEnabled = _user.IsEnabled = !serial;
        _host.PlaceholderText = serial ? "/dev/ttyUSB0 ou /dev/serial/by-id/…" : "Serveur, adresse IP ou alias SSH";
        if (!serial && _port.Value is 22 or 3389) _port.Value = ssh ? 22 : 3389;
    }
    private void Filter() => RebuildTree();
    private void Edit(Connection c)
    {
        if (!PrepareEditorChange()) return;
        _hydratingEditor = true;
        try {
        _encryptedPassword = c.EncryptedPassword; _gateway.Text = c.GatewayHost; _gatewayMode.SelectedItem = c.GatewayMode;
        _description.Text = c.Description; _colors.SelectedItem = c.ColorDepth; _audio.SelectedIndex = c.AudioMode; _console.IsChecked = c.ConsoleSession;
        _editing = c; _selectedFolder = c.FolderPath; _folder.Text = string.Join(" > ", c.FolderPath); _name.Text = c.Name; _host.Text = c.Host; _protocol.SelectedIndex = c.Protocol == "Serial" ? 2 : c.Protocol == "SSH" ? 0 : 1;
        _port.Value = c.Port; _user.Text = c.User; _domain.Text = c.Domain; _key.Text = c.Identity; _clipboard.IsChecked = c.Clipboard; _multi.IsChecked = c.MultiMonitor;
        _baud.Value = c.BaudRate; _bits.Value = c.DataBits; _stop.Value = c.StopBits; _parity.SelectedItem = c.Parity; _flow.SelectedItem = c.FlowControl;
        _jump.Text = c.JumpHost; _share.IsChecked = c.RdpShareEnabled; _sharePath.Text = c.RdpSharePath; EditAdvancedProperties(c);
        } finally { _hydratingEditor = false; }
    }
    private Connection Read() => ReadAdvancedProperties(new Connection { IsDraft = _editing?.IsDraft == true && string.IsNullOrWhiteSpace(_host.Text), EncryptedPassword = _encryptedPassword, GatewayHost = _gateway.Text?.Trim() ?? "", GatewayMode = _gatewayMode.SelectedItem?.ToString() ?? "direct", Description = _description.Text ?? "", ColorDepth = _colors.SelectedItem is int depth ? depth : 32, AudioMode = _audio.SelectedIndex, ConsoleSession = _console.IsChecked == true, Name = _name.Text?.Trim() ?? "", Host = _host.Text?.Trim() ?? "", Protocol = _protocol.SelectedItem?.ToString() ?? "", Port = (int)(_port.Value ?? 0), User = _user.Text?.Trim() ?? "", Domain = _domain.Text?.Trim() ?? "", Identity = _key.Text?.Trim() ?? "", Clipboard = _clipboard.IsChecked == true, MultiMonitor = _multi.IsChecked == true, BaudRate = (int)(_baud.Value ?? 0), DataBits = (int)(_bits.Value ?? 0), StopBits = (int)(_stop.Value ?? 0), Parity = _parity.SelectedItem?.ToString() ?? "", FlowControl = _flow.SelectedItem?.ToString() ?? "", FolderPath = ReadFolderPath(), JumpHost = _jump.Text?.Trim() ?? "", RdpShareEnabled = _share.IsChecked == true, RdpSharePath = _sharePath.Text?.Trim() ?? "" });
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
        var advanced = fixture with { Icon = "Linux", RdpResolution = "fixed", RdpWidth = 1600, RdpHeight = 900 };
        Edit(advanced); edited = Read();
        if (edited.Icon != "Linux" || edited.RdpResolution != "fixed" || edited.RdpWidth != 1600 || edited.RdpHeight != 900 || !_rdpWidth.IsEnabled) throw new Exception("Advanced RDP properties did not round trip.");
        advanced = new Connection { Host = "ssh.example", CertificateFile = "/tmp/key-cert.pub", TunnelEnabled = true, TunnelLocalPort = 15432, TunnelRemoteHost = "db.internal", TunnelRemotePort = 5432 };
        Edit(advanced); edited = Read();
        if (edited.CertificateFile != advanced.CertificateFile || !edited.TunnelEnabled || edited.TunnelLocalPort != 15432 || edited.TunnelRemoteHost != "db.internal" || edited.TunnelRemotePort != 5432 || !_sshSection.IsVisible || _rdpSection.IsVisible) throw new Exception("Advanced SSH properties did not round trip.");
        New(); if (Read().Description != "" || Read().AudioMode != 2 || Read().ConsoleSession) throw new Exception("New connection retained previous properties.");
        if (Read().TunnelEnabled || Read().CertificateFile.Length > 0 || Read().Icon != "Auto" || Read().RdpResolution != "dynamic") throw new Exception("New connection retained advanced properties.");
        string previousStatus = _status.Text ?? ""; _storeLoaded = false; Save();
        if (!_status.Text!.Contains("non chargé")) throw new Exception("Failed-load persistence guard is missing.");
        _storeLoaded = true; _status.Text = previousStatus;
        New();
    }
    private void Save() => SaveEditor();
    private bool SaveEditor(bool automatic = false)
    {
        _savingEditor = true;
        try {
            if (!_storeLoaded) throw new IOException("Fichier existant non chargé : corrigez-le puis relancez MGRemote. Enregistrement bloqué pour préserver vos données.");
            if (_profileBusy) throw new IOException("Une opération de profil est en cours. Attendez sa fin avant d’enregistrer.");
            var item = Read(); Connections.Validate(item, allowDraft: true);
            var next = _connections.ToList(); int index = _editing == null ? -1 : next.FindIndex(c => ReferenceEquals(c, _editing));
            if (index < 0) next.Add(item); else next[index] = item;
            var folders = _folders.Concat(Enumerable.Range(1, item.FolderPath.Length).Select(length => item.FolderPath.Take(length).ToArray())).DistinctBy(FolderKey).ToArray();
            Persist(next, folders); _folders.Clear(); _folders.AddRange(folders); _connections.Clear(); foreach (var c in next) _connections.Add(c);
            CancelAutoSave(); _editing = item; RememberTreeSelection(item); Filter();
            if (!automatic) { if (_nodes.TryGetValue(item, out var node)) _list.SelectedItem = node; Edit(item); }
            _status.Text = automatic ? "Modifications enregistrées automatiquement." : "Connexion enregistrée ; les secrets importés restent chiffrés.";
            return true;
        } catch (Exception ex) { _status.Text = (automatic ? "Non enregistré : " : "") + ex.Message; return false; }
        finally { _savingEditor = false; }
    }
    private Window? _keyPicker;
    private async Task ChooseKey(Action<Window>? shown = null)
    {
        if (_keyPicker != null) { _keyPicker.Activate(); return; }
        var dialog = new Window { Title = "Clé privée SSH", Width = 540, Height = 330, MinWidth = 420, MinHeight = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        _keyPicker = dialog;
        bool closed = false; Task? pending = null;
        var path = new TextBox { Text = _key.Text, PlaceholderText = "~/.ssh/id_ed25519" };
        Avalonia.Automation.AutomationProperties.SetName(path, "Chemin de la clé privée SSH");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Saisissez le chemin de votre clé privée OpenSSH/PEM ou choisissez un fichier. Une clé publique (.pub) ne permet pas de vous authentifier.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Chemin de la clé privée" }); panel.Children.Add(path);
        var browse = new Button { Content = "Parcourir…" };
        browse.Click += async (_, _) => {
            browse.IsEnabled = false;
            error.Text = "Sélecteur système ouvert. S’il ne répond pas, fermez-le et saisissez le chemin directement.";
            try {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choisir une clé privée SSH", AllowMultiple = false });
                if (!closed) { if (files.Count > 0) path.Text = files[0].TryGetLocalPath(); error.Text = ""; }
            } catch (Exception ex) { if (!closed) error.Text = "Sélecteur indisponible : " + ex.Message + " Saisissez le chemin directement."; }
            finally { if (!closed) browse.IsEnabled = true; }
        };
        panel.Children.Add(browse); panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(18, 0, 18, 18) };
        var cancel = Button("Annuler", () => dialog.Close()); cancel.IsCancel = true; actions.Children.Add(cancel);
        actions.Children.Add(Button("Sans clé", () => { _key.Text = _certificate.Text = ""; _status.Text = "SSH sans clé imposée : mot de passe, agent ou configuration OpenSSH."; dialog.Close(); }));
        var use = new Button { Content = "Utiliser cette clé", IsDefault = true };
        async Task Validate()
        {
            use.IsEnabled = browse.IsEnabled = path.IsEnabled = false; error.Text = "Vérification du fichier…";
            try {
                string selected = SessionLauncher.IdentityPath(path.Text ?? "");
                await Task.Run(() => SessionLauncher.ValidateIdentityFile(selected));
                if (closed || _closing || !_storeLoaded) return;
                _key.Text = selected; _status.Text = "Clé privée sélectionnée ; sauvegarde automatique après la modification."; dialog.Close();
            } catch (Exception ex) { if (!closed) error.Text = ex.Message; }
            finally { if (!closed) use.IsEnabled = browse.IsEnabled = path.IsEnabled = true; }
        }
        use.Click += (_, _) => { if (use.IsEnabled) pending = Validate(); }; actions.Children.Add(use);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = DesktopAppearance.Surface };
        layout.Children.Add(new ScrollViewer { Content = panel }); Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Opened += (_, _) => path.Focus();
        dialog.Closed += (_, _) => { closed = true; _keyPicker = null; };
        try { var completed = dialog.ShowDialog(this); shown?.Invoke(dialog); await completed; if (pending != null) await pending; }
        finally { if (ReferenceEquals(_keyPicker, dialog)) _keyPicker = null; }
    }
    internal async Task VerifyKeySelection()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!App.Smoke) throw new InvalidOperationException("Isolated GUI fixture required.");
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-key-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string file = Path.Combine(directory, "synthetic-key"), previous = _key.Text ?? "";
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        string fixture = rsa.ExportRSAPrivateKeyPem();
        File.WriteAllText(file, fixture);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try {
            var opened = new TaskCompletionSource<Window>();
            Task selecting = ChooseKey(dialog => opened.SetResult(dialog));
            var dialog = await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var controls = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(dialog).ToArray();
            var input = controls.OfType<TextBox>().Single();
            var use = controls.OfType<Button>().Single(b => b.Content?.ToString() == "Utiliser cette clé");
            async Task Click()
            {
                use.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (dialog.IsVisible && !use.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(20);
                if (dialog.IsVisible && !use.IsEnabled) throw new Exception("SSH key validation did not finish.");
            }
            await ChooseKey();
            if (!ReferenceEquals(_keyPicker, dialog)) throw new Exception("Repeated key selection opened another dialog.");
            dialog.Width = 420; dialog.Height = 240; await Task.Delay(100);
            var point = use.TranslatePoint(new Point(), dialog);
            if (point == null || point.Value.Y < 0 || point.Value.Y + use.Bounds.Height > dialog.ClientSize.Height) throw new Exception("Key dialog actions are clipped.");
            foreach (string invalid in new[] { "relative", Path.Combine(directory, "missing") }) {
                input.Text = invalid; await Click();
                if (!dialog.IsVisible || _key.Text != previous) throw new Exception("Invalid key selection was accepted.");
            }
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            input.Text = file; await Click();
            if (!dialog.IsVisible || _key.Text != previous) throw new Exception("Publicly readable private key was accepted.");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            foreach (string invalid in new[] { "ssh-ed25519 AAAA synthetic-public-key", "PuTTY-User-Key-File-3: ssh-ed25519" }) {
                File.WriteAllText(file, invalid); await Click();
                if (!dialog.IsVisible || _key.Text != previous) throw new Exception("Public/PPK key was accepted.");
            }
            dialog.Width = 540; dialog.Height = 330; await Task.Delay(100);
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)dialog.ClientSize.Width, (int)dialog.ClientSize.Height))) {
                image.Render((Visual)dialog.Content!);
                image.Save(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..].Replace(".png", "-key.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            File.WriteAllText(file, fixture);
            await Click(); await selecting;
            if (_key.Text != file) throw new Exception("Direct key-path selection did not update the connection.");
            await ChooseKey(canceled => canceled.Close());
            if (_key.Text != file) throw new Exception("Canceling changed the selected key.");
            _key.Text = previous;
            await ChooseKey(canceled => {
                var descendants = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(canceled).ToArray();
                descendants.OfType<TextBox>().Single().Text = file;
                descendants.OfType<Button>().Single(b => b.Content?.ToString() == "Utiliser cette clé").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                canceled.Close();
            });
            if ((_key.Text ?? "") != previous) throw new Exception("Canceled validation changed the connection.");
            if (!SessionLauncher.IdentityPath("~/.ssh/id_ed25519").StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/", StringComparison.Ordinal)) throw new Exception("Home path expansion failed.");
            Console.WriteLine("PASS: responsive SSH key dialog, compact fixed actions, repeated-open guard, direct path, invalid-key rejection, successful selection and cancellation during validation.");
        } finally { _key.Text = previous; Directory.Delete(directory, true); }
    }
    private async Task Import()
    {
        try {
            if (!_storeLoaded) throw new IOException("Fichier existant non chargé : import bloqué pour préserver vos données. Corrigez-le puis relancez l’application.");
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Importer mRemoteNG XML", AllowMultiple = false });
            if (files.Count == 0) return;
            var keys = await AskImportKeys(); if (keys == null) return;
            var result = await Task.Run(() => { var items = Connections.Import(files[0].TryGetLocalPath() ?? throw new IOException("Fichier local requis."), out int ignored, out var folders, keys.Source, keys.Local); return (Items: items, Ignored: ignored, Folders: folders); });
            var imported = result.Items;
            var folders = _folders.Concat(result.Folders).DistinctBy(FolderKey).ToArray();
            Persist(_connections.Concat(imported), folders); _folders.Clear(); _folders.AddRange(folders); foreach (var c in imported) _connections.Add(c);
            Filter(); _status.Text = $"{imported.Count} connexions importées ; {result.Ignored} protocoles non pris en charge ignorés. Héritage résolu ; secrets réencryptés si demandés. Certaines options Windows restent non prises en charge. Vérifiez les valeurs avant connexion.";
        } catch (Exception ex) { _status.Text = "Import annulé, connexions existantes conservées : " + ex.Message; }
    }
}
