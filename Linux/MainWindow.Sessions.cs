using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using System.Text.Json;
namespace MGRemote.Linux;
public sealed partial class MainWindow
{
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
        if (tab?.Host == null) return;
        _sessionArea.Children.Remove(tab.Body); _tabs.Remove(tab); tab.Host.Dispose(); _tabBar.SelectedItem = _tabs.LastOrDefault();
    }
    private void Launch(bool sftp)
    {
        try {
            var model = Read(); Connections.Validate(model);
            if (sftp && model.Protocol != "SSH") throw new ArgumentException("SFTP nécessite SSH.");
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Cette édition nécessite Linux X11/XWayland.");
            if (model.Protocol == "RDP" && model.MultiMonitor) throw new NotSupportedException("Le multi-écran utilise encore Sessions → Ouvrir en fenêtre externe.");
            var status = new TextBlock { Text = "Initialisation…", Margin = new Thickness(8), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            var native = new EmbeddedSession(parent => SessionLauncher.Embedded(model, sftp, parent), text => { status.Text = text; _status.Text = text; });
            var content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") }; content.Children.Add(native); Grid.SetRow(status, 1); content.Children.Add(status);
            AddSession(new SessionTab((sftp ? "SFTP · " : "") + model.Name, content, native));
        } catch (Exception ex) { _status.Text = "Connexion : " + ex.Message; }
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
        } finally { CloseSession(b); CloseSession(a); await Task.WhenAll(first.ShutdownCompletion, second.ShutdownCompletion).WaitAsync(TimeSpan.FromSeconds(8)); }
        Console.WriteLine("PASS: two X11 terminals embed, survive tab switching and close their processes with their tabs.");
    }
}
