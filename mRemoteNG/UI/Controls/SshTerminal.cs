// MGRemote modification, 2026-10-30. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace mRemoteNG.UI.Controls;

internal sealed class SshTerminal : UserControl
{
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(24, 26, 31) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 25, Text = "Initialisation du terminal SSH…", ForeColor = Color.Silver, BackColor = Color.FromArgb(24, 26, 31) };
    private readonly PseudoTerminal _terminal = new();
    private SerialSession _serial;
    private readonly ContextMenuStrip _clipboardMenu = new();
    private string _copySelection;
    private TaskCompletionSource _ack;
    private string _executable, _arguments;
    private volatile bool _stopping;
    private bool _ready;
    public Task Ready { get; private set; } = Task.CompletedTask;
    public Task ShutdownCompletion => _serial?.ShutdownCompletion ?? _terminal.ShutdownCompletion;
    public bool IsProcessRunning => !_stopping && (_serial?.IsRunning ?? _terminal.IsRunning);
    public int ProcessId => _terminal.ProcessId;
    public SshTerminal()
    {
        Controls.Add(_view); Controls.Add(_status);
        _clipboardMenu.Items.Add("Copier", null, (_, _) => {
            try { if (!string.IsNullOrEmpty(_copySelection)) Clipboard.SetText(_copySelection); }
            catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Presse-papiers indisponible."; }
        });
        _clipboardMenu.Items.Add("Coller", null, (_, _) => PasteClipboard());
    }
    public void StartProcess(string executable, string arguments)
    {
        _executable = executable; _arguments = arguments;
        Ready = InitializeAsync();
    }
    internal void StartSerial(SerialSession session) { _serial = session; _status.Text = "Initialisation du terminal série…"; Ready = InitializeAsync(); }
    private async Task InitializeAsync()
    {
        try {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MGRemote", "TerminalWebView");
            var environment = await CoreWebView2Environment.CreateAsync(null, cache);
            if (_stopping) return;
            await _view.EnsureCoreWebView2Async(environment);
            if (_stopping) return;
            var core = _view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false; core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            bool initialDocument = true;
            core.NavigationStarting += (_, e) => { e.Cancel = !initialDocument; initialDocument = false; };
            core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && !_stopping) _status.Text = "Chargement du terminal : " + e.WebErrorStatus; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.ProcessFailed += (_, _) => { if (!_stopping) { _status.Text = "Le moteur du terminal s’est arrêté. Reconnectez la session."; StopProcess(); } };
            core.WebResourceRequested += (_, e) => e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            core.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
            core.WebMessageReceived += OnMessage;
            string nonce = Guid.NewGuid().ToString("N");
            string html = Asset("terminal.html").Replace("NONCE", nonce).Replace("/*XTERM*/", Asset("xterm.js"))
                .Replace("/*FIT*/", Asset("addon-fit.js")).Replace("/*CSS*/", Asset("xterm.css"));
            core.NavigateToString(html);
        } catch (Exception ex) {
            if (!_stopping) _status.Text = "Terminal indisponible : " + ex.Message;
        }
    }
    private static string Asset(string name)
    {
        var assembly = typeof(SshTerminal).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".TerminalAssets." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_stopping || e.Source != "about:blank") return;
        try {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString()) {
                case "ready":
                    if (_ready) return;
                    _ready = true;
                    if (_serial != null) _serial.Start(DisplayAsync); else _terminal.Start(_executable, _arguments, DisplayAsync);
                    _status.Text = (_serial != null ? "Série" : "SSH") + " • Ctrl+Maj+C : copier • Ctrl+Maj+V : coller • clic droit : menu";
                    FocusInput(); break;
                case "ack": _ack?.TrySetResult(); break;
                case "input":
                    string input = root.GetProperty("text").GetString() ?? "";
                    if (!IsProcessRunning || !(_serial?.Send(input) ?? _terminal.Send(input))) _status.Text = "Saisie trop rapide ou session fermée : réessayez.";
                    break;
                case "resize": if (_serial == null) _terminal.Resize(root.GetProperty("cols").GetInt32(), root.GetProperty("rows").GetInt32()); break;
                case "copy":
                    string text = root.GetProperty("text").GetString();
                    if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                    break;
                case "paste": PasteClipboard(); break;
                case "menu":
                    _copySelection = root.GetProperty("text").GetString();
                    _clipboardMenu.Show(Cursor.Position); break;
            }
        } catch (Exception ex) when (ex is InvalidOperationException or JsonException or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception) {
            _status.Text = "Terminal : " + ex.Message;
        }
    }
    private Task DisplayAsync(string text)
    {
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ack = ack;
        if (_stopping) return Task.CompletedTask;
        try { BeginInvoke(new Action(() => {
            if (_stopping) { ack.TrySetResult(); return; }
            try { _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", text })); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { ack.TrySetResult(); }
        })); } catch (InvalidOperationException) { ack.TrySetResult(); }
        return ack.Task;
    }
    internal async Task PasteTextAsync(string text)
    {
        if (_stopping || !_ready || string.IsNullOrEmpty(text)) return;
        if (text.Length > (_serial != null ? 4096 : 65536)) { _status.Text = _serial != null ? "Collage série limité à 4 Ko." : "Collage limité à 64 Ko. Utilisez SFTP pour les fichiers."; return; }
        if (text.Contains('\n') || text.Contains('\r') || _serial != null)
            if (MessageBox.Show(this, "Le collage peut envoyer et exécuter des commandes sur la destination. Coller ?", "Collage terminal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await _view.ExecuteScriptAsync("window.mgPaste(" + JsonSerializer.Serialize(text) + ")");
    }
    private async void PasteClipboard()
    {
        try {
            if (Clipboard.ContainsFileDropList()) { _status.Text = "Fichiers et dossiers : utilisez Transfert SSH (SFTP)."; return; }
            if (Clipboard.ContainsText()) await PasteTextAsync(Clipboard.GetText());
        } catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ObjectDisposedException or InvalidOperationException) {
            if (!_stopping) _status.Text = "Presse-papiers indisponible.";
        }
    }
    public void FocusInput() { if (!_stopping) _view.Focus(); }
    public void StopProcess() { _stopping = true; _ack?.TrySetResult(); _serial?.Dispose(); _terminal.Dispose(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopProcess(); _clipboardMenu.Dispose(); _copySelection = null; }
        base.Dispose(disposing);
    }
}
