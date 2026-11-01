using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    internal async Task VerifyProfileWorkflowUi()
    {
        if (!App.Smoke) throw new InvalidOperationException("Isolated UI fixture required.");
        var items = _connections.ToArray(); var folders = _folders.ToArray(); bool loaded = _storeLoaded;
        string activePath = _dataPath;
        void Click(Window dialog, string label) => dialog.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        TextBox Input(Window dialog, string name) => dialog.GetLogicalDescendants().OfType<TextBox>().Single(c => c.Name == name);
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!condition()) throw new Exception("Profile workflow UI did not reach its expected state.");
        }
        try {
            _storeLoaded = true; _connections.Clear(); _folders.Clear();
            var first = new Connection { Name = "Auto-save fixture", Host = "first.example.invalid" };
            var second = new Connection { Name = "Other fixture", Host = "second.example.invalid" };
            _connections.Add(first); _connections.Add(second); Persist(_connections); Filter(); Edit(first); _autoSaveEnabled = true;
            _name.Text = "Saved automatically"; _user.Text = "alice"; _port.Value = 2222;
            await Until(() => Connections.LoadStore(_dataPath).Items.Any(c => c.Name == "Saved automatically" && c.User == "alice" && c.Port == 2222));
            if (_key.Text?.Length > 0 || SessionLauncher.Arguments(Read()).Contains("-i")) throw new Exception("Auto-save requires an SSH key.");
            _description.Text = "Flushed on selection"; _list.SelectedItem = _nodes[second];
            if (Connections.LoadStore(_dataPath).Items[0].Description != "Flushed on selection" || !ReferenceEquals(_editing, second)) throw new Exception("Selection discarded pending edits.");
            string before = File.ReadAllText(_dataPath); _name.Text = "";
            await Task.Delay(850);
            if (!_autoSaveDirty || File.ReadAllText(_dataPath) != before || FlushAutoSave()) throw new Exception("Invalid edits overwrote the profile.");
            _list.SelectedItem = _nodes[_connections[0]]; if (!ReferenceEquals(_editing, second)) throw new Exception("Invalid edits were silently discarded on selection.");
            _name.Text = second.Name; _clipboard.IsChecked = true; _icon.SelectedItem = "Linux";
            await Until(() => Connections.LoadStore(_dataPath).Items[1].Icon == "Linux");
            _profileKey = ProfileEncryption.CreateKey("synthetic automatic-save phrase");
            _profileRevision = Connections.Save(_connections, _folders, _dataPath, _profileKey, _profileRevision);
            _host.Text = "encrypted-auto.example.invalid";
            await Until(() => Connections.LoadStore(_dataPath, _profileKey).Items[1].Host == "encrypted-auto.example.invalid");
            if (!ProfileEncryption.IsEncryptedFile(_dataPath)) throw new Exception("Automatic saving removed profile encryption.");
            _key.Text = "/synthetic/old-key";
            _certificate.Text = "/synthetic/old-cert.pub";
            if (!FlushAutoSave()) throw new Exception("Fixture key reference was not saved.");
            await ChooseKey(dialog => Click(dialog, "Sans clé"));
            await Until(() => Connections.LoadStore(_dataPath, _profileKey).Items[1] is { Identity.Length: 0, CertificateFile.Length: 0 });
            if (_key.Text != "") throw new Exception("The explicit no-key option did not clear the key.");
            CancelAutoSave(); _autoSaveEnabled = false;
            string? created = await ChooseStartupProfile(_dataPath, opened: dialog => { Input(dialog, "ProfileName").Text = "GUI created"; Click(dialog, "Créer un profil"); });
            if (created == null || Connections.LoadStore(created).Items.Length != 0 || File.ReadAllText(activePath).Length == 0) throw new Exception("Startup creation damaged the active profile.");
            string? restored = await ChooseStartupProfile(created, opened: dialog => { Input(dialog, "BackupPath").Text = activePath; Click(dialog, "Restaurer"); });
            if (restored == null || Connections.LoadStore(restored, _profileKey).Items.Length != 2) throw new Exception("Startup restore failed.");
            string? opened = await ChooseStartupProfile(created, opened: dialog => { Input(dialog, "ProfilePath").Text = restored; Click(dialog, "Ouvrir"); });
            if (opened != restored) throw new Exception("Startup could not choose another encrypted profile.");
            string? canceled = await ChooseStartupProfile(created, opened: dialog => dialog.Close());
            if (canceled != null || !File.Exists(restored)) throw new Exception("Canceling the profile chooser changed files.");
            Window? deletion = null;
            var cancelDelete = ChooseStartupProfile(created, opened: dialog => { deletion = dialog; Click(dialog, "Supprimer…"); }, confirmDelete: confirm => confirm.Close(false));
            await Task.Delay(100); deletion!.Close(); await cancelDelete;
            if (!File.Exists(created)) throw new Exception("Canceled profile deletion removed the profile.");
            var actualDelete = ChooseStartupProfile(created, opened: dialog => { deletion = dialog; Click(dialog, "Supprimer…"); }, confirmDelete: confirm => confirm.Close(true));
            await Until(() => !File.Exists(created)); deletion!.Close(); await actualDelete;
            string archive = Directory.EnumerateFiles(Path.GetDirectoryName(created)!, "*.bak").Single();
            if (Connections.LoadStore(archive).Items.Length != 0 || !File.Exists(activePath)) throw new Exception("Profile deletion lost the recoverable archive or active profile.");
            Window? preview = null;
            var showing = ChooseStartupProfile(restored, opened: dialog => preview = dialog);
            await Task.Delay(120);
            var visible = preview ?? throw new Exception("Profile chooser preview did not open.");
            foreach (var button in visible.GetLogicalDescendants().OfType<Button>().Where(b => b.Content?.ToString() is "Ouvrir" or "Créer un profil" or "Restaurer" or "Supprimer…" or "Annuler")) {
                var position = button.TranslatePoint(new Avalonia.Point(), visible);
                if (position == null || position.Value.Y + button.Bounds.Height > visible.ClientSize.Height) throw new Exception("Startup profile actions are clipped.");
            }
            using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)visible.ClientSize.Width, (int)visible.ClientSize.Height))) {
                image.Render((Avalonia.Visual)visible.Content!);
                image.Save(Environment.GetCommandLineArgs().Single(a => a.StartsWith("--smoke-ui="))[11..].Replace(".png", "-profiles.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            visible.Close(); await showing;
            var closing = new MainWindow();
            try {
                closing._storeLoaded = true;
                var fixture = new Connection { Name = "Closing fixture", Host = "closing.example.invalid" };
                closing._connections.Add(fixture); closing.Persist(closing._connections); closing.Filter(); closing.Edit(fixture); closing._autoSaveEnabled = true;
                string? savedOnClose = null; closing.Closing += (_, e) => { if (!e.Cancel) savedOnClose = Connections.LoadStore(closing._dataPath).Items.Single().Description; };
                closing.Show(this); closing._name.Text = ""; closing.Close();
                if (!closing.IsVisible) throw new Exception("Closing discarded invalid pending edits.");
                closing._name.Text = fixture.Name; closing._description.Text = "Saved before close"; closing.Close();
                if (closing.IsVisible || savedOnClose != "Saved before close") throw new Exception("Closing did not flush pending edits.");
            } finally { closing.CancelAutoSave(); closing._autoSaveEnabled = false; if (!closing._closing) closing.Close(); }
            Console.WriteLine("PASS: automatic plaintext/encrypted saves, input validation, selection/close flush, optional SSH key, startup creation/restore/open/cancel and recoverable deletion; isolated profiles only.");
        } finally {
            CancelAutoSave(); _autoSaveEnabled = false; _profileKey?.Dispose(); _profileKey = null;
            _dataPath = activePath; _storeLoaded = loaded; _connections.Clear(); foreach (var item in items) _connections.Add(item);
            _folders.Clear(); _folders.AddRange(folders); ForgetTreeState(); Filter(); New(); UpdateProfileActions();
            string profiles = Path.Combine(Path.GetDirectoryName(_defaultProfilePath)!, "profiles"); if (Directory.Exists(profiles)) Directory.Delete(profiles, true);
            CleanupSmokeStore();
        }
    }
}
