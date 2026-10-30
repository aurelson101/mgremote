using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Text.Json;
namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private string[] ReadFolderPath()
    {
        string text = _folder.Text ?? "";
        return text == string.Join(" > ", _selectedFolder) ? _selectedFolder.ToArray() : text.Split(" > ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
    private void Duplicate()
    {
        if (_editing == null) { _status.Text = "Sélectionnez une connexion à dupliquer."; return; }
        var source = _editing; Edit(source); _editing = null; _list.SelectedItem = null;
        _name.Text = source.Name + " (copie)"; _name.Focus(); _status.Text = "Copie prête ; cliquez sur Enregistrer pour l’ajouter.";
    }
    private async Task ChooseRdpFolder()
    {
        try {
            var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Dossier local à partager avec le serveur RDP", AllowMultiple = false });
            if (folders.Count > 0) _sharePath.Text = Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders[0]);
        } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void RebuildTree()
    {
        string query = _search.Text?.Trim() ?? "";
        var items = new List<TreeViewItem>();
        var root = new TreeViewItem { Header = "Connexions", Tag = Array.Empty<string>(), IsExpanded = true, ItemsSource = items };
        var folders = new Dictionary<string, TreeViewItem>(); _nodes.Clear();
        foreach (var c in _connections) {
            if (query.Length > 0 && !c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !c.Host.Contains(query, StringComparison.OrdinalIgnoreCase) && !string.Join(" > ", c.FolderPath).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var parent = root;
            for (int i = 0; i < c.FolderPath.Length; i++) {
                string[] path = c.FolderPath.Take(i + 1).ToArray(); string key = JsonSerializer.Serialize(path);
                if (!folders.TryGetValue(key, out var folder)) {
                    folder = new TreeViewItem { Header = "▸ " + c.FolderPath[i], Tag = path, IsExpanded = true, ItemsSource = new List<TreeViewItem>() };
                    ((List<TreeViewItem>)parent.ItemsSource!).Add(folder); folders.Add(key, folder);
                }
                parent = folder;
            }
            var node = new TreeViewItem { Header = c.Protocol + " · " + c.Name, Tag = c };
            ((List<TreeViewItem>)parent.ItemsSource!).Add(node); _nodes.Add(c, node);
        }
        _list.ItemsSource = new[] { root };
    }
    private void AddSession(SessionTab tab) { tab.Body.IsVisible = false; _sessionArea.Children.Add(tab.Body); _tabs.Add(tab); _tabBar.SelectedItem = tab; }
    private void CloseSession(SessionTab? tab)
    {
        if (tab == null || !tab.Closable) return;
        _sessionArea.Children.Remove(tab.Body); _tabs.Remove(tab); tab.Host?.Dispose(); tab.Credentials?.Dispose(); (tab.Body as IDisposable)?.Dispose(); _tabBar.SelectedItem = _tabs.LastOrDefault();
    }
    private async void Launch(bool sftp)
    {
        CredentialBroker? credentials = null;
        try {
            var model = Read(); Connections.Validate(model);
            if (sftp && model.Protocol != "SSH") throw new ArgumentException("SFTP nécessite SSH.");
            if (sftp) { AddSession(new SessionTab("SFTP · " + model.Name, new FileTransferPane(model, () => UnlockConnection(model)))); return; }
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cette édition nécessite Linux X11/XWayland.");
            if (model.Protocol == "RDP" && model.MultiMonitor) throw new NotSupportedException("Le multi-écran utilise encore Sessions → Ouvrir en fenêtre externe.");
            credentials = await UnlockConnection(model);
            if (_closing) throw new OperationCanceledException();
            var ownedCredentials = credentials;
            var status = new TextBlock { Text = "Initialisation…", Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var native = new EmbeddedSession(parent => { var start = SessionLauncher.Embedded(model, sftp, parent); ownedCredentials?.Configure(start, model.Protocol); return start; }, text => { status.Text = text; _status.Text = text; });
            var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") }; content.Children.Add(native); Grid.SetRow(status, 1); content.Children.Add(status);
            AddSession(new SessionTab((sftp ? "SFTP · " : "") + model.Name, content, native, Credentials: ownedCredentials)); credentials = null;
        } catch (OperationCanceledException) { credentials?.Dispose(); _status.Text = "Connexion annulée."; }
        catch (Exception ex) { credentials?.Dispose(); _status.Text = "Connexion : " + ex.Message; }
    }
    private void External() { try { SessionLauncher.Launch(Read()); _status.Text = "Session externe ouverte explicitement."; } catch (Exception ex) { _status.Text = ex.Message; } }
    private async Task Delete()
    {
        if (_editing == null || !_storeLoaded) return;
        var target = _editing;
        var dialog = new Window { Title = "Supprimer la connexion", Width = 430, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Supprimer « " + target.Name + " » ?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Button("Annuler", () => dialog.Close(false))); actions.Children.Add(Button("Supprimer", () => dialog.Close(true))); panel.Children.Add(actions); dialog.Content = panel;
        if (!await dialog.ShowDialog<bool>(this)) return;
        try { var next = _connections.Where(c => !ReferenceEquals(c, target)).ToArray(); Connections.Save(next); _connections.Remove(target); Filter(); New(); _status.Text = "Connexion supprimée ; sessions ouvertes conservées."; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    internal async Task VerifyEmbeddedSessions()
    {
        if (!OperatingSystem.IsLinux()) return;
        EmbeddedSession Fixture() => new(parent => SessionLauncher.Terminal(parent, Path.Combine(AppContext.BaseDirectory, "MGRemoteLinux"), new[] { "--terminal-fixture" }), _ => { });
        var first = Fixture(); var second = Fixture();
        var a = new SessionTab("Native fixture A", first, first); var b = new SessionTab("Native fixture B", second, second);
        try {
            AddSession(a); var deadline = DateTime.UtcNow.AddSeconds(12);
            while ((first.ProcessId == 0 || first.ChildWindows == 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!first.ProcessRunning || first.ChildWindows == 0) throw new Exception("First terminal did not embed.");
            AddSession(b); deadline = DateTime.UtcNow.AddSeconds(12);
            while ((second.ProcessId == 0 || second.ChildWindows == 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!second.ProcessRunning || second.ChildWindows == 0 || !first.ProcessRunning) throw new Exception("Switching tabs destroyed a terminal.");
            _tabBar.SelectedItem = a; await Task.Delay(300);
            if (!first.ProcessRunning || !second.ProcessRunning) throw new Exception("Hidden sessions are not retained.");
            await Task.Delay(800); int resizeRequests = first.ResizeRequests, queries = first.TreeQueries; await Task.Delay(1000);
            if (first.ResizeRequests != resizeRequests || first.TreeQueries != queries) throw new Exception("An idle terminal repeatedly queries/resizes its X11 children.");
        } finally { CloseSession(b); CloseSession(a); await Task.WhenAll(first.ShutdownCompletion, second.ShutdownCompletion).WaitAsync(TimeSpan.FromSeconds(8)); }
        foreach (int pid in new[] { first.ProcessId, second.ProcessId }) {
            bool alive; try { using var process = System.Diagnostics.Process.GetProcessById(pid); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
            if (alive) throw new Exception("An owned terminal process survived tab closure.");
        }
        using var transfer = new FileTransferPane(new Connection { Name = "Fixture", Host = "example.invalid" }); await transfer.VerifyValidation();
        Console.WriteLine("PASS: embedded tabs, idle resize suppression, real PID cleanup and transfer UI validation.");
    }
}
