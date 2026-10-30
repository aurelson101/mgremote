using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;

namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private static DrawingImage DrawIcon(params (string Color, string Path)[] layers)
    {
        var group = new DrawingGroup();
        foreach (var layer in layers) group.Children.Add(new GeometryDrawing { Brush = Brush.Parse(layer.Color), Geometry = Geometry.Parse(layer.Path) });
        return new DrawingImage { Drawing = group };
    }
    private static readonly DrawingImage FolderIcon = DrawIcon(("#b7791f", "M1 4H9L12 7H23V21H1Z"), ("#f4c55b", "M2 9H23L21 21H1Z"));
    private static readonly DrawingImage WindowsIcon = DrawIcon(("#1684d6", "M1 3L11 2V11H1Z M13 2L23 1V11H13Z M1 13H11V22L1 21Z M13 13H23V23L13 22Z"));
    private static readonly DrawingImage LinuxIcon = DrawIcon(("#202832", "M12 1C7 1 7 6 7 9C5 12 3 16 5 20L9 22H15L19 20C21 16 19 12 17 9C17 6 17 1 12 1Z"), ("#ffffff", "M12 7C8 7 7 13 7 17C7 22 17 22 17 17C17 13 16 7 12 7Z M8 5H10V8H8Z M14 5H16V8H14Z"), ("#e9a52a", "M9 9L12 7L15 9L12 11Z M4 20L9 19L10 23H2Z M15 19L20 20L22 23H14Z"));
    private static readonly DrawingImage SerialIcon = DrawIcon(("#64748b", "M2 5H22V18H2Z M6 18H18V22H6Z"), ("#ffffff", "M5 8H7V10H5Z M11 8H13V10H11Z M17 8H19V10H17Z M8 13H10V15H8Z M14 13H16V15H14Z"));
    private static readonly DrawingImage NetworkIcon = DrawIcon(("#5267b3", "M2 8H22V18H2Z M5 3H7V8H5Z M17 3H19V8H17Z"), ("#ffffff", "M5 11H8V14H5Z M10 11H13V14H10Z M15 11H18V14H15Z"));
    private static Control TreeHeader(string name, string kind, string login = "")
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(new Image { Source = kind switch { "Folder" => FolderIcon, "RDP" or "Windows" => WindowsIcon, "SSH" or "Linux" => LinuxIcon, "Serial" => SerialIcon, _ => NetworkIcon }, Width = 18, Height = 18 });
        header.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        if (login.Length > 0) header.Children.Add(new TextBlock { Text = login, FontSize = 11, Foreground = Brush.Parse("#64748b"), VerticalAlignment = VerticalAlignment.Center });
        return header;
    }
    private const string HelpText = "MGRemote — Guide Linux 0.2.6\n\nConnexions\nClic droit : nouveau dossier, nouvelle connexion, modifier/renommer, dupliquer. F2 modifie ; Inser crée une connexion. Sélectionnez une connexion pour choisir SSH, RDP ou Série, l’hôte, le port et l’utilisateur dans Configuration, puis Enregistrer.\n\nProfil et icônes\nLe menu Fichier permet de chiffrer le profil complet. La proposition au démarrage est facultative ; Plus tard conserve le fonctionnement sans chiffrement. Un profil chiffré exige sa phrase secrète à chaque ouverture. Dans Général, Auto choisit l’icône depuis les métadonnées et le protocole ; un choix manuel reste possible.\n\nClés et tunnels SSH\nDans la section SSH, choisissez une clé privée OpenSSH/PEM, un certificat public facultatif et un alias de bastion. Un tunnel local écoute sur 127.0.0.1 pendant la session ; indiquez son port local et la destination vue du serveur SSH. SFTP n’ouvre pas de second tunnel.\n\nSSH — texte dans les deux sens\nSélectionnez le texte puis Ctrl+Maj+C pour copier ; Ctrl+Maj+V colle le presse-papiers local dans le terminal. Ctrl+C interrompt la commande. Dans une application qui capture la souris, maintenez Maj pour sélectionner. Pour les fichiers/dossiers, utilisez SFTP : ils ne sont pas collés comme commandes.\n\nRDP — presse-papiers et fichiers\nActivez Presse-papiers RDP avant de connecter/reconnecter. Utilisez Ctrl+C / Ctrl+V dans le bureau distant. Les stratégies du serveur peuvent bloquer la redirection. Le partage explicite d’un dossier donne accès en lecture/écriture via \\\\tsclient\\MGRemote ; aucun lecteur n’est partagé automatiquement.\n\nMulti-écran RDP\nCochez Plusieurs écrans puis Connecter. FreeRDP ouvre une fenêtre native en plein écran sur plusieurs moniteurs ; son onglet MGRemote permet de fermer le processus. Ctrl+Alt+Entrée bascule le plein écran si le client le permet. Les écrans physiques, DPI mixtes et branchements à chaud nécessitent un essai réel.\n\nSecrets importés\nDéverrouillez avec la phrase locale ou choisissez Connexion manuelle. Le premier accès SSH, les bastions, phrases de clés et MFA peuvent nécessiter le mode manuel. Vérifiez les empreintes et certificats.\n\nCette édition reste une préversion avec une parité Windows incomplète. Les tests locaux de presse-papiers ne prouvent pas la redirection d’un serveur RDP/SSH réel.";
    private void ShowHelp()
    {
        var existing = _tabs.FirstOrDefault(t => t.Name == "Aide");
        if (existing != null) { _tabBar.SelectedItem = existing; return; }
        AddSession(new SessionTab("Aide", new ScrollViewer { Content = new TextBlock { Text = HelpText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) } }));
    }
    private async Task ShowAbout()
    {
        var dialog = new Window { Title = "À propos de MGRemote", Width = 440, Height = 250, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "MGRemote Linux " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3), FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "Fork indépendant de mRemoteNG — GPL-2.0.\nPréversion non signée. .NET / Avalonia et clients natifs Linux.\nLa parité Windows et les connexions réelles restent à valider.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Button("Fermer", () => dialog.Close())); dialog.Content = panel; if (App.Smoke) dialog.Opened += (_, _) => dialog.Close(); await dialog.ShowDialog(this);
    }
    internal async Task VerifyDesktopUi()
    {
        foreach (string kind in new[] { "Folder", "Windows", "Linux", "Network", "RDP", "SSH", "Serial" }) {
            if (TreeHeader("Fixture", kind, "alice") is not StackPanel panel || panel.Children[0] is not Image { Source: not null } || panel.Children.OfType<TextBlock>().Last().Text != "alice") throw new Exception("Tree icon/login header is invalid.");
        }
        var menu = ((Grid)Content!).Children.OfType<Menu>().Single();
        var guide = menu.ItemsSource!.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Aide").ItemsSource!.OfType<MenuItem>().First();
        guide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); var help = _tabBar.SelectedItem as SessionTab; guide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        if (help?.Name != "Aide" || _tabs.Count(t => t.Name == "Aide") != 1 || !ReferenceEquals(_tabBar.SelectedItem, help)) throw new Exception("Help menu did not open/reuse its guide.");
        CloseSession(help); await ShowAbout(); Console.WriteLine("PASS: protocol/folder icons, login labels and reusable help tab.");
        if (OperatingSystem.IsLinux()) {
            var start = new ProcessStartInfo(SessionLauncher.Find("sleep")) { UseShellExecute = false }; start.ArgumentList.Add("60");
            var owned = new ExternalSession(start); var tab = new SessionTab("External fixture", owned); AddSession(tab); CloseSession(tab);
            await owned.ShutdownCompletion;
            bool alive; try { using var process = Process.GetProcessById(owned.ProcessId); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
            if (alive) throw new Exception("Managed external session survived tab closure.");
            Console.WriteLine("PASS: managed external-session process cleanup (local fixture, no RDP server).");
        }
    }
    private static async Task<string> RunDesktopTool(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(SessionLauncher.Find(tool)) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try {
            string output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(6));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            if (process.ExitCode != 0) throw new IOException(tool + " failed: " + await process.StandardError.ReadToEndAsync());
            return output;
        } finally { if (!process.HasExited) process.Kill(true); }
    }
    private static async Task VerifyClipboard(int pid, string report)
    {
        await Task.Delay(350);
        string id = (await RunDesktopTool("xdotool", "search", "--onlyvisible", "--pid", pid.ToString())).Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        if (!ulong.TryParse(id, out _)) throw new Exception("No X11 terminal window for clipboard test.");
        string geometry = await RunDesktopTool("xdotool", "getwindowgeometry", "--shell", id);
        int width = int.Parse(geometry.Split('\n').Single(l => l.StartsWith("WIDTH="))[6..], System.Globalization.CultureInfo.InvariantCulture);
        await RunDesktopTool("xdotool", "windowfocus", "--sync", id);
        await RunDesktopTool("xdotool", "mousemove", "--window", id, "3", "10", "mousedown", "1", "mousemove", "--window", id, (width - 4).ToString(), "10", "mouseup", "1");
        await RunDesktopTool("xdotool", "key", "--clearmodifiers", "ctrl+shift+c");
        string copied = await RunDesktopTool("xclip", "-o", "-selection", "clipboard");
        if (!copied.Contains("MGRemote embedded terminal fixture")) throw new Exception("Ctrl+Shift+C did not copy the embedded terminal selection.");
        string nonce = "mgremote-paste-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(SessionLauncher.Find("xclip")) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-quiet", "-i", "-selection", "clipboard" }) start.ArgumentList.Add(arg);
        using var owner = Process.Start(start)!;
        try {
            await owner.StandardInput.WriteAsync(nonce); owner.StandardInput.Close(); await Task.Delay(200);
            if (await RunDesktopTool("xclip", "-o", "-selection", "clipboard") != nonce) throw new Exception("Clipboard test owner was not ready.");
            await RunDesktopTool("xdotool", "windowfocus", "--sync", id);
            await RunDesktopTool("xdotool", "key", "--clearmodifiers", "ctrl+shift+v", "Return");
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while ((!File.Exists(report) || !File.ReadAllText(report).Contains(nonce)) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!File.Exists(report) || !File.ReadAllText(report).Contains(nonce)) throw new Exception("Ctrl+Shift+V did not reach the embedded terminal PTY.");
            Console.WriteLine("PASS: embedded X11 clipboard in both directions using actual Ctrl+Shift+C/V; local PTY fixture only.");
        } finally { if (!owner.HasExited) owner.Kill(true); await owner.WaitForExitAsync(); }
    }
}

internal sealed class ExternalSession : StackPanel, IDisposable
{
    private readonly Process _process;
    private bool _disposed;
    internal int ProcessId { get; }
    internal Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    internal ExternalSession(ProcessStartInfo start)
    {
        Margin = new Thickness(24); Spacing = 12;
        Children.Add(new TextBlock { Text = "Session RDP dans une fenêtre native", FontSize = 20 });
        Children.Add(new TextBlock { Text = "Le bureau multi-écran est ouvert par FreeRDP. Fermer cet onglet arrête aussi le client et son terminal.", TextWrapping = TextWrapping.Wrap });
        var stop = new Button { Content = "Fermer la session externe" }; stop.Click += (_, _) => { Dispose(); stop.IsEnabled = false; }; Children.Add(stop);
        _process = Process.Start(start) ?? throw new IOException("Impossible de démarrer le client externe."); ProcessId = _process.Id;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { if (!_process.HasExited) _process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        ShutdownCompletion = Finish();
    }
    private async Task Finish()
    {
        try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { _process.Dispose(); }
    }
}
