using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    internal async Task VerifySessionTransferTools()
    {
        using var native = new EmbeddedSession(_ => throw new InvalidOperationException("This toolbar fixture must not start a client."), _ => { });
        var rdp = new Connection { Protocol = "RDP", Host = "fixture.example.invalid", Clipboard = true, RdpFileClipboard = true, RdpShareEnabled = true, RdpSharePath = Path.GetTempPath() };
        var tools = SessionWorkspace(rdp, native, new TextBlock());
        tools.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Copier le chemin du partage").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(100);
        if (Clipboard == null || await Clipboard.TryGetTextAsync() != @"\\tsclient\MGRemote") throw new Exception("RDP shared-folder shortcut did not copy its Windows path.");
        await Clipboard.ClearAsync();
        using var sshNative = new EmbeddedSession(_ => throw new InvalidOperationException("This toolbar fixture must not start a client."), _ => { });
        tools = SessionWorkspace(new Connection { Name = "Session SFTP fixture", Host = "fixture.example.invalid" }, sshNative, new TextBlock());
        int count = _tabs.Count;
        tools.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Fichiers / dossiers SFTP").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        if (_tabs.Count != count + 1 || _tabs.Last().Body is not FileTransferPane) throw new Exception("SSH session shortcut did not open its SFTP pane.");
        CloseSession(_tabs.Last());
        Console.WriteLine("PASS: RDP shared-path clipboard shortcut and SSH session-to-SFTP toolbar; no remote connection.");
    }
    private Control SessionWorkspace(Connection connection, EmbeddedSession native, TextBlock status, Func<Task>? reconnect = null)
    {
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        bool retrying = false;
        if (reconnect != null) {
            var retry = Button("Reconnecter", async () => {
                if (!retrying) { retrying = true; try { await reconnect(); } finally { retrying = false; } }
            });
            retry.IsEnabled = native.ExitCode != null || native.StartupFailed;
            native.Completed += _ => retry.IsEnabled = true;
            native.Failed += _ => retry.IsEnabled = true;
            tools.Children.Add(retry);
        }
        if (connection.Protocol == "SSH") {
            tools.Children.Add(Button("Fichiers / dossiers SFTP", () => {
                try { RequireLoadedProfile(); AddSession(new SessionTab("SFTP · " + connection.Name, new FileTransferPane(connection, () => UnlockConnection(connection)))); }
                catch (Exception ex) { _status.Text = ex.Message; }
            }));
            tools.Children.Add(new TextBlock { Text = "Souris : sélectionner pour copier · clic droit pour coller · Maj si l’application capture la souris · Fichiers : SFTP", Margin = new Thickness(10, 4), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        } else if (connection.Protocol == "RDP") {
            tools.Children.Add(new TextBlock { Text = connection.Clipboard ? "Clic droit → Copier / Coller dans les applications du bureau distant" + (connection.RdpFileClipboard ? " · Fichiers bidirectionnels activés" : " · Dossiers : utilisez le partage local") : "Presse-papiers RDP désactivé pour cette connexion.", Margin = new Thickness(4), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (connection.RdpShareEnabled) {
                tools.Children.Add(Button("Copier le chemin du partage", async () => {
                    try { var clipboard = Clipboard ?? throw new IOException("Presse-papiers indisponible."); await clipboard.SetTextAsync(@"\\tsclient\MGRemote"); _status.Text = connection.Clipboard ? "Chemin copié : collez-le dans l’Explorateur Windows du serveur." : @"Chemin copié localement. Le presse-papiers RDP est désactivé : saisissez \\tsclient\MGRemote dans l’Explorateur distant."; }
                    catch (Exception ex) { _status.Text = ex.Message; }
                }));
                tools.Children.Add(Button("Ouvrir le dossier local", () => {
                    try {
                        if (!Directory.Exists(connection.RdpSharePath)) throw new IOException("Dossier partagé introuvable.");
                        var start = new System.Diagnostics.ProcessStartInfo(SessionLauncher.Find("xdg-open")) { UseShellExecute = false };
                        start.ArgumentList.Add(connection.RdpSharePath); using var process = System.Diagnostics.Process.Start(start);
                    } catch (Exception ex) { _status.Text = ex.Message; }
                }));
            }
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(tools); var content = SessionContent(native, status); Grid.SetRow(content, 1); layout.Children.Add(content); return layout;
    }
}
