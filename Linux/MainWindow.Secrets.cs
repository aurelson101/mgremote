using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MGRemote.Linux;
public sealed partial class MainWindow
{
    private sealed record ImportKeys(string? Source, string? Local);
    private sealed record AuthenticationChoice(CredentialBroker? Broker);
    private async Task<CredentialBroker?> UnlockConnection(Connection connection)
    {
        if (connection.EncryptedPassword.Length == 0 || connection.Protocol is not ("SSH" or "RDP")) return null;
        var dialog = new Window { Title = "Authentification — " + connection.Name, Width = 520, Height = 350, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        bool supported = connection.Protocol != "RDP" || (connection.User.Length > 0 && connection.GatewayHost.Length == 0);
        panel.Children.Add(new TextBlock { Text = supported
            ? "Déverrouillez le mot de passe importé pour cette connexion uniquement. SSH exige une clé d’hôte déjà vérifiée ; pour une première connexion, un bastion ou une phrase de clé privée, utilisez le mode manuel."
            : "Cette connexion nécessite l’authentification manuelle : renseignez l’utilisateur RDP ; les identifiants de passerelle ne sont pas encore automatisés.", TextWrapping = TextWrapping.Wrap });
        var key = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète locale", IsEnabled = supported };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var unlock = new Button { Content = "Utiliser le secret importé", IsEnabled = supported };
        bool closed = false;
        unlock.Click += async (_, _) => {
            unlock.IsEnabled = false; string phrase = key.Text ?? ""; key.Text = "";
            try {
                string password = await Task.Run(() => XmlSecrets.Unprotect(connection.EncryptedPassword, phrase));
                if (!closed) dialog.Close(new AuthenticationChoice(new CredentialBroker(connection, password)));
            } catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or NotSupportedException or IOException or System.Net.Sockets.SocketException) {
                if (!closed) status.Text = "Déverrouillage impossible. Vérifiez la phrase secrète ou choisissez le mode manuel.";
            } finally { if (!closed) unlock.IsEnabled = true; }
        };
        panel.Children.Add(key); panel.Children.Add(status); panel.Children.Add(unlock);
        panel.Children.Add(Button("Connexion manuelle", () => dialog.Close(new AuthenticationChoice(null))));
        panel.Children.Add(Button("Annuler", () => dialog.Close()));
        dialog.Content = new ScrollViewer { Content = panel }; dialog.Closed += (_, _) => { closed = true; key.Text = ""; };
        var choice = await dialog.ShowDialog<AuthenticationChoice?>(this);
        return choice == null ? throw new OperationCanceledException() : choice.Broker;
    }
    private async Task<ImportKeys?> AskImportKeys()
    {
        var dialog = new Window { Title = "Importer un XML mRemoteNG", Width = 520, Height = 520, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(18) };
        var source = new TextBox { PasswordChar = '●', PlaceholderText = "Mot de passe du fichier XML (vide : clé mRemoteNG par défaut)" };
        var keep = new CheckBox { Content = "Importer aussi les mots de passe des connexions" };
        var local = new TextBox { PasswordChar = '●', PlaceholderText = "Nouvelle phrase secrète locale (12 caractères minimum)", IsEnabled = false };
        var confirmation = new TextBox { PasswordChar = '●', PlaceholderText = "Confirmer la phrase secrète locale", IsEnabled = false };
        keep.IsCheckedChanged += (_, _) => local.IsEnabled = confirmation.IsEnabled = keep.IsChecked == true;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Le mot de passe XML sert uniquement au déchiffrement de cet import. Les secrets conservés seront chiffrés à nouveau avec votre phrase locale. Aucune phrase secrète n’est enregistrée.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(source); panel.Children.Add(keep); panel.Children.Add(local); panel.Children.Add(confirmation);
        panel.Children.Add(new TextBlock { Text = "Sans la phrase locale, les secrets seront irrécupérables. Leur déverrouillage reste manuel ; ils ne sont pas transmis automatiquement aux clients SSH/RDP.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Button("Annuler", () => dialog.Close()));
        actions.Children.Add(Button("Importer", () => {
            string? sourceKey = string.IsNullOrEmpty(source.Text) ? null : source.Text;
            string? localKey = keep.IsChecked == true ? local.Text ?? "" : null;
            if (localKey != null && (localKey.Length < 12 || localKey.Length > 1024 || string.IsNullOrWhiteSpace(localKey) || localKey != confirmation.Text || localKey == sourceKey || localKey == "mR3m")) {
                error.Text = "Confirmez une phrase locale distincte du mot de passe XML, de 12 à 1024 caractères."; return;
            }
            dialog.Close(new ImportKeys(sourceKey, localKey));
        }));
        panel.Children.Add(actions); dialog.Content = new ScrollViewer { Content = panel };
        dialog.Closed += (_, _) => source.Text = local.Text = confirmation.Text = "";
        return await dialog.ShowDialog<ImportKeys?>(this);
    }
    private async Task RevealImportedSecret()
    {
        string envelope = _encryptedPassword;
        if (envelope.Length == 0) { _status.Text = "Cette connexion ne contient pas de mot de passe importé."; return; }
        var dialog = new Window { Title = "Secret importé — " + (_name.Text ?? ""), Width = 500, Height = 330, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(18) };
        var key = new TextBox { PasswordChar = '●', PlaceholderText = "Phrase secrète locale choisie à l’import" };
        var value = new TextBox { PasswordChar = '●', IsReadOnly = true };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(new TextBlock { Text = "Déverrouillage temporaire. Aucun envoi automatique ni copie automatique au presse-papiers.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(key); panel.Children.Add(value); panel.Children.Add(status);
        bool closed = false;
        var unlock = new Button { Content = "Déverrouiller" };
        unlock.Click += async (_, _) => {
            unlock.IsEnabled = false;
            string password = key.Text ?? ""; key.Text = "";
            try { string secret = await Task.Run(() => XmlSecrets.Unprotect(envelope, password)); if (!closed) { value.Text = secret; status.Text = "Déverrouillé jusqu’à la fermeture de cette fenêtre."; } }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException) { value.Text = ""; status.Text = "Phrase secrète incorrecte ou secret altéré."; }
            finally { unlock.IsEnabled = true; }
        };
        panel.Children.Add(unlock);
        panel.Children.Add(Button("Afficher / masquer", () => value.PasswordChar = value.PasswordChar == '\0' ? '●' : '\0'));
        panel.Children.Add(Button("Fermer", () => dialog.Close()));
        dialog.Content = new ScrollViewer { Content = panel }; dialog.Closed += (_, _) => { closed = true; key.Text = value.Text = ""; };
        await dialog.ShowDialog(this);
    }
}
