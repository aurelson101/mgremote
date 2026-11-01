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
            await Clipboard().SetFilesAsync([file, folder]); await PasteFiles();
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
            await Clipboard().ClearAsync();
            Console.WriteLine("PASS: native file/folder clipboard round trip, drop data, recursive mode, explicit send confirmation and download arguments; no remote transfer.");
        } finally { Directory.Delete(root, true); }
    }
}
