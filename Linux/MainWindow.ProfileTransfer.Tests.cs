using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    internal async Task VerifyPortableProfileUi()
    {
        if (!App.Smoke || _profileKey != null) throw new InvalidOperationException("Portable fixtures require the isolated smoke profile.");
        var originalItems = _connections.ToArray(); var originalFolders = _folders.ToArray(); bool loaded = _storeLoaded;
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-portable-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "profile.mgremote"); const string phrase = "synthetic portable GUI phrase";
        void Click(Window dialog, string name) => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        void Fill(Window dialog, string value) { foreach (var input in dialog.GetLogicalDescendants().OfType<TextBox>()) input.Text = value; }
        try {
            _storeLoaded = true; _connections.Clear(); _folders.Clear(); _connections.Add(new Connection { Name = "Portable GUI", Host = "portable-ui.example.invalid", FolderPath = ["Portable"] });
            Persist(_connections); string active = File.ReadAllText(_dataPath);
            var preview = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task canceledExport = ExportPortableDialog(archive, "missing", dialog => preview.TrySetResult(dialog));
            var exportWindow = await preview.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(120);
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)exportWindow.ClientSize.Width, (int)exportWindow.ClientSize.Height))) {
                image.Render((Visual)exportWindow.Content!);
                image.Save(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..].Replace(".png", "-export.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var exportButton = exportWindow.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Exporter");
            var buttonPosition = exportButton.TranslatePoint(new Point(), exportWindow);
            if (buttonPosition == null || buttonPosition.Value.Y + exportButton.Bounds.Height > exportWindow.ClientSize.Height) throw new Exception("Export action is clipped.");
            Fill(exportWindow, phrase); Click(exportWindow, "Exporter"); exportWindow.Close(); await canceledExport;
            if (File.Exists(archive)) throw new Exception("Canceled export created an archive.");
            await ExportPortableDialog(archive, "missing", dialog => { Fill(dialog, phrase); Click(dialog, "Exporter"); });
            if (!File.Exists(archive) || File.ReadAllText(_dataPath) != active || _profileKey != null) throw new Exception("Export dialog altered the active profile.");
            string archiveBytes = File.ReadAllText(archive);
            _connections.Clear(); _folders.Clear(); _connections.Add(new Connection { Name = "Existing on target", Host = "local.example.invalid" }); Persist(_connections);
            string before = File.ReadAllText(_dataPath);
            await ImportPortableDialog(archive, dialog => { Fill(dialog, phrase); Click(dialog, "Importer"); dialog.Close(); });
            if (File.ReadAllText(_dataPath) != before || _profileKey != null || _connections.Count != 1) throw new Exception("Canceled import changed the target profile.");
            var opened = new TaskCompletionSource<Window>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task invalid = ImportPortableDialog(archive, dialog => { Fill(dialog, "incorrect portable GUI phrase"); opened.TrySetResult(dialog); Click(dialog, "Importer"); });
            var wrong = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try {
                var deadline = DateTime.UtcNow.AddSeconds(7);
                while (!wrong.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Phrase secrète incorrecte") == true) && DateTime.UtcNow < deadline) await Task.Delay(20);
                if (!wrong.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("Phrase secrète incorrecte") == true) || File.ReadAllText(_dataPath) != before) throw new Exception("Wrong import phrase was not refused without changing the target.");
            } finally { wrong.Close(); await invalid; }
            await ImportPortableDialog(archive, dialog => { Fill(dialog, phrase); dialog.GetLogicalDescendants().OfType<CheckBox>().Single().IsChecked = false; Click(dialog, "Importer"); });
            if (_profileKey != null || ProfileEncryption.IsEncryptedFile(_dataPath) || _connections.Count != 2) throw new Exception("Import ignored explicit opt-out of local profile encryption.");
            await ImportPortableDialog(archive, dialog => { Fill(dialog, phrase); Click(dialog, "Importer"); });
            if (_profileKey == null || _connections.Count != 2 || !ProfileEncryption.IsEncryptedFile(_dataPath) || Connections.LoadStore(_dataPath, _profileKey).Items.Length != 2) throw new Exception("Portable import did not merge and encrypt the new local profile.");
            var key = _profileKey;
            await ImportPortableDialog(archive, dialog => { Fill(dialog, phrase); Click(dialog, "Importer"); });
            if (!ReferenceEquals(key, _profileKey) || _connections.Count != 2 || File.ReadAllText(archive) != archiveBytes) throw new Exception("Repeated import replaced the local key, duplicated connections or changed the archive.");
            Console.WriteLine("PASS: portable export/import dialogs, cancellation, wrong phrase, merging on target, optional local encryption and repeated-import deduplication; isolated profiles only.");
        } finally {
            _profileKey?.Dispose(); _profileKey = null; _storeLoaded = loaded; _connections.Clear(); foreach (var item in originalItems) _connections.Add(item);
            _folders.Clear(); _folders.AddRange(originalFolders); ForgetTreeState(); Filter(); New(); UpdateProfileActions(); CleanupSmokeStore(); Directory.Delete(directory, recursive: true);
        }
    }
}
