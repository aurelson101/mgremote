using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Text.Json;

namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private readonly List<string[]> _folders = [];
    private readonly Dictionary<string, TreeViewItem> _folderNodes = [];
    private readonly string _dataPath = App.Smoke ? Path.Combine(Path.GetTempPath(), "mgremote-ui-" + Guid.NewGuid().ToString("N"), "connections.json") : Connections.DataPath;
    private static string FolderKey(string[] path) => JsonSerializer.Serialize(path);
    private static bool InFolder(string[] candidate, string[] parent) => candidate.Length >= parent.Length && candidate.Take(parent.Length).SequenceEqual(parent);
    private void SelectTreeTarget(TreeViewItem? item) => _list.SelectedItem = item ?? _list.ItemsSource?.OfType<TreeViewItem>().FirstOrDefault();
    private void InstallTreeMenu()
    {
        MenuItem Entry(string name, Action action) { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); return item; }
        var create = Entry("Nouvelle connexion", NewConnectionFromTree);
        var folder = Entry("Nouveau dossier…", async () => await CreateFolderDialog());
        var edit = Entry("Modifier / renommer…", async () => await EditTreeItem());
        var connect = Entry("Connecter", () => Launch(false));
        var duplicate = Entry("Dupliquer", Duplicate);
        var sftp = Entry("SFTP", () => Launch(true));
        var delete = Entry("Supprimer", async () => {
            try {
                if (_list.SelectedItem is TreeViewItem { Tag: string[] path }) DeleteFolder(path);
                else await Delete();
            } catch (Exception ex) { _status.Text = ex.Message; }
        });
        var menu = new ContextMenu { ItemsSource = new Control[] { create, folder, new Separator(), connect, edit, duplicate, sftp, new Separator(), delete } };
        void UpdateActions()
        {
            object? tag = (_list.SelectedItem as TreeViewItem)?.Tag;
            bool connection = tag is Connection;
            create.IsEnabled = folder.IsEnabled = _storeLoaded;
            edit.IsEnabled = connection || tag is string[] { Length: > 0 };
            delete.IsEnabled = _storeLoaded && edit.IsEnabled;
            connect.IsEnabled = connection; duplicate.IsEnabled = connection;
            sftp.IsEnabled = tag is Connection { Protocol: "SSH" };
        }
        menu.Opening += (_, _) => UpdateActions();
        _list.SelectionChanged += (_, _) => UpdateActions();
        UpdateActions();
        _list.ContextMenu = menu;
        _list.AddHandler(InputElement.PointerPressedEvent, (_, e) => {
            if (!e.GetCurrentPoint(_list).Properties.IsRightButtonPressed) return;
            var visual = e.Source as Visual;
            SelectTreeTarget(visual as TreeViewItem ?? visual?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault());
        }, RoutingStrategies.Tunnel);
        _list.KeyDown += async (_, e) => {
            if (e.Key == Key.F2) { await EditTreeItem(); e.Handled = true; }
            else if (e.Key == Key.Insert) { NewConnectionFromTree(); e.Handled = true; }
        };
    }
    private void Persist(IEnumerable<Connection> connections, IEnumerable<string[]>? folders = null)
    {
        if (!_storeLoaded) throw new IOException("Fichier existant non chargé : écriture bloquée pour préserver vos données.");
        Connections.Save(connections, folders ?? _folders, _dataPath);
    }
    private void NewConnectionFromTree()
    {
        try {
            var parent = (_list.SelectedItem as TreeViewItem)?.Tag switch { Connection c => c.FolderPath, string[] path => path, _ => _selectedFolder };
            string name = "Nouvelle connexion"; int suffix = 2;
            while (_connections.Any(c => c.FolderPath.SequenceEqual(parent) && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = "Nouvelle connexion (" + suffix++ + ")";
            var draft = new Connection { Name = name, FolderPath = parent.ToArray(), IsDraft = true };
            Persist(_connections.Append(draft)); _connections.Add(draft); _search.Text = ""; Filter();
            _list.SelectedItem = _nodes[draft]; Edit(draft); _name.Focus();
            _status.Text = "Connexion créée. Choisissez le protocole, renseignez l’hôte puis enregistrez.";
        } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private void CreateFolder(string name)
    {
        string[] path = [.._selectedFolder, name.Trim()]; Connections.ValidateFolderPath(path);
        if (_folderNodes.ContainsKey(FolderKey(path))) throw new ArgumentException("Ce dossier existe déjà.");
        Persist(_connections, _folders.Append(path)); _folders.Add(path); _search.Text = ""; Filter();
        _list.SelectedItem = _folderNodes[FolderKey(path)]; _status.Text = "Dossier créé.";
    }
    private async Task CreateFolderDialog()
    {
        try { string? name = await AskTreeName("Nouveau dossier", "Nouveau dossier"); if (name != null) CreateFolder(name); }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task EditTreeItem()
    {
        try {
            if (_list.SelectedItem is TreeViewItem { Tag: Connection connection }) { Edit(connection); _name.Focus(); return; }
            if (_list.SelectedItem is TreeViewItem { Tag: string[] { Length: > 0 } path }) {
                string? name = await AskTreeName("Renommer le dossier", path[^1]); if (name != null) RenameFolder(path, name);
            }
        } catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task<string?> AskTreeName(string title, string current)
    {
        var dialog = new Window { Title = title, Width = 430, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Text = current }; var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Annuler", () => dialog.Close())); actions.Children.Add(Button("Valider", () => dialog.Close(name.Text)));
        panel.Children.Add(name); panel.Children.Add(actions); dialog.Content = panel;
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await dialog.ShowDialog<string?>(this);
    }
    private void RenameFolder(string[] original, string name)
    {
        string[] target = [..original[..^1], name.Trim()]; Connections.ValidateFolderPath(target);
        if (!target.SequenceEqual(original) && _folderNodes.ContainsKey(FolderKey(target))) throw new ArgumentException("Ce dossier existe déjà.");
        string[] Rewrite(string[] path) => InFolder(path, original) ? [..target, ..path.Skip(original.Length)] : path;
        var next = _connections.Select(c => InFolder(c.FolderPath, original) ? c with { FolderPath = Rewrite(c.FolderPath) } : c).ToArray();
        var folders = _folders.Select(Rewrite).ToArray(); Persist(next, folders);
        _connections.Clear(); foreach (var c in next) _connections.Add(c); _folders.Clear(); _folders.AddRange(folders);
        _selectedFolder = target; _search.Text = ""; Filter(); _list.SelectedItem = _folderNodes[FolderKey(target)]; _status.Text = "Dossier et chemins des connexions renommés.";
    }
    private void DeleteFolder(string[] path)
    {
        if (path.Length == 0) return;
        if (_connections.Any(c => InFolder(c.FolderPath, path)) || _folders.Any(f => f.Length > path.Length && InFolder(f, path))) throw new InvalidOperationException("Dossier non vide : supprimez ou déplacez d’abord son contenu.");
        var next = _folders.Where(f => !f.SequenceEqual(path)).ToArray(); Persist(_connections, next);
        _folders.Clear(); _folders.AddRange(next); _selectedFolder = path[..^1]; _search.Text = ""; Filter();
        _list.SelectedItem = _folderNodes.TryGetValue(FolderKey(_selectedFolder), out var parent) ? parent : _folderNodes[FolderKey([])]; _status.Text = "Dossier vide supprimé.";
    }
    internal void VerifyTreeActions()
    {
        try {
            CreateFolder("Parent"); CreateFolder("Child");
            var menu = _list.ContextMenu!;
            var create = menu.ItemsSource!.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Nouvelle connexion");
            SelectTreeTarget(_folderNodes[FolderKey(["Parent", "Child"])]);
            menu.Open(_list);
            if (!create.IsEnabled || menu.ItemsSource!.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Connecter").IsEnabled) throw new Exception("Folder context menu actions are invalid.");
            create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            var draft = _connections.Single();
            if (!draft.IsDraft || !draft.FolderPath.SequenceEqual(new[] { "Parent", "Child" }) || !ReferenceEquals(_editing, draft)) throw new Exception("Context creation did not target the selected folder.");
            bool blocked = false; try { SessionLauncher.Arguments(draft); } catch (ArgumentException) { blocked = true; }
            if (!blocked) throw new Exception("An unconfigured draft can connect.");
            _host.Text = "server"; _encryptedPassword = "v1:synthetic-envelope"; Save();
            if (_connections.Single().IsDraft || _connections.Single().Host != "server") throw new Exception("Draft editor did not save the endpoint.");
            RenameFolder(["Parent"], "Renamed");
            var stored = Connections.LoadStore(_dataPath);
            if (!stored.Items.Single().FolderPath.SequenceEqual(new[] { "Renamed", "Child" }) || stored.Items.Single().EncryptedPassword != "v1:synthetic-envelope") throw new Exception("Folder rename lost descendants or encrypted fields.");
            SelectTreeTarget(_folderNodes[FolderKey(["Renamed"])]); CreateFolder("Empty");
            if (!Connections.LoadStore(_dataPath).Folders.Any(f => f.SequenceEqual(new[] { "Renamed", "Empty" }))) throw new Exception("Empty folder was not persisted.");
            DeleteFolder(["Renamed", "Empty"]);
            if (Connections.LoadStore(_dataPath).Folders.Any(f => f.SequenceEqual(new[] { "Renamed", "Empty" }))) throw new Exception("Empty folder deletion did not persist.");
            bool refused = false; try { DeleteFolder(["Renamed"]); } catch (InvalidOperationException) { refused = true; }
            if (!refused || _connections.Count != 1) throw new Exception("Nonempty folder deletion was not protected.");
            SelectTreeTarget(_nodes[_connections.Single()]);
            menu.ItemsSource!.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Modifier / renommer…").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (!ReferenceEquals(_editing, _connections.Single())) throw new Exception("Context editing did not select the connection.");
            SelectTreeTarget(null); create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (_connections.Last().FolderPath.Length != 0) throw new Exception("Root context creation retained another folder.");
            _host.Text = "server2"; _folder.Text = "Moved > Leaf"; Save();
            Persist(_connections.Where(c => c.Host != "server2"));
            if (!Connections.LoadStore(_dataPath).Folders.Any(f => f.SequenceEqual(new[] { "Moved", "Leaf" }))) throw new Exception("Removing the last connection removed its folder metadata.");
            Console.WriteLine("PASS: tree context creation/editing, draft safety, empty-folder persistence, recursive rename and protected deletion; temporary profile only.");
        } finally { _connections.Clear(); _folders.Clear(); _selectedFolder = []; Filter(); New(); CleanupSmokeStore(); }
    }
    private void CleanupSmokeStore()
    {
        if (!App.Smoke) return;
        try { File.Delete(_dataPath); if (Directory.Exists(Path.GetDirectoryName(_dataPath))) Directory.Delete(Path.GetDirectoryName(_dataPath)!); } catch (IOException) { }
    }
}
