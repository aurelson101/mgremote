using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private bool _autoSaveEnabled, _autoSaveDirty, _hydratingEditor, _savingEditor;
    private IDisposable? _pendingAutoSave;

    private void InstallAutoSave()
    {
        _autoSaveEnabled = !App.Smoke;
        foreach (var control in _profileEditor!.GetLogicalDescendants().OfType<Control>()) {
            control.PropertyChanged += (_, e) => {
                if (e.Property == TextBox.TextProperty || e.Property == SelectingItemsControl.SelectedIndexProperty ||
                    e.Property == ToggleButton.IsCheckedProperty || e.Property == NumericUpDown.ValueProperty) QueueAutoSave();
            };
        }
        Closing += (_, e) => { if (!FlushAutoSave()) e.Cancel = true; };
        Closed += (_, _) => CancelAutoSave();
    }
    private void QueueAutoSave()
    {
        if (!_autoSaveEnabled || _hydratingEditor || _savingEditor || !_storeLoaded || _profileBusy || _closing) return;
        if (_editing == null && string.IsNullOrWhiteSpace(_host.Text)) return;
        _autoSaveDirty = true; _pendingAutoSave?.Dispose();
        _status.Text = "Modifications en cours — sauvegarde automatique…";
        _pendingAutoSave = DispatcherTimer.RunOnce(() => { _pendingAutoSave = null; FlushAutoSave(); }, TimeSpan.FromMilliseconds(700));
    }
    private bool FlushAutoSave()
    {
        if (!_autoSaveDirty || !_autoSaveEnabled || _hydratingEditor || _savingEditor) return true;
        if (_profileBusy || !_storeLoaded) return false;
        return SaveEditor(automatic: true);
    }
    private void CancelAutoSave()
    {
        _pendingAutoSave?.Dispose(); _pendingAutoSave = null; _autoSaveDirty = false;
    }
    private bool PrepareEditorChange()
    {
        if (FlushAutoSave()) return true;
        _rebuildingTree = true;
        try { if (_editing != null && _nodes.TryGetValue(_editing, out var node)) _list.SelectedItem = node; }
        finally { _rebuildingTree = false; }
        return false;
    }
}
