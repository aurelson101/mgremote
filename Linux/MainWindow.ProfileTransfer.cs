using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private readonly MenuItem _exportProfileItem = new() { Header = "Exporter le profil chiffré…", IsEnabled = false };
    private readonly MenuItem _importProfileItem = new() { Header = "Importer un profil chiffré…", IsEnabled = false };
    private static readonly FilePickerFileType PortableProfileType = new("Profil MGRemote chiffré") { Patterns = ["*.mgremote"] };

    private async Task ExportPortableProfile()
    {
        if (_profileBusy || !_storeLoaded || _closing) return;
        _profileBusy = true; UpdateProfileActions();
        try {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Exporter le profil chiffré", SuggestedFileName = "MGRemote-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mgremote",
                DefaultExtension = "mgremote", FileTypeChoices = [PortableProfileType], ShowOverwritePrompt = true
            });
            if (file == null || _closing) return;
            string path = file.TryGetLocalPath() ?? throw new IOException("Choisissez un fichier local.");
            string revision = await Task.Run(() => ProfileTransfer.DestinationRevision(path, _dataPath));
            if (!_closing) await ExportPortableDialog(path, revision);
        } catch (Exception ex) { if (!_closing) _status.Text = "Export impossible : " + ex.Message; }
        finally { _profileBusy = false; UpdateProfileActions(); }
    }
    private async Task ExportPortableDialog(string path, string revision, Action<Window>? opened = null)
    {
        var dialog = new Window { Title = "Exporter le profil chiffré", Width = 570, Height = 480, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Une copie chiffrée des connexions enregistrées et des dossiers sera créée. Sur l’autre PC Linux équipé de MGRemote : Fichier → Importer un profil chiffré, puis saisissez cette phrase secrète. Enregistrez la fiche en cours avant l’export si vous l’avez modifiée.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète de l’export (12 caractères minimum)" };
        var confirmation = new TextBox { PasswordChar = '●', PlaceholderText = "Confirmer la phrase secrète" };
        panel.Children.Add(password); panel.Children.Add(confirmation);
        panel.Children.Add(new TextBlock { Text = "Les fichiers de clés privées SSH ne sont pas inclus. Les secrets de connexion déjà importés gardent leur phrase locale d’origine. Transmettez la phrase de l’export séparément du fichier et conservez-la : aucune récupération n’est possible.", TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var export = new Button { Content = "Exporter" }; var cancel = Button("Annuler", () => dialog.Close());
        bool closed = false; Task? pending = null;
        async Task WriteExport()
        {
            string phrase = password.Text ?? "";
            if (phrase.Length is < 12 or > 1024 || string.IsNullOrWhiteSpace(phrase) || phrase != confirmation.Text) { error.Text = "Confirmez une phrase secrète de 12 à 1024 caractères."; return; }
            export.IsEnabled = false; password.Text = confirmation.Text = "";
            try {
                var snapshot = new Connections.Store(1, _connections.ToArray(), _folders.ToArray());
                string encrypted = await Task.Run(() => ProfileTransfer.Encrypt(snapshot, phrase));
                if (closed || _closing) return;
                ProfileTransfer.Write(encrypted, path, _dataPath, revision);
                _status.Text = "Profil chiffré exporté : " + path + " — à importer sur l’autre ordinateur depuis le menu Fichier.";
                dialog.Close();
            } catch (Exception ex) { if (!closed) error.Text = "Export impossible : " + ex.Message; }
            finally { if (!closed) export.IsEnabled = true; }
        }
        export.Click += (_, _) => pending = WriteExport();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(18, 0, 18, 18) }; actions.Children.Add(cancel); actions.Children.Add(export);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = DesktopAppearance.Surface }; layout.Children.Add(new ScrollViewer { Content = panel }); Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Opened += (_, _) => { password.Focus(); opened?.Invoke(dialog); };
        dialog.Closed += (_, _) => { closed = true; password.Text = confirmation.Text = ""; };
        await dialog.ShowDialog(this); if (pending != null) await pending;
    }
    private async Task ImportPortableProfile()
    {
        if (_profileBusy || !_storeLoaded || _closing) return;
        _profileBusy = true; UpdateProfileActions();
        try {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Importer un profil MGRemote chiffré", AllowMultiple = false, FileTypeFilter = [PortableProfileType, FilePickerFileTypes.All] });
            if (files.Count == 0 || _closing) return;
            string path = files[0].TryGetLocalPath() ?? throw new IOException("Choisissez un fichier local.");
            await ImportPortableDialog(path);
        } catch (Exception ex) { if (!_closing) _status.Text = "Import impossible : " + ex.Message; }
        finally { _profileBusy = false; UpdateProfileActions(); }
    }
    private async Task ImportPortableDialog(string path, Action<Window>? opened = null)
    {
        var dialog = new Window { Title = "Importer un profil chiffré", Width = 570, Height = 420, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "Les connexions et dossiers seront ajoutés au profil actuel. Les éléments strictement identiques ne seront pas dupliqués ; les connexions existantes et les sessions ouvertes sont conservées. Les modifications non enregistrées de la fiche en cours seront ignorées.", TextWrapping = TextWrapping.Wrap });
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète du fichier exporté" }; panel.Children.Add(password);
        var protect = new CheckBox { Content = "Chiffrer le profil local avec cette phrase", IsChecked = true, IsVisible = _profileKey == null }; panel.Children.Add(protect);
        panel.Children.Add(new TextBlock { Text = "Le chiffrement actuel est conservé s’il est déjà actif. Vérifiez les chemins locaux des clés SSH, dossiers partagés et ports série sur ce PC ; les clés privées et les alias ~/.ssh/config ne sont pas transférés. Les secrets de connexion gardent leur phrase locale d’origine.", TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var import = new Button { Content = "Importer" }; var cancel = Button("Annuler", () => dialog.Close());
        bool closed = false; Task? pending = null;
        async Task ReadImport()
        {
            string phrase = password.Text ?? ""; password.Text = ""; import.IsEnabled = false; protect.IsEnabled = false;
            bool encryptLocal = _profileKey == null && protect.IsChecked == true;
            ProfileKey? candidate = null;
            try {
                var incoming = await Task.Run(() => ProfileTransfer.Read(path, phrase));
                if (closed || _closing) return;
                if (encryptLocal) candidate = await Task.Run(() => ProfileEncryption.CreateKey(phrase));
                if (closed || _closing) return;
                var merged = ProfileTransfer.Merge(new Connections.Store(1, _connections.ToArray(), _folders.ToArray()), incoming);
                int added = merged.Items.Length - _connections.Count;
                _profileRevision = Connections.Save(merged.Items, merged.Folders, _dataPath, _profileKey ?? candidate, _profileRevision);
                if (candidate != null) { _profileKey = candidate; candidate = null; }
                _connections.Clear(); foreach (var item in merged.Items) _connections.Add(item);
                _folders.Clear(); _folders.AddRange(merged.Folders); _selectedFolder = []; _search.Text = ""; ForgetTreeState(); Filter(); New(); UpdateProfileActions();
                _status.Text = added + " connexion(s) ajoutée(s). Profil local " + (_profileKey == null ? "non chiffré" : "chiffré") + " ; vérifiez les chemins propres à ce PC.";
                dialog.Close();
            } catch (System.Security.Cryptography.CryptographicException) { if (!closed) error.Text = "Phrase secrète incorrecte ou fichier altéré. Le profil actuel est conservé."; }
            catch (Exception ex) { if (!closed) error.Text = "Import impossible : " + ex.Message; }
            finally { candidate?.Dispose(); if (!closed) import.IsEnabled = protect.IsEnabled = true; }
        }
        import.Click += (_, _) => pending = ReadImport();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(18, 0, 18, 18) }; actions.Children.Add(cancel); actions.Children.Add(import);
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Background = DesktopAppearance.Surface }; layout.Children.Add(new ScrollViewer { Content = panel }); Grid.SetRow(actions, 1); layout.Children.Add(actions); dialog.Content = layout;
        dialog.Opened += (_, _) => { password.Focus(); opened?.Invoke(dialog); };
        dialog.Closed += (_, _) => { closed = true; password.Text = ""; };
        await dialog.ShowDialog(this); if (pending != null) await pending;
    }
}
