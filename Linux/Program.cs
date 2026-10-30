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
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
public sealed class App : Application
{
    internal static bool Smoke => Environment.GetCommandLineArgs().Any(a => a.StartsWith("--smoke-ui="));
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new MainWindow();
            if (Smoke) Avalonia.Threading.DispatcherTimer.RunOnce(() => {
                try {
                    var window = desktop.MainWindow;
                    ((MainWindow)window).VerifyProtocolPortEditing();
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
public sealed class MainWindow : Window
{
    private readonly ObservableCollection<Connection> _connections = [];
    private readonly ListBox _list = new() { MinWidth = 220 };
    private readonly TextBox _name = new(), _host = new(), _user = new(), _domain = new(), _key = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    private readonly ComboBox _protocol = new() { ItemsSource = new[] { "SSH", "RDP" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _clipboard = new() { Content = "Presse-papiers RDP bidirectionnel (texte)" };
    private readonly CheckBox _multi = new() { Content = "RDP sur plusieurs écrans" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private Connection? _editing;
    public MainWindow()
    {
        Title = "MGRemote Linux — Prototype 0.1.1"; Width = 1080; Height = 740; MinWidth = 760; MinHeight = 540;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20) };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 18) };
        toolbar.Children.Add(new TextBlock { Text = "MGRemote Linux", FontSize = 26, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        toolbar.Children.Add(Button("Nouvelle", New)); toolbar.Children.Add(Button("Importer XML…", async () => await Import()));
        root.Children.Add(toolbar);
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("280,16,*") }; Grid.SetRow(body, 1);
        _list.ItemsSource = _connections;
        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Connection c) Edit(c); };
        body.Children.Add(_list);
        var fields = new StackPanel { Spacing = 9 };
        Add(fields, "Nom", _name); Add(fields, "Protocole", _protocol); Add(fields, "Serveur / IP / alias SSH", _host);
        Add(fields, "Port", _port); Add(fields, "Utilisateur (facultatif)", _user); Add(fields, "Domaine RDP (facultatif)", _domain);
        Add(fields, "Clé privée SSH OpenSSH/PEM (facultative)", _key);
        fields.Children.Add(Button("Choisir une clé…", async () => await ChooseKey()));
        fields.Children.Add(_clipboard); fields.Children.Add(_multi);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Enregistrer", Save)); actions.Children.Add(Button("Connecter", () => Launch(false)));
        actions.Children.Add(Button("SFTP", () => Launch(true)));
        fields.Children.Add(actions);
        fields.Children.Add(new TextBlock { Text = "Prototype : sessions dans des fenêtres Linux séparées. Les mots de passe ne sont ni importés ni enregistrés. Clés Windows à sélectionner à nouveau sous Linux. Les profils OpenSSH locaux peuvent définir ProxyJump.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
        var scroll = new ScrollViewer { Content = fields }; Grid.SetColumn(scroll, 2); body.Children.Add(scroll); root.Children.Add(body);
        Grid.SetRow(_status, 2); _status.Margin = new Thickness(0, 14, 0, 0); root.Children.Add(_status); Content = root;
        _protocol.SelectionChanged += (_, _) => {
            bool ssh = _protocol.SelectedIndex == 0;
            _key.IsEnabled = ssh; _domain.IsEnabled = !ssh; _clipboard.IsEnabled = _multi.IsEnabled = !ssh;
            if (_port.Value is 22 or 3389) _port.Value = ssh ? 22 : 3389;
        };
        try { if (!App.Smoke) foreach (var c in Connections.Load()) _connections.Add(c); _status.Text = "Prêt — OpenSSH, FreeRDP et un terminal Linux sont requis pour les sessions."; }
        catch (Exception ex) { _status.Text = "Chargement : " + ex.Message; }
        New();
    }
    private static Button Button(string title, Action action)
    {
        var button = new Button { Content = title, Margin = new Thickness(0, 0, 8, 4) };
        button.Click += (_, _) => action(); return button;
    }
    private static void Add(Panel panel, string label, Control control) { panel.Children.Add(new TextBlock { Text = label }); panel.Children.Add(control); }
    private void New() { _editing = null; _list.SelectedItem = null; _name.Text = "Nouvelle connexion"; _host.Text = _user.Text = _domain.Text = _key.Text = ""; _protocol.SelectedIndex = 0; _port.Value = 22; _clipboard.IsChecked = _multi.IsChecked = false; }
    private void Edit(Connection c)
    {
        _editing = c; _name.Text = c.Name; _host.Text = c.Host; _protocol.SelectedIndex = c.Protocol == "SSH" ? 0 : 1;
        _port.Value = c.Port; _user.Text = c.User; _domain.Text = c.Domain; _key.Text = c.Identity; _clipboard.IsChecked = c.Clipboard; _multi.IsChecked = c.MultiMonitor;
    }
    private Connection Read() => new() { Name = _name.Text?.Trim() ?? "", Host = _host.Text?.Trim() ?? "", Protocol = _protocol.SelectedIndex == 0 ? "SSH" : "RDP", Port = (int)(_port.Value ?? 0), User = _user.Text?.Trim() ?? "", Domain = _domain.Text?.Trim() ?? "", Identity = _key.Text?.Trim() ?? "", Clipboard = _clipboard.IsChecked == true, MultiMonitor = _multi.IsChecked == true };
    internal void VerifyProtocolPortEditing()
    {
        _editing = new Connection(); _protocol.SelectedIndex = 0; _port.Value = 22;
        _protocol.SelectedIndex = 1;
        if (_port.Value != 3389) throw new Exception("Editing SSH to RDP did not update the default port.");
        _port.Value = 3391; _protocol.SelectedIndex = 0;
        if (_port.Value != 3391) throw new Exception("Changing protocol overwrote a custom port.");
        New();
    }
    private void Save()
    {
        try {
            var item = Read(); Connections.Validate(item);
            var next = _connections.ToList(); int index = _editing == null ? -1 : next.IndexOf(_editing);
            if (index < 0) next.Add(item); else next[index] = item;
            Connections.Save(next); _connections.Clear(); foreach (var c in next) _connections.Add(c);
            _list.SelectedItem = item; _status.Text = "Connexion enregistrée sans mot de passe.";
        } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void Launch(bool sftp) { try { SessionLauncher.Launch(Read(), sftp); _status.Text = "Session ouverte dans le terminal/client Linux. Vérifiez la clé d’hôte ou le certificat avant de poursuivre."; } catch (Exception ex) { _status.Text = ex.Message; } }
    private async Task ChooseKey()
    {
        try { var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Clé privée SSH — ne partagez jamais ce fichier", AllowMultiple = false }); if (files.Count > 0) _key.Text = files[0].TryGetLocalPath(); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task Import()
    {
        try {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Importer mRemoteNG XML sans secrets", AllowMultiple = false });
            if (files.Count == 0) return;
            var imported = await Task.Run(() => Connections.Import(files[0].TryGetLocalPath() ?? throw new IOException("Fichier local requis.")));
            Connections.Save(_connections.Concat(imported)); foreach (var c in imported) _connections.Add(c);
            _status.Text = $"{imported.Count} connexions importées. Héritage protocole/port/utilisateur/domaine résolu ; mots de passe, clés et options Windows exclus. Vérifiez les valeurs avant connexion.";
        } catch (Exception ex) { _status.Text = "Import annulé, connexions existantes conservées : " + ex.Message; }
    }
}
