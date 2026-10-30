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
    private async Task InitializeProfile()
    {
        if (App.Smoke || _closing) return;
        _storeLoaded = false;
        ProfileKey? candidate = null;
        try {
            bool encrypted = ProfileEncryption.IsEncryptedFile(_dataPath);
            if (encrypted) {
                candidate = await AskProfileUnlock();
                if (candidate == null) { _status.Text = "Profil verrouillé — ouvrez Fichier → Chiffrer / déverrouiller le profil pour réessayer. Aucune écriture autorisée."; return; }
            }
            var store = Connections.LoadStore(_dataPath, candidate);
            if (_closing) return;
            _profileKey?.Dispose(); _profileKey = candidate; candidate = null;
            _connections.Clear(); foreach (var item in store.Items) _connections.Add(item);
            _folders.Clear(); _folders.AddRange(store.Folders); _storeLoaded = true;
            Filter(); New();
            _status.Text = encrypted ? "Profil chiffré déverrouillé pour cette session." : "Profil chargé — chiffrement facultatif disponible dans Fichier.";
            if (!encrypted) await OfferProfileEncryption();
        } catch (Exception ex) { _status.Text = "Chargement du profil : " + ex.Message + " — Écriture bloquée pour préserver vos données."; }
        finally { candidate?.Dispose(); }
    }
    private async Task ManageProfile()
    {
        if (!_storeLoaded) { await InitializeProfile(); return; }
        if (_profileKey != null) { _status.Text = "Le profil complet est déjà chiffré. Sa phrase secrète sera demandée au prochain démarrage."; return; }
        await OfferProfileEncryption();
    }
    private async Task<ProfileKey?> AskProfileUnlock(Action<Window>? opened = null)
    {
        if (new FileInfo(_dataPath).Length > ProfileEncryption.MaxFile) throw new IOException("Profil trop volumineux.");
        using var document = JsonDocument.Parse(File.ReadAllText(_dataPath));
        JsonElement envelope = document.RootElement.Clone();
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
                Connections.Save(_connections, _folders, _dataPath, candidate);
                _profileKey = candidate; candidate = null;
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
        var previousItems = _connections.ToArray(); var previousFolders = _folders.ToArray(); bool previousLoaded = _storeLoaded;
        void Click(Window dialog, string content) => dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content?.ToString() == content).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        const string phrase = "synthetic GUI profile phrase";
        try {
            var fixture = new Connection { Name = "Profile UI fixture", Host = "profile.example" };
            _connections.Clear(); _connections.Add(fixture); _folders.Clear(); _storeLoaded = true;
            Connections.Save([fixture], dataPath: _dataPath); string plain = File.ReadAllText(_dataPath);
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
            Console.WriteLine("PASS: optional startup profile dialog skip/encrypt, close during key derivation preserves plaintext, canceled unlock write block and successful profile unlock; temporary profile only.");
        } finally {
            _profileKey?.Dispose(); _profileKey = null; _storeLoaded = previousLoaded;
            _connections.Clear(); foreach (var item in previousItems) _connections.Add(item);
            _folders.Clear(); _folders.AddRange(previousFolders); Filter(); New(); CleanupSmokeStore();
        }
    }
}
