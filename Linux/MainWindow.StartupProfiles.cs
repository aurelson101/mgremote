using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private string _defaultProfilePath = "";
    private async Task<string?> ChooseStartupProfile(string current, string message = "", Action<Window>? opened = null, Action<Window>? confirmDelete = null)
    {
        var dialog = new Window { Title = "Profils MGRemote", Width = 680, Height = 550, MinWidth = 500, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Ouvrez une configuration, créez un profil indépendant ou restaurez une sauvegarde. Les clés SSH sont facultatives.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var choices = new ComboBox { ItemsSource = ProfileRecovery.List(_defaultProfilePath).Append(current).Distinct().ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var path = new TextBox { Name = "ProfilePath", Text = current };
        choices.SelectionChanged += (_, _) => { if (choices.SelectedItem is string selected) path.Text = selected; };
        choices.SelectedItem = current;
        panel.Children.Add(new TextBlock { Text = "Profils locaux" }); panel.Children.Add(choices);
        panel.Children.Add(new TextBlock { Text = "Profil à ouvrir (chemin complet)" }); panel.Children.Add(path);
        var error = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        panel.Children.Add(Button("Choisir un fichier…", async () => {
            try { var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Profil MGRemote", AllowMultiple = false }); if (files.Count > 0) path.Text = files[0].TryGetLocalPath(); }
            catch (Exception ex) { error.Text = ex.Message + " Vous pouvez saisir le chemin directement."; }
        }));
        var name = new TextBox { Name = "ProfileName", Text = "Mon profil", PlaceholderText = "Nom du nouveau profil" };
        panel.Children.Add(new TextBlock { Text = "Nom pour créer ou restaurer un profil" }); panel.Children.Add(name);
        var backup = new TextBox { Name = "BackupPath", PlaceholderText = "Chemin du fichier .json, .mgremote ou .bak" };
        panel.Children.Add(new TextBlock { Text = "Sauvegarde à restaurer (une copie indépendante sera créée)" }); panel.Children.Add(backup);
        panel.Children.Add(Button("Choisir une sauvegarde…", async () => {
            try { var files = await dialog.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Sauvegarde du profil", AllowMultiple = false }); if (files.Count > 0) backup.Text = files[0].TryGetLocalPath(); }
            catch (Exception ex) { error.Text = ex.Message; }
        }));
        panel.Children.Add(new TextBlock { Text = "Un mot de passe oublié ne peut pas être récupéré. Créer un autre profil conserve l’ancien. Supprimer déplace le fichier dans une archive .bak récupérable. Une sauvegarde chiffrée conserve son mot de passe.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        error.Margin = new Thickness(18, 0, 18, 8);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 14) };
        bool closed = false, busy = false; Task? pending = null;
        async Task Run(Func<string> operation)
        {
            busy = true; actions.IsEnabled = false;
            try { string selected = await Task.Run(operation); busy = false; if (!closed) dialog.Close(selected); }
            catch (Exception ex) { if (!closed) error.Text = ex.Message; }
            finally { busy = false; if (!closed) actions.IsEnabled = true; }
        }
        var open = Button("Ouvrir", () => { string file = path.Text ?? ""; pending = Run(() => { file = SessionLauncher.IdentityPath(file); ProfileRecovery.Read(file); return file; }); });
        actions.Children.Add(open);
        actions.Children.Add(Button("Créer un profil", () => {
            string label = name.Text ?? "";
            pending = Run(() => { string file = ProfileRecovery.NewPath(Path.GetDirectoryName(_defaultProfilePath)!, label); ProfileRecovery.Create(file); return file; });
        }));
        actions.Children.Add(Button("Restaurer", () => {
            string source = backup.Text ?? "", label = name.Text ?? "";
            pending = Run(() => { string file = ProfileRecovery.NewPath(Path.GetDirectoryName(_defaultProfilePath)!, label); ProfileRecovery.Restore(SessionLauncher.IdentityPath(source), file); return file; });
        }));
        actions.Children.Add(Button("Supprimer…", async () => {
            try {
                string file = SessionLauncher.IdentityPath(path.Text ?? ""); string revision = ProfileEncryption.ReadRevision(file);
                var confirm = new Window { Title = "Supprimer ce profil", Width = 520, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var body = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
                body.Children.Add(new TextBlock { Text = "Retirer ce fichier du chemin sélectionné et le conserver dans une archive .bak ?\n" + file, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                body.Children.Add(Button("Annuler", () => confirm.Close(false))); body.Children.Add(Button("Supprimer et archiver", () => confirm.Close(true))); confirm.Content = body;
                confirm.Opened += (_, _) => confirmDelete?.Invoke(confirm);
                if (!await confirm.ShowDialog<bool>(dialog) || closed) return;
                string archived = ProfileRecovery.Archive(file, revision);
                backup.Text = archived; error.Text = "Profil retiré ; archive restaurable : " + archived;
                choices.ItemsSource = ProfileRecovery.List(_defaultProfilePath);
            } catch (Exception ex) { if (!closed) error.Text = ex.Message; }
        }));
        actions.Children.Add(Button("Annuler", () => dialog.Close()));
        foreach (var button in actions.Children.OfType<Button>()) button.Margin = new Thickness(4);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), Background = DesktopAppearance.Surface };
        layout.Children.Add(new ScrollViewer { Content = panel }); Grid.SetRow(error, 1); layout.Children.Add(error); Grid.SetRow(actions, 2); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Opened += (_, _) => { path.Focus(); opened?.Invoke(dialog); }; dialog.Closed += (_, _) => closed = true; dialog.Closing += (_, e) => { if (busy) e.Cancel = true; };
        string? result = await dialog.ShowDialog<string?>(this); if (pending != null) await pending; return result;
    }
    private async Task SwitchProfile()
    {
        if (_profileBusy || _closing) return;
        bool saved = FlushAutoSave();
        if (_storeLoaded) {
            var dialog = new Window { Title = "Changer de profil", Width = 470, Height = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = saved ? "Les modifications seront enregistrées et toutes les sessions et transferts seront arrêtés avant le changement de profil." : "Les modifications actuelles n’ont pas pu être enregistrées. Continuer les abandonne, conserve le fichier sur disque et arrête toutes les sessions. Annuler permet de corriger la fiche.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            panel.Children.Add(Button("Annuler", () => dialog.Close(false))); panel.Children.Add(Button("Continuer", () => dialog.Close(true))); dialog.Content = panel;
            if (!await dialog.ShowDialog<bool>(this) || _closing) return;
            if (saved && !FlushAutoSave()) return;
            if (!saved) CancelAutoSave();
            _profileBusy = true; UpdateProfileActions();
            try {
                var tabs = _tabs.Where(t => t.Closable).ToArray(); foreach (var tab in tabs) CloseSession(tab);
                await Task.WhenAll(tabs.Select(SessionShutdown)).WaitAsync(TimeSpan.FromSeconds(8));
                CancelAutoSave(); _storeLoaded = false; _profileKey?.Dispose(); _profileKey = null;
                _connections.Clear(); _folders.Clear(); _selectedFolder = []; ForgetTreeState(); Filter(); New();
            } catch (Exception ex) { _status.Text = "Changement interrompu : " + ex.Message; return; }
            finally { _profileBusy = false; UpdateProfileActions(); }
        }
        await InitializeProfile();
    }
}
