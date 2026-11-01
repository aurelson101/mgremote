using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace MGRemote.Linux;

public sealed partial class FileTransferPane : UserControl, IDisposable
{
    private readonly Connection _connection;
    private readonly Func<Task<CredentialBroker?>>? _unlock;
    private CredentialBroker? _credentials;
    private readonly ComboBox _direction = new() { ItemsSource = new[] { "Envoyer vers le serveur", "Télécharger depuis le serveur" }, SelectedIndex = 0 };
    private readonly TextBox _local = new() { PlaceholderText = "Fichiers/dossiers locaux (un chemin par ligne), ou dossier de réception", AcceptsReturn = true, MinHeight = 42, MaxHeight = 90 }, _remote = new() { PlaceholderText = "/chemin/distant/absolu ou C:/chemin" };
    private readonly CheckBox _recursive = new() { Content = "Dossier / récursif" }, _overwrite = new() { Content = "J’autorise le remplacement des fichiers de même nom sur le serveur." };
    private readonly TextBlock _status = new() { Text = "Les clés et le profil SSH sont repris de la connexion. Les secrets importés restent chiffrés au repos.", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _start = new() { Content = "Démarrer le transfert" };
    private readonly ContentControl _progress = new();
    private readonly TextBlock _destination = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private CancellationTokenSource? _cancel;
    private EmbeddedSession? _session;
    private bool _busy, _disposed;
    private bool SessionActive => _session != null && _session.ExitCode == null && !_session.StartupFailed;
    private Task _operation = Task.CompletedTask;
    // Last remote folder and receive folder per user@host:port, so repeated transfers need no retyping.
    private static readonly Dictionary<string, (string Remote, string Local)> Recent = [];
    private string RecentKey => _connection.User + "@" + _connection.Host + ":" + _connection.Port;
    private string? _localTarget;
    private string? _receivedFolder;
    internal Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    internal FileTransferPane(Connection connection, Func<Task<CredentialBroker?>>? unlock = null)
    {
        _connection = connection; _unlock = unlock;
        Background = DesktopAppearance.Surface; Foreground = DesktopAppearance.Text;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(12), Background = DesktopAppearance.Surface };
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = "Transfert SFTP", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        form.Children.Add(new TextBlock { Text = connection.Name + " · " + connection.Host, Foreground = DesktopAppearance.MutedText, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis }); form.Children.Add(_direction);
        form.Children.Add(new TextBlock { Text = "Local — déposez ou collez des fichiers et dossiers" });
        form.Children.Add(_local); form.Children.Add(new TextBlock { Text = "Distant — dossier existant terminé par / pour un envoi multiple" }); form.Children.Add(_remote); form.Children.Add(_recursive); form.Children.Add(_overwrite);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var browse = new Button { Content = "Parcourir…", Margin = new Thickness(0, 0, 8, 0) }; browse.Click += async (_, _) => await Browse();
        var stop = new Button { Content = "Arrêter", Margin = new Thickness(8, 0, 0, 0) }; stop.Click += async (_, _) => await Cancel();
        _start.Click += async (_, _) => { _operation = Start(); await _operation; }; actions.Children.Add(browse); actions.Children.Add(_start); actions.Children.Add(stop); ToolTip.SetTip(stop, "Arrêter / fermer le transfert ; les fichiers partiels sont conservés.");
        actions.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(actions, 1); root.Children.Add(actions);
        var clipboard = new WrapPanel { Orientation = Orientation.Horizontal };
        Button Action(string title, Func<Task> action) { var button = new Button { Content = title, Margin = new Thickness(0, 0, 6, 4) }; button.Click += async (_, _) => { try { await action(); } catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; } }; return button; }
        clipboard.Children.Add(Action("Coller fichiers à envoyer", PasteFiles));
        clipboard.Children.Add(Action("Coller chemin distant", async () => {
            if (_busy || SessionActive || _disposed) return;
            string text = (await Clipboard().TryGetTextAsync())?.Trim() ?? "";
            if (text.Length is < 1 or > 4096 || text.Any(char.IsControl)) throw new IOException("Copiez un seul chemin distant absolu (4096 caractères maximum).");
            if (!_disposed && !_busy && !SessionActive) _remote.Text = text;
        }));
        clipboard.Children.Add(Action("Ouvrir le dossier local", () => { OpenLocalFolder(); return Task.CompletedTask; }));
        clipboard.Children.Add(Action("Copier le dossier reçu", CopyReceivedFolder));
        form.Children.Add(clipboard);
        form.Children.Add(new TextBlock { Text = "Téléchargement dans un nouveau sous-dossier. Les fichiers partiels sont conservés en cas d’annulation. Le terminal ci-dessous affiche la progression et les demandes d’authentification.", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        form.Children.Add(_destination);
        var formScroll = new ScrollViewer { Content = form, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        SizeChanged += (_, _) => formScroll.MaxHeight = Math.Max(150, Math.Min(420, Bounds.Height * 0.42));
        root.Children.Add(formScroll); _status.Margin = new Thickness(0, 10); Grid.SetRow(_status, 2); root.Children.Add(_status);
        Grid.SetRow(_progress, 3); root.Children.Add(_progress); Content = root;
        InstallMouseMenus();
        _direction.SelectionChanged += (_, _) => { _overwrite.IsVisible = _direction.SelectedIndex == 0; _overwrite.IsChecked = false; };
        DragDrop.SetAllowDrop(_local, true);
        _local.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = !_busy && !_disposed && !SessionActive && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        _local.AddHandler(DragDrop.DropEvent, (_, e) => {
            try { if (!_busy && !_disposed && !SessionActive) ApplyFiles(e.DataTransfer.TryGetFiles() ?? []); }
            catch (Exception ex) { _status.Text = ex.Message; }
            e.Handled = true;
        });
        if (Recent.TryGetValue(RecentKey, out var last)) { _remote.Text = last.Remote; if (last.Local.Length > 0 && Directory.Exists(last.Local)) _status.Text = "Dernier dossier distant restauré : " + last.Remote; }
        _start.Classes.Add("primary-action");
        DetachedFromVisualTree += (_, _) => Dispose();
    }
    private IClipboard Clipboard() => TopLevel.GetTopLevel(this)?.Clipboard ?? throw new IOException("Presse-papiers indisponible.");
    private void ApplyFiles(IEnumerable<IStorageItem> files)
    {
        var paths = files.Take(FileTransfer.MaxSources + 1).Select(f => f.TryGetLocalPath() ?? throw new IOException("Seuls les fichiers et dossiers locaux peuvent être envoyés.")).ToArray();
        if (paths.Length is < 1 or > FileTransfer.MaxSources || paths.Any(p => p.Any(char.IsControl))) throw new IOException("Collez entre 1 et 100 chemins locaux sans caractères de contrôle.");
        _direction.SelectedIndex = 0; _overwrite.IsChecked = false;
        _local.Text = string.Join(Environment.NewLine, paths); _recursive.IsChecked = paths.Any(Directory.Exists);
        _status.Text = paths.Length + " élément(s) prêts. Vérifiez la destination et confirmez l’envoi ; aucun transfert n’a démarré.";
    }
    private async Task PasteFiles()
    {
        if (_busy || _disposed || SessionActive) return;
        var files = await Clipboard().TryGetFilesAsync();
        if (!_disposed && !_busy && !SessionActive) ApplyFiles(files ?? []);
    }
    private void OpenLocalFolder()
    {
        string? path = _localTarget;
        if (path == null) {
            string local = FileTransfer.Sources(_local.Text ?? "")[0];
            path = Directory.Exists(local) ? local : Path.GetDirectoryName(local);
        }
        else if (!Directory.Exists(path)) path = Path.GetDirectoryName(path);
        if (path == null || !Directory.Exists(path)) throw new IOException("Choisissez d’abord un dossier local existant.");
        var start = new System.Diagnostics.ProcessStartInfo(SessionLauncher.Find("xdg-open")) { UseShellExecute = false }; start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
    }
    private async Task Browse()
    {
        if (_busy || _disposed || SessionActive) return;
        try {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new IOException("Sélecteur de fichiers indisponible.");
            if (_direction.SelectedIndex == 1 || _recursive.IsChecked == true) {
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Dossier local", AllowMultiple = false });
                if (folders.Count > 0 && !_disposed && !_busy && !SessionActive) _local.Text = folders[0].TryGetLocalPath();
            } else {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Fichiers locaux à envoyer", AllowMultiple = true });
                if (files.Count > 0 && !_disposed && !_busy && !SessionActive) ApplyFiles(files);
            }
        } catch (Exception ex) { if (!_disposed) _status.Text = ex.Message; }
    }
    private async Task Start()
    {
        if (_busy || _disposed || SessionActive) return;
        _busy = true; _start.IsEnabled = false; EnableInputs(false);
        var cancel = new CancellationTokenSource(); _cancel = cancel;
        try {
            if (_session != null) {
                var previous = _session; _session = null; previous.Dispose();
                await previous.ShutdownCompletion; _progress.Content = null;
                _credentials?.Dispose(); _credentials = null;
            }
            bool download = _direction.SelectedIndex == 1, recursive = _recursive.IsChecked == true;
            if (!download && _overwrite.IsChecked != true) throw new ArgumentException("Confirmez le remplacement éventuel des fichiers sur le serveur avant l’envoi.");
            string local = _local.Text?.Trim() ?? "", remote = _remote.Text?.Trim() ?? "";
            string[] sources = download ? [] : FileTransfer.Sources(local);
            if (!download && sources.Any(Directory.Exists)) { recursive = true; _recursive.IsChecked = true; }
            if (download) { local = SessionLauncher.IdentityPath(local); _ = FileTransfer.Arguments(_connection, local, remote, true, recursive); }
            else _ = FileTransfer.UploadArguments(_connection, sources, remote, recursive);
            _status.Text = "Vérification du client et des fichiers locaux…";
            var plan = await Task.Run(() => download ? FileTransfer.Prepare(_connection, local, remote, true, recursive, cancel.Token) : FileTransfer.PrepareUpload(_connection, sources, remote, recursive, cancel.Token), cancel.Token);
            cancel.Token.ThrowIfCancellationRequested(); if (_disposed) return;
            _credentials = _unlock == null ? null : await _unlock();
            if (_disposed || cancel.IsCancellationRequested) { _credentials?.Dispose(); _credentials = null; return; }
            _session = new EmbeddedSession(parent => { var start = SessionLauncher.Terminal(parent, plan.Executable, plan.Arguments); _credentials?.Configure(start, "SSH"); return start; }, message => { if (!_disposed) _status.Text = message; });
            var current = _session;
            current.Completed += code => {
                if (_disposed || !ReferenceEquals(current, _session)) return;
                _credentials?.Dispose(); _credentials = null;
                _start.IsEnabled = true; EnableInputs(true);
                _status.Text = code == 0 ? "Transfert terminé avec succès (code 0)." : "Échec du transfert (code " + code + "). Les fichiers reçus peuvent être partiels ; consultez le journal avant de réessayer.";
            };
            current.Failed += message => {
                if (_disposed || !ReferenceEquals(current, _session)) return;
                _credentials?.Dispose(); _credentials = null;
                _start.IsEnabled = true; EnableInputs(true); _status.Text = "Transfert non démarré : " + message;
            };
            if (remote.Length > 0) Recent[RecentKey] = (remote, download ? plan.LocalTarget : "");
            _localTarget = download ? plan.LocalTarget : Path.GetDirectoryName(sources[0]);
            if (download) _receivedFolder = plan.LocalTarget;
            _destination.Text = download ? "Dossier de réception : " + plan.LocalTarget : sources.Length + " élément(s) locaux à envoyer.";
            _progress.Content = _session; _status.Text = download ? "Téléchargement lancé dans " + plan.LocalTarget + ". Le terminal affiche la progression." : "Envoi de " + sources.Length + " élément(s). Le terminal affiche la progression ; Annuler / fermer arrête la session.";
        } catch (OperationCanceledException) { if (!_disposed) _status.Text = "Préparation annulée ; aucun transfert démarré."; }
        catch (Exception ex) { if (!_disposed) _status.Text = "Transfert : " + ex.Message; }
        finally { _busy = false; if (ReferenceEquals(_cancel, cancel)) _cancel = null; cancel.Dispose(); if (!_disposed) { _start.IsEnabled = !SessionActive; EnableInputs(!SessionActive); } }
    }
    private async Task Cancel()
    {
        bool interrupted = _busy || SessionActive;
        _credentials?.Dispose(); _credentials = null;
        _cancel?.Cancel(); var session = _session; _session = null; _progress.Content = null;
        if (session != null) { session.Dispose(); ShutdownCompletion = session.ShutdownCompletion; await ShutdownCompletion; }
        if (!_disposed) { _start.IsEnabled = !_busy; EnableInputs(!_busy); _status.Text = interrupted ? "Transfert annulé. Les fichiers partiels sont conservés ; aucun succès n’est annoncé." : "Journal de transfert fermé."; }
    }
    private void EnableInputs(bool enabled)
    {
        _local.IsEnabled = _remote.IsEnabled = _direction.IsEnabled = _recursive.IsEnabled = _overwrite.IsEnabled = enabled;
    }
    internal async Task VerifyValidation()
    {
        _direction.SelectedIndex = 1; await Start();
        if (_session != null || _status.Text?.Contains("chemin", StringComparison.OrdinalIgnoreCase) != true) throw new Exception("Transfer UI did not reject an empty path before any operation.");
        string directory = Path.Combine(Path.GetTempPath(), "mgremote-transfer-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CredentialBroker?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = new FileTransferPane(new Connection { Name = "Cancellation fixture", Host = "fixture.example.invalid" }, () => { entered.TrySetResult(); return release.Task; });
        try {
            pending._direction.SelectedIndex = 1; pending._local.Text = directory; pending._remote.Text = "/fixture";
            pending._operation = pending.Start();
            await Task.WhenAny(entered.Task, pending._operation).WaitAsync(TimeSpan.FromSeconds(7));
            if (!entered.Task.IsCompletedSuccessfully) throw new Exception("Local transfer preparation failed: " + pending._status.Text);
            pending.Dispose();
            if (pending.ShutdownCompletion.IsCompleted) throw new Exception("Transfer shutdown ignored pending authentication.");
            release.TrySetResult(null); await pending.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            if (pending._session != null || pending._credentials != null) throw new Exception("Canceled transfer created a client or retained credentials.");
            Console.WriteLine("PASS: SFTP disposal waits for pending authentication and cancels before client creation; local preparation only.");
        } finally {
            release.TrySetResult(null); pending.Dispose(); await pending.ShutdownCompletion.WaitAsync(TimeSpan.FromSeconds(3));
            Directory.Delete(directory, recursive: true);
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel?.Cancel(); _session?.Dispose(); _credentials?.Dispose();
        ShutdownCompletion = Task.WhenAll(_operation, _session?.ShutdownCompletion ?? ShutdownCompletion);
    }
}
