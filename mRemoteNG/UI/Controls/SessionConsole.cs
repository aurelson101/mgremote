// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace mRemoteNG.UI.Controls;

// A bounded text console, not a VT/ConPTY emulator. Native-console SSH is provided
// separately for interactive authentication and full terminal applications.
internal sealed class SessionConsole : UserControl
{
    private readonly RichTextBox _output = new() { Name = "TerminalOutput", Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = false, ShortcutsEnabled = true };
    private readonly TextBox _input = new() { Name = "TerminalInput", Dock = DockStyle.Bottom, PlaceholderText = "Commande — Entrée pour envoyer, Maj+Entrée pour une nouvelle ligne", Multiline = true, Height = 62, ScrollBars = ScrollBars.Vertical };
    private readonly ContextMenuStrip _clipboardMenu = new();
    private readonly Font _terminalFont = new(FontFamily.GenericMonospace, 10);
    private readonly System.Windows.Forms.Timer _displayTimer = new() { Interval = 33 };
    private readonly CancellationTokenSource _readerStop = new();
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(64) {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private Process _process;
    private Task _readers = Task.CompletedTask;
    private Task _exit = Task.CompletedTask;
    private Task _shutdown = Task.CompletedTask;
    private volatile bool _stopping;
    private volatile bool _exited;
    private bool _inputAllowed = true;
    public int ProcessId { get; private set; }
    public Task ShutdownCompletion => _shutdown;
    public bool IsProcessRunning => !_stopping && !_exited && _process != null && !_process.HasExited;
    public bool IsInputEnabled { get => _inputAllowed; set { _inputAllowed = value; _input.Enabled = value && IsProcessRunning; } }

    public SessionConsole()
    {
        _output.Font = _input.Font = _terminalFont;
        Controls.Add(_output); Controls.Add(_input);
        _clipboardMenu.Items.Add("Copier le texte sélectionné", null, (_, _) => CopySelection());
        _clipboardMenu.Items.Add("Coller dans la saisie (sans exécuter)", null, (_, _) => PasteClipboard());
        _output.ContextMenuStrip = _input.ContextMenuStrip = _clipboardMenu;
        _displayTimer.Tick += (_, _) => {
            if (_stopping) return;
            var batch = new StringBuilder();
            for (int i = 0; i < 32 && _pending.Reader.TryRead(out string text); i++) batch.Append(text);
            if (batch.Length > 0) {
                int selectedStart = _output.SelectionStart, selectedLength = _output.SelectionLength, removed = 0;
                // Limit both queued output and visible history during long/noisy sessions.
                if (_output.TextLength + batch.Length > 200000) {
                    removed = Math.Min(_output.TextLength, _output.TextLength + batch.Length - 160000);
                    _output.Select(0, removed);
                    _output.SelectedText = string.Empty;
                }
                _output.AppendText(batch.ToString());
                if (selectedLength > 0) {
                    int start = Math.Max(0, selectedStart - removed);
                    int end = Math.Max(0, selectedStart + selectedLength - removed);
                    _output.Select(start, Math.Max(0, end - start));
                } else _output.SelectionStart = _output.TextLength;
                _output.ScrollToCaret();
            }
            _input.Enabled = _inputAllowed && !_exited;
        };
        _input.KeyDown += async (_, e) => {
            if (e.KeyCode != Keys.Enter || e.Shift) return;
            e.SuppressKeyPress = true;
            if (!IsProcessRunning) return;
            string command = _input.Text;
            _input.Clear(); // No input echo or command/password history is retained.
            _input.ClearUndo();
            try { await _process.StandardInput.WriteLineAsync(command); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) {
                if (!_stopping) _pending.Writer.TryWrite("\r\nL’entrée du terminal est fermée.\r\n");
            }
        };
    }

    public void FocusInput() { if (_input.Enabled) _input.Focus(); else _output.Focus(); }
    internal string SelectedTerminalText => _input.Focused && _input.SelectionLength > 0 ? _input.SelectedText : _output.SelectedText;
    internal void PasteText(string text) { if (_stopping) return; _input.SelectedText = text ?? string.Empty; FocusInput(); }
    private void CopySelection()
    {
        if (string.IsNullOrEmpty(SelectedTerminalText)) return;
        try { Clipboard.SetText(SelectedTerminalText, TextDataFormat.UnicodeText); }
        catch (System.Runtime.InteropServices.ExternalException) { _pending.Writer.TryWrite("\r\nLe presse-papiers est occupé. Réessayez.\r\n"); }
    }
    private void PasteClipboard()
    {
        try {
            if (Clipboard.ContainsFileDropList()) { _pending.Writer.TryWrite("\r\nPour les fichiers/dossiers, utilisez Transfert SSH (SFTP) dans le menu de la connexion.\r\n"); return; }
            if (Clipboard.ContainsText()) PasteText(Clipboard.GetText(TextDataFormat.UnicodeText));
        } catch (System.Runtime.InteropServices.ExternalException) { _pending.Writer.TryWrite("\r\nLe presse-papiers est occupé. Réessayez.\r\n"); }
    }
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == (Keys.Control | Keys.Shift | Keys.C)) { CopySelection(); return true; }
        if (keys == (Keys.Control | Keys.Shift | Keys.V) || keys == (Keys.Control | Keys.V)) { PasteClipboard(); return true; }
        return base.ProcessCmdKey(ref message, keys);
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        if (_output != null) _output.BackColor = _input.BackColor = BackColor;
    }
    protected override void OnForeColorChanged(EventArgs e)
    {
        base.OnForeColorChanged(e);
        if (_output != null) _output.ForeColor = _input.ForeColor = ForeColor;
    }

    public void StartProcess(string executable, string arguments)
    {
        if (_process != null || _stopping) throw new InvalidOperationException("This console already owns a session.");
        _process = new Process { StartInfo = new ProcessStartInfo(executable, arguments) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        if (!_process.Start()) throw new InvalidOperationException("Cannot start the terminal process.");
        ProcessId = _process.Id;
        _readers = Task.WhenAll(ReadOutputAsync(_process.StandardOutput), ReadOutputAsync(_process.StandardError));
        _exit = ObserveExitAsync(_process);
        _input.Enabled = _inputAllowed;
        _displayTimer.Start();
    }

    private async Task ReadOutputAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        try {
            while (!_stopping) {
                int length = await reader.ReadAsync(buffer.AsMemory(), _readerStop.Token).ConfigureAwait(false);
                if (length == 0) break;
                if (!_stopping) _pending.Writer.TryWrite(new string(buffer, 0, length));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task ObserveExitAsync(Process process)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        finally { _exited = true; }
    }

    public void StopProcess()
    {
        if (_stopping) return;
        _stopping = true;
        _displayTimer.Stop(); _input.Enabled = false;
        _readerStop.Cancel(); _pending.Writer.TryComplete();
        try {
            if (_process != null && !_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { /* Process already exited or did not start. */ }
        finally { _shutdown = FinishShutdownAsync(); }
    }

    private async Task FinishShutdownAsync()
    {
        try { await Task.WhenAll(_exit, _readers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { _process?.Dispose(); _readerStop.Dispose(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { StopProcess(); _displayTimer.Dispose(); _clipboardMenu.Dispose(); }
        base.Dispose(disposing);
        if (disposing) _terminalFont.Dispose();
    }
}
