using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed partial class FileTransferPane
{
    internal async Task VerifyClipboardUi(string screenshot)
    {
        string root = Path.Combine(Path.GetTempPath(), "mgremote-file-clipboard-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string filePath = Path.Combine(root, "local file.txt"), folderPath = Path.Combine(root, "folder");
            File.WriteAllText(filePath, "synthetic clipboard fixture"); Directory.CreateDirectory(folderPath);
            var top = TopLevel.GetTopLevel(this) ?? throw new Exception("Transfer UI is not attached.");
            using var file = await top.StorageProvider.TryGetFileFromPathAsync(filePath) ?? throw new Exception("Fixture file not exposed by storage provider.");
            using var folder = await top.StorageProvider.TryGetFolderFromPathAsync(folderPath) ?? throw new Exception("Fixture folder not exposed by storage provider.");
            await Clipboard().SetFilesAsync([file, folder]);
            async Task MouseMenu(string label)
            {
                var position = _local.PointToScreen(new Point(15, 15));
                string window = (top.TryGetPlatformHandle() ?? throw new Exception("No native window for mouse test.")).Handle.ToInt64().ToString();
                await MainWindow.RunDesktopTool("xdotool", "windowfocus", "--sync", window);
                await MainWindow.RunDesktopTool("xdotool", "mousemove", "--sync", position.X.ToString(), position.Y.ToString(), "click", "1");
                await Task.Delay(100);
                await MainWindow.RunDesktopTool("xdotool", "click", "3");
                await Task.Delay(150);
                var menu = _local.ContextMenu ?? throw new Exception("Local transfer menu is missing.");
                if (!menu.IsOpen) throw new Exception("Right-click did not open the transfer context menu.");
                var item = menu.ItemsSource!.OfType<MenuItem>().Single(m => m.Header?.ToString() == label);
                var screen = item.PointToScreen(new Point(12, item.Bounds.Height / 2));
                await MainWindow.RunDesktopTool("xdotool", "mousemove", "--sync", screen.X.ToString(), screen.Y.ToString(), "click", "1");
                await Task.Delay(150);
            }
            await MouseMenu("Coller fichiers / dossiers à envoyer");
            if (_direction.SelectedIndex != 0 || _recursive.IsChecked != true || _overwrite.IsChecked == true || FileTransfer.Sources(_local.Text ?? "").Length != 2 || _session != null) throw new Exception("Clipboard did not prepare files/folders without starting an upload.");
            var dropData = new DataTransfer(); dropData.Add(DataTransferItem.CreateFile(file));
            ApplyFiles(dropData.TryGetFiles() ?? []);
            if (_local.Text != filePath || _recursive.IsChecked == true) throw new Exception("Dropped file data was not handled like clipboard files.");
            await PasteFiles(); _remote.Text = "/remote-folder/";
            await Start();
            if (_session != null || _status.Text?.Contains("Confirmez") != true) throw new Exception("Clipboard upload bypassed explicit overwrite confirmation.");
            await Task.Delay(100);
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Bounds.Width), Math.Max(1, (int)Bounds.Height)))) {
                image.Render(this); image.Save(screenshot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            _local.Text = root; _direction.SelectedIndex = 1; _recursive.IsChecked = true;
            var args = FileTransfer.Arguments(_connection, root, "/remote folder/", true, true);
            if (!args.Contains("-r") || args[^1] != root) throw new Exception("Recursive download did not retain its local destination.");
            _receivedFolder = folderPath;
            await MouseMenu("Copier le dossier reçu");
            var copiedFolder = await Clipboard().TryGetFilesAsync();
            if (copiedFolder?.Single().TryGetLocalPath()?.TrimEnd(Path.DirectorySeparatorChar) != folderPath) throw new Exception("Mouse context menu did not export the received folder to the system clipboard.");
            _direction.SelectedIndex = 0; _local.Text = filePath;
            await MouseMenu("Copier les fichiers / dossiers locaux");
            var copiedFile = await Clipboard().TryGetFilesAsync();
            if (copiedFile?.Single().TryGetLocalPath() != filePath) throw new Exception("Mouse context menu did not export local files to the system clipboard.");
            string externalUris = await MainWindow.RunDesktopTool("xclip", "-o", "-selection", "clipboard", "-t", "text/uri-list");
            if (!externalUris.Contains(file.Path.AbsoluteUri, StringComparison.Ordinal)) throw new Exception("External file manager clipboard format was not published.");
            await Clipboard().ClearAsync();
            Console.WriteLine("PASS: real right-click menus paste files/folders and copy local files/received folders to the native clipboard; drop data, recursion and explicit send confirmation; no remote transfer.");
        } finally { Directory.Delete(root, true); }
    }
}
