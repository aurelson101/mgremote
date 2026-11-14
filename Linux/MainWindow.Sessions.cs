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
        if (!FlushAutoSave()) return;
        if (_editing == null) { _status.Text = "Sélectionnez une connexion à dupliquer."; return; }
        var source = _editing; Edit(source); _editing = null; _list.SelectedItem = null;
        _name.Text = source.Name + " (copie)"; _name.Focus(); _status.Text = "Copie prête ; les modifications seront enregistrées automatiquement.";
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
        CaptureTreeState();
        _rebuildingTree = true;
        try {
        string query = _search.Text?.Trim() ?? "";
        var items = new List<TreeViewItem>();
        var root = new TreeViewItem { Header = TreeHeader("Connexions", "Folder"), Tag = Array.Empty<string>(), IsExpanded = TreeFolderExpanded([], query), ItemsSource = items };
        _folderNodes.Clear(); _nodes.Clear(); _folderNodes[FolderKey([])] = root;
        TreeViewItem EnsureFolder(string[] path)
        {
            var parent = root;
            for (int i = 0; i < path.Length; i++) {
                string[] prefix = path.Take(i + 1).ToArray(); string key = FolderKey(prefix);
                if (!_folderNodes.TryGetValue(key, out var folder)) {
                    folder = new TreeViewItem { Header = TreeHeader(path[i], "Folder"), Tag = prefix, IsExpanded = TreeFolderExpanded(prefix, query), ItemsSource = new List<TreeViewItem>() };
                    ((List<TreeViewItem>)parent.ItemsSource!).Add(folder); _folderNodes.Add(key, folder);
                }
                parent = folder;
            }
            return parent;
        }
        foreach (var path in _folders) if (query.Length == 0 || string.Join(" > ", path).Contains(query, StringComparison.OrdinalIgnoreCase)) EnsureFolder(path);
        foreach (var c in _connections) {
            if (query.Length > 0 && !c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !c.Host.Contains(query, StringComparison.OrdinalIgnoreCase) && !string.Join(" > ", c.FolderPath).Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var parent = EnsureFolder(c.FolderPath);
            var node = new TreeViewItem { Header = TreeHeader(c.Name, ConnectionIcons.Resolve(c), c.Protocol == "RDP" && c.Domain.Length > 0 && c.User.Length > 0 ? c.Domain + "\\" + c.User : c.User), Tag = c };
            ((List<TreeViewItem>)parent.ItemsSource!).Add(node); _nodes.Add(c, node);
        }
        _list.ItemsSource = new[] { root };
        RestoreTreeSelection(query);
        } finally { _rebuildingTree = false; }
    }
    private void AddSession(SessionTab tab) { tab.Body.IsVisible = false; _sessionArea.Children.Add(tab.Body); _tabs.Add(tab); _tabBar.SelectedItem = tab; }
    private void CloseSession(SessionTab? tab)
    {
        if (tab == null || !tab.Closable || !_tabs.Contains(tab)) return;
        var selected = _tabBar.SelectedItem; int index = _tabs.IndexOf(tab);
        _sessionArea.Children.Remove(tab.Body); _tabs.Remove(tab); tab.Host?.Dispose(); tab.Credentials?.Dispose(); (tab.Body as IDisposable)?.Dispose();
        _tabBar.SelectedItem = ReferenceEquals(selected, tab) ? _tabs.ElementAtOrDefault(Math.Min(index, _tabs.Count - 1)) : selected;
    }
    private static Task SessionShutdown(SessionTab tab) => tab.Host?.ShutdownCompletion ?? tab.Body switch {
        ExternalSession external => external.ShutdownCompletion,
        FileTransferPane transfer => transfer.ShutdownCompletion,
        _ => Task.CompletedTask
    };
    internal void VerifyTabClosing()
    {
        var first = new SessionTab("First fixture", new Border()); var background = new SessionTab("Background fixture", new Border()); var last = new SessionTab("Last fixture", new Border());
        try {
            AddSession(first); AddSession(background); AddSession(last); _tabBar.SelectedItem = first;
            CloseSession(background);
            if (!ReferenceEquals(_tabBar.SelectedItem, first) || !first.Body.IsVisible) throw new Exception("Closing a background tab changed the active session.");
            CloseSession(first);
            if (!ReferenceEquals(_tabBar.SelectedItem, last)) throw new Exception("Closing the active tab did not select its adjacent session.");
            CloseSession(last); CloseSession(last);
            if (_tabs.Any(t => t.Closable) || _tabBar.SelectedItem == null) throw new Exception("Closing the last session lost the home tab.");
            Console.WriteLine("PASS: background-tab closure preserves the active session, adjacent selection and repeated close safety.");
        } finally { CloseSession(first); CloseSession(background); CloseSession(last); }
    }
    private static Grid SessionContent(EmbeddedSession native, TextBlock status)
    {
        var error = new TextBlock { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        native.Failed += message => { native.IsVisible = false; error.Text = message; error.IsVisible = true; };
        var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        content.Children.Add(native); Grid.SetRow(status, 1); content.Children.Add(status); content.Children.Add(error);
        return content;
    }
    private async void Launch(bool sftp)
    {
        await OpenConnection(null, sftp);
    }
    private readonly HashSet<SessionTab> _reconnecting = [];
    private async Task ReconnectSession(SessionTab tab)
    {
        if (!_tabs.Contains(tab) || tab.Connection == null || !_reconnecting.Add(tab)) return;
        try {
            RequireLoadedProfile();
            tab.Host?.Dispose(); (tab.Body as IDisposable)?.Dispose(); await SessionShutdown(tab);
            tab.Credentials?.Dispose();
            if (!_closing && _tabs.Contains(tab)) await OpenConnection(tab.Connection, false, tab, tab.Body is ExternalSession);
        } catch (Exception ex) { _status.Text = "Reconnexion : " + ex.Message; }
        finally { _reconnecting.Remove(tab); }
    }
    private async Task OpenConnection(Connection? snapshot, bool sftp, SessionTab? replacing = null, bool externalWindow = false)
    {
        CredentialBroker? credentials = null;
        try {
            RequireLoadedProfile();
            var model = snapshot ?? Read(); Connections.Validate(model);
            string profilePath = _dataPath;
            if (sftp && model.Protocol != "SSH") throw new ArgumentException("SFTP nécessite SSH.");
            if (sftp) { AddSession(new SessionTab("SFTP · " + model.Name, new FileTransferPane(model, () => UnlockConnection(model)))); return; }
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cette édition nécessite Linux X11/XWayland.");
            SessionLauncher.Preflight(model);
            credentials = await UnlockConnection(model);
            if (_closing) throw new OperationCanceledException();
            RequireLoadedProfile();
            if (profilePath != _dataPath || replacing != null && !_tabs.Contains(replacing)) throw new OperationCanceledException();
            var ownedCredentials = credentials;
            if (externalWindow || model.Protocol == "RDP" && (model.MultiMonitor || model.RdpResolution == "fullscreen")) {
                var start = SessionLauncher.ExternalStart(model); ownedCredentials?.Configure(start, model.Protocol);
                SessionTab? externalTab = null;
                var external = new ExternalSession(start, () => ReconnectSession(externalTab!));
                external.Completed += _ => ownedCredentials?.Dispose();
                externalTab = new SessionTab(model.Name + " · Fenêtre externe", external, Credentials: ownedCredentials, Connection: model);
                InstallConnectedTab(externalTab, replacing); credentials = null;
                _status.Text = "Fenêtre native liée à son onglet ; reconnexion disponible après la fin du client."; return;
            }
            var status = new TextBlock { Text = "Initialisation…", Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var native = new EmbeddedSession(parent => { var start = SessionLauncher.Embedded(model, sftp, parent); ownedCredentials?.Configure(start, model.Protocol); return start; }, text => { status.Text = text; _status.Text = text; });
            native.Completed += _ => ownedCredentials?.Dispose();
            SessionTab? tab = null;
            var content = SessionWorkspace(model, native, status, () => ReconnectSession(tab!));
            tab = new SessionTab(model.Name, content, native, Credentials: ownedCredentials, Connection: model);
            InstallConnectedTab(tab, replacing); credentials = null;
        } catch (OperationCanceledException) { credentials?.Dispose(); _status.Text = "Connexion annulée."; }
        catch (Exception ex) {
            credentials?.Dispose(); _status.Text = "Connexion : " + ex.Message;
            if (externalWindow) return;
            var dialog = new Window { Title = "Connexion impossible", Width = 500, Height = 230, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = ex.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(Button("Fermer", () => dialog.Close())); dialog.Content = panel;
            if (!_closing) await dialog.ShowDialog(this);
        }
    }
    private void InstallConnectedTab(SessionTab tab, SessionTab? replacing)
    {
        AddSession(tab);
        if (replacing == null) return;
        int index = _tabs.IndexOf(replacing);
        if (index >= 0) { _tabs.Move(_tabs.IndexOf(tab), index); CloseSession(replacing); }
    }
    private async void External() => await OpenConnection(null, false, externalWindow: true);
    private async Task Delete()
    {
        if (!FlushAutoSave()) return;
        if (_editing == null || !_storeLoaded) return;
        var target = _editing;
        var dialog = new Window { Title = "Supprimer la connexion", Width = 430, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 14, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Supprimer « " + target.Name + " » ?", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Button("Annuler", () => dialog.Close(false))); actions.Children.Add(Button("Supprimer", () => dialog.Close(true))); panel.Children.Add(actions); dialog.Content = panel;
        if (!await dialog.ShowDialog<bool>(this)) return;
        try { var next = _connections.Where(c => !ReferenceEquals(c, target)).ToArray(); Persist(next); _connections.Remove(target); Filter(); New(); _status.Text = "Connexion supprimée ; sessions ouvertes conservées."; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    internal async Task VerifyEmbeddedSessions()
    {
        if (!OperatingSystem.IsLinux()) return;
        string report = Path.Combine(Path.GetTempPath(), "mgremote-clipboard-" + Guid.NewGuid().ToString("N"));
        EmbeddedSession Fixture(bool record) => new(parent => SessionLauncher.Terminal(parent, Path.Combine(AppContext.BaseDirectory, "MGRemoteLinux"), record ? new[] { "--terminal-fixture", "--fixture-output=" + report } : new[] { "--terminal-fixture" }), _ => { });
        var first = Fixture(true); var second = Fixture(false);
        var a = new SessionTab("Native fixture A", first, first); var b = new SessionTab("Native fixture B", second, second);
        try {
            AddSession(a); var deadline = DateTime.UtcNow.AddSeconds(12);
            while ((first.ProcessId == 0 || first.ChildWindows == 0) && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (!first.ProcessRunning || first.ChildWindows == 0) throw new Exception("First terminal did not embed.");
            await VerifyClipboard(first.ProcessId, report);
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
        using var clipboardTransfer = new FileTransferPane(new Connection { Name = "Clipboard fixture", Host = "fixture.example.invalid" });
        var transferTab = new SessionTab("Transferts — fichier et dossier", clipboardTransfer);
        try {
            AddSession(transferTab); await Task.Delay(150);
            await clipboardTransfer.VerifyClipboardUi(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..].Replace(".png", "-transfer.png"));
        } finally { CloseSession(transferTab); }
        Console.WriteLine("PASS: embedded tabs, idle resize suppression, real PID cleanup and transfer UI validation.");
        foreach (string executable in new[] { "/usr/bin/false", "/nonexistent/mgremote-fixture", "/usr/bin/sleep" }) {
            var status = new TextBlock();
            var failed = new EmbeddedSession(_ => {
                var start = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false };
                if (executable.EndsWith("/sleep", StringComparison.Ordinal)) start.ArgumentList.Add("30");
                return start;
            }, text => status.Text = text);
            var body = SessionContent(failed, status);
            var tab = new SessionTab("Startup failure fixture", body, failed);
            try {
                AddSession(tab); var deadline = DateTime.UtcNow.AddSeconds(14);
                while ((!failed.StartupFailed || failed.IsVisible) && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (!failed.StartupFailed || failed.IsVisible || !body.Children.OfType<TextBlock>().Any(t => t != status && t.IsVisible && !string.IsNullOrWhiteSpace(t.Text))) throw new Exception("Failed startup left a blank session pane.");
            } finally { CloseSession(tab); await failed.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(8)); }
        }
        Console.WriteLine("PASS: early client exit, missing executable and startup timeout show a visible error pane and stop owned processes.");
    }
}
