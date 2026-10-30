using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed class FileTransferPane : UserControl, IDisposable
{
    private readonly Connection _connection;
    private readonly Func<Task<CredentialBroker?>>? _unlock;
    private CredentialBroker? _credentials;
    private readonly ComboBox _direction = new() { ItemsSource = new[] { "Envoyer vers le serveur", "Télécharger depuis le serveur" }, SelectedIndex = 0 };
    private readonly TextBox _local = new() { PlaceholderText = "Fichier/dossier local, ou dossier de réception" }, _remote = new() { PlaceholderText = "/chemin/distant/absolu ou C:/chemin" };
    private readonly CheckBox _recursive = new() { Content = "Dossier / récursif" }, _overwrite = new() { Content = "J’autorise le remplacement des fichiers de même nom sur le serveur." };
    private readonly TextBlock _status = new() { Text = "Les clés et le profil SSH sont repris de la connexion. Les secrets importés restent chiffrés au repos.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Démarrer le transfert" };
    private readonly ContentControl _progress = new();
    private readonly TextBlock _destination = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private CancellationTokenSource? _cancel;
    private EmbeddedSession? _session;
    private bool _busy, _disposed;
    internal FileTransferPane(Connection connection, Func<Task<CredentialBroker?>>? unlock = null)
    {
        _connection = connection; _unlock = unlock;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), Margin = new Thickness(12) };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = "Transfert SFTP — " + connection.Name, FontSize = 18 }); form.Children.Add(_direction);
        form.Children.Add(_local); form.Children.Add(_remote); form.Children.Add(_recursive); form.Children.Add(_overwrite);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var browse = new Button { Content = "Parcourir…", Margin = new Thickness(0, 0, 8, 0) }; browse.Click += async (_, _) => await Browse();
        var stop = new Button { Content = "Annuler / fermer le transfert", Margin = new Thickness(8, 0, 0, 0) }; stop.Click += async (_, _) => await Cancel();
        _start.Click += async (_, _) => await Start(); actions.Children.Add(browse); actions.Children.Add(_start); actions.Children.Add(stop); form.Children.Add(actions);
        form.Children.Add(new TextBlock { Text = "Téléchargement dans un nouveau sous-dossier. Les fichiers partiels sont conservés en cas d’annulation. Le terminal ci-dessous affiche la progression et les demandes d’authentification.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        form.Children.Add(_destination); root.Children.Add(new ScrollViewer { Content = form, MaxHeight = 320 }); _status.Margin = new Thickness(0, 10); Grid.SetRow(_status, 1); root.Children.Add(_status);
        Grid.SetRow(_progress, 2); root.Children.Add(_progress); Content = root;
        _direction.SelectionChanged += (_, _) => { _overwrite.IsVisible = _direction.SelectedIndex == 0; _overwrite.IsChecked = false; };
        DetachedFromVisualTree += (_, _) => Dispose();
    }
    private async Task Browse()
    {
        try {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new IOException("Sélecteur de fichiers indisponible.");
            if (_direction.SelectedIndex == 1 || _recursive.IsChecked == true) {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dossier local", AllowMultiple = false });
                if (folders.Count > 0) _local.Text = folders[0].TryGetLocalPath();
            } else {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Fichier local à envoyer", AllowMultiple = false });
                if (files.Count > 0) _local.Text = files[0].TryGetLocalPath();
            }
        } catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
    }
    private async Task Start()
    {
        if (_busy || _disposed || _session != null) return;
        _busy = true; _start.IsEnabled = false;
        var cancel = new CancellationTokenSource(); _cancel = cancel;
        try {
            bool download = _direction.SelectedIndex == 1, recursive = _recursive.IsChecked == true;
            if (!download && _overwrite.IsChecked != true) throw new ArgumentException("Confirmez le remplacement éventuel des fichiers sur le serveur avant l’envoi.");
            string local = _local.Text?.Trim() ?? "", remote = _remote.Text?.Trim() ?? "";
            _ = FileTransfer.Arguments(_connection, local, remote, download, recursive);
            _status.Text = "Vérification du client et des fichiers locaux…";
            var plan = await Task.Run(() => FileTransfer.Prepare(_connection, local, remote, download, recursive, cancel.Token), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested(); if (_disposed) return;
            _credentials = _unlock == null ? null : await _unlock();
            if (_disposed || cancel.IsCancellationRequested) { _credentials?.Dispose(); _credentials = null; return; }
            _session = new EmbeddedSession(parent => { var start = SessionLauncher.Terminal(parent, plan.Executable, plan.Arguments); _credentials?.Configure(start, "SSH"); return start; }, message => { if (!_disposed) _status.Text = message; });
            _destination.Text = download ? "Dossier de réception : " + plan.LocalTarget : "Source locale : " + plan.LocalTarget;
            _progress.Content = _session; _status.Text = "Transfert lancé. Destination locale : " + plan.LocalTarget + ". Fermez/annulez pour interrompre la session.";
        } catch (OperationCanceledException) { if (!_disposed) _status.Text = "Préparation annulée ; aucun transfert démarré."; }
        catch (Exception ex) { if (!_disposed) _status.Text = "Transfert : " + ex.Message; }
        finally { _busy = false; if (ReferenceEquals(_cancel, cancel)) _cancel = null; cancel.Dispose(); if (!_disposed) _start.IsEnabled = _session == null; }
    }
    private async Task Cancel()
    {
        _credentials?.Dispose(); _credentials = null;
        _cancel?.Cancel(); var session = _session; _session = null; _progress.Content = null;
        if (session != null) { session.Dispose(); await session.ShutdownCompletion; }
        if (!_disposed) { _start.IsEnabled = !_busy; _status.Text = "Session de transfert fermée. Les fichiers partiels sont conservés."; }
    }
    internal async Task VerifyValidation()
    {
        _direction.SelectedIndex = 1; await Start();
        if (_session != null || _status.Text?.Contains("chemin local") != true) throw new Exception("Transfer UI did not reject an empty path before any operation.");
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel?.Cancel(); _session?.Dispose(); _credentials?.Dispose();
    }
}
