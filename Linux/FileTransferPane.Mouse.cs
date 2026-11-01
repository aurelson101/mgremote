using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed partial class FileTransferPane
{
    private void InstallMouseMenus()
    {
        MenuItem Item(string label, Func<Task> action)
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => { try { if (!_disposed) await action(); } catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; } };
            return item;
        }
        ContextMenu Editor(TextBox input, bool local)
        {
            var items = new List<Control> {
                Item("Copier le texte", () => { input.Copy(); return Task.CompletedTask; }),
                Item("Coller le texte", () => { input.Paste(); return Task.CompletedTask; }),
                Item("Tout sélectionner", () => { input.SelectAll(); return Task.CompletedTask; }), new Separator()
            };
            if (local) {
                items.Add(Item("Coller fichiers / dossiers à envoyer", PasteFiles));
                items.Add(Item("Copier les fichiers / dossiers locaux", CopyLocalFiles));
                items.Add(Item("Copier le dossier reçu", CopyReceivedFolder));
                items.Add(Item("Ouvrir le dossier local", () => { OpenLocalFolder(); return Task.CompletedTask; }));
            }
            return new ContextMenu { ItemsSource = items };
        }
        // The Fluent TextBox flyout otherwise takes precedence over these context menus.
        _local.ContextFlyout = null; _remote.ContextFlyout = null;
        _local.ContextMenu = Editor(_local, true); _remote.ContextMenu = Editor(_remote, false);
        ContextMenu = new ContextMenu { ItemsSource = new[] {
            Item("Coller fichiers / dossiers à envoyer", PasteFiles), Item("Copier le dossier reçu", CopyReceivedFolder),
            Item("Ouvrir le dossier local", () => { OpenLocalFolder(); return Task.CompletedTask; })
        } };
    }
    private async Task CopyLocalFiles()
    {
        if (_direction.SelectedIndex == 1) { await CopyReceivedFolder(); return; }
        await CopyFiles(FileTransfer.Sources(_local.Text ?? ""));
    }
    private Task CopyReceivedFolder()
    {
        if (_receivedFolder == null || !Directory.Exists(_receivedFolder)) throw new IOException("Aucun dossier reçu disponible. Téléchargez d’abord les fichiers ou ouvrez leur dossier local.");
        return CopyFiles([_receivedFolder]);
    }
    private async Task CopyFiles(IEnumerable<string> paths)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new IOException("Accès aux fichiers locaux indisponible.");
        var items = new List<IStorageItem>();
        foreach (string path in paths.Take(FileTransfer.MaxSources + 1)) {
            if (items.Count == FileTransfer.MaxSources) throw new IOException("100 éléments maximum dans le presse-papiers.");
            IStorageItem? item = Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : await storage.TryGetFileFromPathAsync(path);
            items.Add(item ?? throw new IOException("Fichier ou dossier local introuvable : " + path));
        }
        if (_disposed) return;
        await Clipboard().SetFilesAsync(items);
        _status.Text = "Fichiers/dossiers copiés : clic droit → Coller dans votre gestionnaire de fichiers. Les réceptions interrompues peuvent être partielles.";
    }
}
