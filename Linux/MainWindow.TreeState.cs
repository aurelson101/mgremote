using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, bool> _folderExpansion = new(StringComparer.Ordinal);
    private IDisposable? _pendingTreeFilter;
    private object? _rememberedTreeSelection;
    private string _treeQuery = "";
    private bool _treeStateInstalled, _rebuildingTree;

    private void RequestFilter()
    {
        _pendingTreeFilter?.Dispose(); _pendingTreeFilter = null;
        if (string.Equals(_search.Text?.Trim() ?? "", _treeQuery, StringComparison.OrdinalIgnoreCase)) return;
        _pendingTreeFilter = DispatcherTimer.RunOnce(() => {
            _pendingTreeFilter = null;
            if (!_closing) Filter();
        }, TimeSpan.FromMilliseconds(150));
    }

    private void CaptureTreeState()
    {
        _pendingTreeFilter?.Dispose(); _pendingTreeFilter = null;
        if (!_treeStateInstalled) {
            _treeStateInstalled = true;
            _rememberedTreeSelection = (_list.SelectedItem as TreeViewItem)?.Tag;
            _list.SelectionChanged += (_, _) => {
                if (_rebuildingTree) return;
                _rememberedTreeSelection = (_list.SelectedItem as TreeViewItem)?.Tag;
                // Explicit selection after creating a draft reveals its parent;
                // restoring an existing selection during rebuild keeps branches collapsed.
                string[]? ancestors = _rememberedTreeSelection switch {
                    Connection c => c.FolderPath,
                    string[] path when path.Length > 0 => path[..^1],
                    _ => null
                };
                if (ancestors != null)
                    for (int length = 0; length <= ancestors.Length; length++)
                        if (_folderNodes.TryGetValue(FolderKey(ancestors[..length]), out var folder)) folder.IsExpanded = true;
            };
        }
        // Search expansion is temporary, so never save its forced-open branches.
        if (_treeQuery.Length == 0)
            foreach (var pair in _folderNodes) _folderExpansion[pair.Key] = pair.Value.IsExpanded;
        if (_rememberedTreeSelection is Connection selected && !_connections.Any(c => ReferenceEquals(c, selected)))
            _rememberedTreeSelection = null;
    }

    private bool TreeFolderExpanded(string[] path, string query) =>
        query.Length > 0 || !_folderExpansion.TryGetValue(FolderKey(path), out bool expanded) || expanded;

    private void RememberTreeSelection(Connection connection) =>
        _rememberedTreeSelection = _connections.Any(c => ReferenceEquals(c, connection)) ? connection : null;

    private void RestoreTreeSelection(string query)
    {
        _treeQuery = query;
        _list.SelectedItem = _rememberedTreeSelection switch {
            Connection c when _nodes.TryGetValue(c, out var node) => node,
            string[] path when _folderNodes.TryGetValue(FolderKey(path), out var folder) => folder,
            _ => null
        };
        if (query.Length == 0) {
            foreach (string key in _folderExpansion.Keys.Except(_folderNodes.Keys).ToArray()) _folderExpansion.Remove(key);
            if (_rememberedTreeSelection is string[] path && !_folderNodes.ContainsKey(FolderKey(path))) _rememberedTreeSelection = null;
        }
    }

    private void ForgetTreeState()
    {
        _pendingTreeFilter?.Dispose(); _pendingTreeFilter = null;
        _rememberedTreeSelection = null; _treeQuery = ""; _folderExpansion.Clear();
        _rebuildingTree = true;
        try { _list.SelectedItem = null; _list.ItemsSource = null; _folderNodes.Clear(); _nodes.Clear(); }
        finally { _rebuildingTree = false; }
    }

    internal async Task VerifyTreeState()
    {
        if (!App.Smoke) throw new InvalidOperationException("Tree fixtures require the temporary smoke profile.");
        var originals = _connections.ToArray(); var originalFolders = _folders.ToArray();
        string originalSearch = _search.Text ?? "";
        try {
            _connections.Clear(); _folders.Clear(); ForgetTreeState(); _search.Text = "";
            var windows = new Connection { Name = "Windows fixture", Host = "windows.example.invalid", Protocol = "RDP", Port = 3389, FolderPath = ["Fixtures"] };
            var draft = new Connection { Name = "Linux draft", FolderPath = ["Fixtures"], IsDraft = true };
            _folders.Add(["Fixtures"]); _connections.Add(windows); _connections.Add(draft); Filter();
            _list.SelectedItem = _nodes[draft]; _name.Text = "Unsaved draft text";
            _folderNodes[FolderKey(["Fixtures"])].IsExpanded = false;
            Filter();
            if (_folderNodes[FolderKey(["Fixtures"])].IsExpanded || !ReferenceEquals((_list.SelectedItem as TreeViewItem)?.Tag, draft) || _name.Text != "Unsaved draft text")
                throw new Exception("Tree rebuild lost collapsed folders, selection or unsaved draft edits.");

            int rebuilds = 0;
            void Rebuilt(object? sender, AvaloniaPropertyChangedEventArgs args) { if (args.Property == ItemsControl.ItemsSourceProperty) rebuilds++; }
            _list.PropertyChanged += Rebuilt;
            try {
                _search.Text = "win"; RequestFilter(); await Task.Delay(40);
                if (rebuilds != 0) throw new Exception("Connection search was not debounced.");
                _search.Text = "windows"; RequestFilter(); await Task.Delay(230);
                if (rebuilds != 1 || _nodes.Count != 1 || !_nodes.ContainsKey(windows) || !_folderNodes[FolderKey(["Fixtures"])].IsExpanded || _list.SelectedItem != null || _name.Text != "Unsaved draft text")
                    throw new Exception("Debounced search lost draft edits or failed to expand only matching branches.");
            } finally { _list.PropertyChanged -= Rebuilt; }

            _search.Text = ""; Filter();
            if (_folderNodes[FolderKey(["Fixtures"])].IsExpanded || !ReferenceEquals((_list.SelectedItem as TreeViewItem)?.Tag, draft) || _name.Text != "Unsaved draft text")
                throw new Exception("Clearing search did not restore expansion, hidden selection and draft edits.");
            _search.Text = "linux"; Filter(); _name.Text = "Renamed draft"; Save();
            var saved = _connections.Single(c => c.IsDraft);
            if (saved.Name != "Renamed draft" || _list.SelectedItem != null) throw new Exception("Saved rename did not update the filtered tree.");
            _search.Text = ""; Filter();
            if (!ReferenceEquals((_list.SelectedItem as TreeViewItem)?.Tag, saved) || _folderNodes[FolderKey(["Fixtures"])].IsExpanded)
                throw new Exception("Saved edits did not preserve hidden selection or collapsed folders.");
            _connections.Remove(saved); Filter();
            if (_rememberedTreeSelection != null) throw new Exception("Removed connection retained by tree state.");
            _list.SelectedItem = _nodes[windows];
            if (!_folderNodes[FolderKey(["Fixtures"])].IsExpanded) throw new Exception("Explicit node selection did not reveal its collapsed parent.");
            _search.Text = "pending"; RequestFilter(); _search.Text = ""; ForgetTreeState(); await Task.Delay(190);
            if (_list.ItemsSource != null || _rememberedTreeSelection != null || _folderExpansion.Count != 0 || _pendingTreeFilter != null)
                throw new Exception("Clearing tree state retained private profile data or a pending search.");
            Console.WriteLine("PASS: debounced search preserves collapsed branches, hidden selection, unsaved drafts and saved edits; clearing tree state releases profile references.");
        } finally {
            _connections.Clear(); foreach (var connection in originals) _connections.Add(connection);
            _folders.Clear(); _folders.AddRange(originalFolders); ForgetTreeState(); _search.Text = originalSearch; Filter(); New(); CleanupSmokeStore();
        }
    }
}
