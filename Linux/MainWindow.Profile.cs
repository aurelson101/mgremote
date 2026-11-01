using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using System.Text.Json;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private ProfileKey? _profileKey;
    private string _profileRevision = "missing";
    private bool _profileBusy;
    private Control? _profileEditor;
    private readonly MenuItem _lockProfileItem = new() { Header = "Verrouiller le profil…", IsEnabled = false };
    private void UpdateProfileActions()
    {
        _lockProfileItem.IsEnabled = _storeLoaded && _profileKey != null && !_profileBusy;
        _exportProfileItem.IsEnabled = _importProfileItem.IsEnabled = _storeLoaded && !_profileBusy;
        if (_profileEditor != null) _profileEditor.IsEnabled = _storeLoaded && !_profileBusy;
        _list.IsEnabled = _storeLoaded && !_profileBusy;
        _sftp.IsEnabled = _storeLoaded && !_profileBusy && _protocol.SelectedIndex == 0;
    }
    private void RequireLoadedProfile()
    {
        if (!_storeLoaded || _profileBusy) throw new IOException("Déverrouillez ou chargez le profil, puis attendez la fin de l’opération en cours avant d’ouvrir une connexion.");
    }
    private async Task InitializeProfile()
    {
        if (App.Smoke || _closing || _profileBusy) return;
        _profileBusy = true; _storeLoaded = false; UpdateProfileActions();
        if (_dataPath == _defaultProfilePath) _dataPath = ProfileRecovery.LastSelected(_defaultProfilePath);
        string message = "";
        try {
            while (!_closing) {
                string? selected = await ChooseStartupProfile(_dataPath, message);
                if (selected == null || _closing) { _status.Text = "Aucun profil ouvert — Fichier → Changer / gérer les profils."; return; }
                _dataPath = selected;
                ProfileKey? candidate = null;
                try {
                    bool encrypted = await Task.Run(() => ProfileEncryption.IsEncryptedFile(_dataPath));
                    if (_closing) return;
                    if (encrypted) {
                        candidate = await AskProfileUnlock();
                        if (candidate == null) { message = "Profil verrouillé. Réessayez ou choisissez un autre profil ; le fichier est conservé."; continue; }
                    }
                    var store = await Task.Run(() => Connections.LoadStore(_dataPath, candidate));
                    if (_closing) return;
                    _profileKey?.Dispose(); _profileKey = candidate; candidate = null;
                    _profileRevision = store.Revision;
                    _connections.Clear(); foreach (var item in store.Items) _connections.Add(item);
                    _folders.Clear(); _folders.AddRange(store.Folders); _storeLoaded = true;
                    CancelAutoSave(); Filter(); New(); UpdateProfileActions();
                    _status.Text = "Profil chargé : " + _dataPath + " — modifications enregistrées automatiquement.";
                    try { ProfileRecovery.Remember(_defaultProfilePath, _dataPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text += " Dernier choix non mémorisé : " + ex.Message; }
                    if (!encrypted) await OfferProfileEncryption();
                    return;
                } catch (Exception ex) { message = "Chargement impossible : " + ex.Message + " Le fichier est conservé ; choisissez un autre profil ou une sauvegarde."; }
                finally { candidate?.Dispose(); }
            }
        } finally { _profileBusy = false; UpdateProfileActions(); }
    }
    private async Task ManageProfile()
    {
        if (_profileBusy || _closing || !FlushAutoSave()) return;
        if (!_storeLoaded) { await InitializeProfile(); return; }
        if (_profileKey != null) { _status.Text = "Le profil complet est déjà chiffré. Sa phrase secrète sera demandée au prochain démarrage."; return; }
        _profileBusy = true;
        try { await OfferProfileEncryption(); }
        finally { _profileBusy = false; UpdateProfileActions(); }
    }
    private async Task LockProfile(Action<Window>? opened = null)
    {
        if (_profileBusy || _profileKey == null || !_storeLoaded || _closing || !FlushAutoSave()) return;
        var dialog = new Window { Title = "Verrouiller le profil", Width = 490, Height = 220, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Les sessions et transferts ouverts seront arrêtés, et les modifications non enregistrées seront ignorées. Votre phrase secrète sera nécessaire pour retrouver les connexions.", TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Button("Annuler", () => dialog.Close(false))); actions.Children.Add(Button("Verrouiller", () => dialog.Close(true)));
        panel.Children.Add(actions); dialog.Content = panel; dialog.Opened += (_, _) => opened?.Invoke(dialog);
        if (!await dialog.ShowDialog<bool>(this) || _closing) return;
        _profileBusy = true;
        var sessions = _tabs.Where(t => t.Closable).ToArray();
        foreach (var tab in sessions) CloseSession(tab);
        _profileKey.Dispose(); _profileKey = null; _storeLoaded = false;
        _connections.Clear(); _folders.Clear(); _selectedFolder = []; _search.Text = ""; ForgetTreeState();
        Filter(); New(); UpdateProfileActions();
        _configurationTitle.Text = "Configuration · Profil verrouillé";
        _status.Text = "Profil verrouillé — arrêt des processus gérés…";
        try {
            await Task.WhenAll(sessions.Select(SessionShutdown)).WaitAsync(TimeSpan.FromSeconds(6));
            _status.Text = "Profil verrouillé — Fichier → Chiffrer / déverrouiller le profil pour le rouvrir.";
        } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) {
            _status.Text = "Profil verrouillé, mais l’arrêt d’un client n’est pas confirmé. Vérifiez les fenêtres et processus restants.";
        } finally { _profileBusy = false; UpdateProfileActions(); }
    }
    private async Task<ProfileKey?> AskProfileUnlock(Action<Window>? opened = null)
    {
        JsonElement envelope = await Task.Run(() => {
            using var document = JsonDocument.Parse(ProfileEncryption.ReadProfileText(_dataPath));
            return document.RootElement.Clone();
        });
        if (_closing) return null;
        var dialog = new Window { Title = "Déverrouiller le profil MGRemote", Width = 510, Height = 320, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Ce profil est chiffré. Saisissez sa phrase secrète pour charger les connexions, les dossiers et les réglages. Annuler laisse le profil verrouillé et interdit toute écriture.", TextWrapping = TextWrapping.Wrap });
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète du profil" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var unlock = new Button { Content = "Déverrouiller" };
        bool closed = false;
        unlock.Click += async (_, _) => {
            unlock.IsEnabled = false; string phrase = password.Text ?? ""; password.Text = "";
            ProfileKey? candidate = null;
            try {
                candidate = await Task.Run(() => ProfileEncryption.Unlock(envelope, phrase));
                if (!closed) { dialog.Close(candidate); candidate = null; }
            } catch (Exception) { if (!closed) error.Text = "Phrase secrète incorrecte, profil altéré ou format non pris en charge. Le fichier est conservé."; }
            finally { candidate?.Dispose(); if (!closed) unlock.IsEnabled = true; }
        };
        panel.Children.Add(password); panel.Children.Add(error); panel.Children.Add(unlock);
        panel.Children.Add(Button("Annuler", () => dialog.Close())); dialog.Content = panel;
        dialog.Opened += (_, _) => { password.Focus(); opened?.Invoke(dialog); };
        dialog.Closed += (_, _) => { closed = true; password.Text = ""; };
        return await dialog.ShowDialog<ProfileKey?>(this);
    }
    private async Task OfferProfileEncryption(Action<Window>? opened = null)
    {
        if (!_storeLoaded || _profileKey != null || _closing) return;
        var dialog = new Window { Title = "Protéger votre profil MGRemote (facultatif)", Width = 550, Height = 460, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "Chiffrer le profil complet ? Les serveurs, utilisateurs, dossiers et réglages seront protégés sur disque par AES-256-GCM. Votre phrase secrète sera demandée à chaque démarrage. Vous pouvez continuer sans chiffrement et l’activer plus tard dans Fichier.", TextWrapping = TextWrapping.Wrap });
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète (12 caractères minimum)" };
        var confirmation = new TextBox { PasswordChar = '●', PlaceholderText = "Confirmer la phrase secrète" };
        panel.Children.Add(password); panel.Children.Add(confirmation);
        panel.Children.Add(new TextBlock { Text = "Conservez cette phrase en lieu sûr : aucune récupération n’est possible en cas d’oubli. Les éventuels mots de passe importés conservent aussi leur phrase locale distincte.", TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var encrypt = new Button { Content = "Chiffrer le profil" };
        var later = Button("Plus tard — continuer sans chiffrement", () => dialog.Close());
        bool closed = false;
        Task? pendingEncryption = null;
        async Task EncryptProfile()
        {
            string phrase = password.Text ?? "";
            if (phrase.Length is < 12 or > 1024 || string.IsNullOrWhiteSpace(phrase) || phrase != confirmation.Text) { error.Text = "Confirmez une phrase secrète de 12 à 1024 caractères."; return; }
            encrypt.IsEnabled = later.IsEnabled = false; password.Text = confirmation.Text = "";
            ProfileKey? candidate = null;
            try {
                candidate = await Task.Run(() => ProfileEncryption.CreateKey(phrase));
                if (closed || _closing) return;
                _profileRevision = Connections.Save(_connections, _folders, _dataPath, candidate, _profileRevision);
                _profileKey = candidate; candidate = null;
                UpdateProfileActions();
                _status.Text = "Profil complet chiffré — déverrouillage requis au prochain démarrage.";
                dialog.Close();
            } catch (Exception ex) { if (!closed) error.Text = "Chiffrement impossible : " + ex.Message; }
            finally { candidate?.Dispose(); if (!closed) encrypt.IsEnabled = later.IsEnabled = true; }
        }
        encrypt.Click += (_, _) => pendingEncryption = EncryptProfile();
        panel.Children.Add(encrypt); panel.Children.Add(later);
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.Opened += (_, _) => { password.Focus(); opened?.Invoke(dialog); };
        dialog.Closed += (_, _) => { closed = true; password.Text = confirmation.Text = ""; };
        await dialog.ShowDialog(this);
        if (pendingEncryption != null) await pendingEncryption;
    }
    internal async Task VerifyProfileUi()
    {
        if (!App.Smoke) throw new InvalidOperationException("Synthetic profile UI checks require --smoke-ui.");
        var previousItems = _connections.ToArray(); var previousFolders = _folders.ToArray(); bool previousLoaded = _storeLoaded; string previousRevision = _profileRevision;
        void Click(Window dialog, string content) => dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content?.ToString() == content).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        const string phrase = "synthetic GUI profile phrase";
        try {
            var fixture = new Connection { Name = "Profile UI fixture", Host = "profile.example" };
            _connections.Clear(); _connections.Add(fixture); _folders.Clear(); _storeLoaded = true;
            Connections.Save([fixture], dataPath: _dataPath); string plain = File.ReadAllText(_dataPath);
            _profileRevision = Connections.LoadStore(_dataPath).Revision;
            await OfferProfileEncryption(dialog => Click(dialog, "Plus tard — continuer sans chiffrement"));
            if (_profileKey != null || File.ReadAllText(_dataPath) != plain) throw new Exception("Skipping profile encryption changed the profile.");
            await OfferProfileEncryption(dialog => {
                foreach (var input in dialog.GetLogicalDescendants().OfType<TextBox>()) input.Text = phrase;
                Click(dialog, "Chiffrer le profil");
                dialog.Close();
            });
            if (_profileKey != null || File.ReadAllText(_dataPath) != plain) throw new Exception("Closing profile encryption during key derivation changed the profile or retained its key.");
            await OfferProfileEncryption(dialog => {
                foreach (var input in dialog.GetLogicalDescendants().OfType<TextBox>()) input.Text = phrase;
                Click(dialog, "Chiffrer le profil");
            });
            if (_profileKey == null || !ProfileEncryption.IsEncryptedFile(_dataPath) || Connections.LoadStore(_dataPath, _profileKey).Items.Single().Host != fixture.Host) throw new Exception("Profile encryption dialog did not protect the complete profile.");
            _profileKey.Dispose(); _profileKey = null; _storeLoaded = false;
            string encrypted = File.ReadAllText(_dataPath);
            using var canceled = await AskProfileUnlock(dialog => Click(dialog, "Annuler"));
            if (canceled != null || File.ReadAllText(_dataPath) != encrypted) throw new Exception("Canceling profile unlock modified the profile.");
            bool blocked = false; try { Persist([]); } catch (IOException) { blocked = true; }
            if (!blocked) throw new Exception("Locked profile UI allowed writes.");
            using var unlocked = await AskProfileUnlock(dialog => {
                dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text = phrase;
                Click(dialog, "Déverrouiller");
            });
            if (unlocked == null || Connections.LoadStore(_dataPath, unlocked).Items.Single().Host != fixture.Host) throw new Exception("Profile unlock dialog lost the profile.");
            _profileKey = unlocked; _storeLoaded = true; UpdateProfileActions();
            await LockProfile(dialog => Click(dialog, "Annuler"));
            if (!_storeLoaded || _profileKey == null) throw new Exception("Canceling profile lock changed the active profile.");
            var start = new System.Diagnostics.ProcessStartInfo(SessionLauncher.Find("sleep")) { UseShellExecute = false }; start.ArgumentList.Add("60");
            var owned = new ExternalSession(start); AddSession(new SessionTab("Lock fixture", owned));
            await LockProfile(dialog => Click(dialog, "Verrouiller"));
            if (!owned.ShutdownCompletion.IsCompletedSuccessfully) throw new Exception("Profile lock returned before owned process shutdown completed.");
            bool alive; try { using var process = System.Diagnostics.Process.GetProcessById(owned.ProcessId); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
            if (alive || _profileKey != null || _storeLoaded || _connections.Count != 0 || _folders.Count != 0 || _tabs.Any(t => t.Closable) || Read().Host.Length > 0 || _profileEditor?.IsEnabled != false) throw new Exception("Profile lock retained client, key or editable connection data.");
            blocked = false; try { Persist([]); } catch (IOException) { blocked = true; }
            if (!blocked || File.ReadAllText(_dataPath) != encrypted) throw new Exception("Manual profile lock modified the saved profile or permitted writes.");
            int tabs = _tabs.Count; Launch(false); External();
            if (_tabs.Count != tabs || _status.Text?.Contains("Déverrouillez") != true) throw new Exception("Locked profile can open a client.");
            Console.WriteLine("PASS: manual profile lock cancellation, owned-process shutdown, in-memory key/editor cleanup and locked launch/write guards.");
            Console.WriteLine("PASS: optional startup profile dialog skip/encrypt, close during key derivation preserves plaintext, canceled unlock write block and successful profile unlock; temporary profile only.");
        } finally {
            _profileKey?.Dispose(); _profileKey = null; _storeLoaded = previousLoaded; _profileRevision = previousRevision;
            _connections.Clear(); foreach (var item in previousItems) _connections.Add(item);
            _folders.Clear(); _folders.AddRange(previousFolders); Filter(); New(); UpdateProfileActions(); CleanupSmokeStore();
        }
    }
}
