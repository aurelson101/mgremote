// MGRemote serial console. GPL-2.0; see COPYING.txt.
using System;
using System.Drawing;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using mRemoteNG.UI.Controls;
namespace mRemoteNG.UI.Forms;
internal sealed class SerialConsoleForm : Form
{
    private readonly ComboBox _port = new() { Name = "SerialPort", Width = 140 };
    private readonly NumericUpDown _baud = new() { Name = "SerialBaud", Minimum = 50, Maximum = 4000000, Value = 9600, Width = 100 };
    private readonly ComboBox _bits = Choice("8", "7", "6", "5"), _parity = Choice("None", "Even", "Odd", "Mark", "Space"), _stop = Choice("One", "Two"), _flow = Choice("None", "XOnXOff", "RequestToSend", "RequestToSendXOnXOff");
    private readonly CheckBox _dtr = new() { Text = "DTR", AutoSize = true };
    private readonly Button _connect = new() { Name = "SerialConnect", Text = "Ouvrir", AutoSize = true };
    private readonly Label _status = new() { Name = "SerialStatus", Dock = DockStyle.Bottom, Height = 44, Text = "Choisissez le câble console. Valeur courante : 9600, 8N1, sans contrôle de flux.", Padding = new Padding(8) };
    private readonly Panel _host = new() { Dock = DockStyle.Fill };
    private SshTerminal _terminal;
    private bool _opening;
    private int _generation;
    public SerialConsoleForm()
    {
        Text = "MGRemote — Console série"; Size = new Size(1000, 720); MinimumSize = new Size(740, 500); Font = SystemFonts.MessageBoxFont;
        DesktopAppearance.Configure(this, new Size(740, 500));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        void Field(string label, Control control) { bar.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(5, 8, 3, 0) }); bar.Controls.Add(control); }
        Field("Port", _port); Field("Bauds", _baud); Field("Bits", _bits); Field("Parité", _parity); Field("Stop", _stop); Field("Flux", _flow);
        var refresh = new Button { Text = "Actualiser", AutoSize = true }; refresh.Click += (_, _) => RefreshPorts();
        var disconnect = new Button { Text = "Fermer le port", AutoSize = true }; disconnect.Click += async (_, _) => await Disconnect();
        bar.Controls.Add(_dtr); bar.Controls.Add(refresh); bar.Controls.Add(_connect); bar.Controls.Add(disconnect);
        _connect.Click += async (_, _) => await Open(); Controls.Add(_host); Controls.Add(_status); Controls.Add(bar); RefreshPorts();
    }
    private static ComboBox Choice(params string[] choices) { var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 105, DropDownWidth = 230 }; box.Items.AddRange(choices); box.SelectedIndex = 0; return box; }
    private void RefreshPorts()
    {
        try { string old = _port.Text; _port.Items.Clear(); _port.Items.AddRange(SerialPort.GetPortNames().OrderBy(p => p).ToArray()); _port.Text = old.Length > 0 ? old : _port.Items.Count > 0 ? _port.Items[0].ToString() : ""; }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
    private async Task Open()
    {
        if (_opening || _terminal != null) return;
        SerialSession session = null;
        int generation = ++_generation;
        try {
            string port = _port.Text.Trim(); int baud = (int)_baud.Value, bits = int.Parse(_bits.Text);
            SerialSession.Validate(port, baud, bits);
            var parity = Enum.Parse<Parity>(_parity.Text); var stop = Enum.Parse<StopBits>(_stop.Text); var flow = Enum.Parse<Handshake>(_flow.Text); bool dtr = _dtr.Checked;
            _opening = true; _connect.Enabled = false; _status.Text = "Ouverture du port…";
            session = await Task.Run(() => SerialSession.Open(port, baud, bits, parity, stop, flow, dtr));
            if (IsDisposed || Disposing || generation != _generation) { session.Dispose(); return; }
            _terminal = new SshTerminal { Dock = DockStyle.Fill }; _host.Controls.Add(_terminal); _terminal.StartSerial(session); session = null;
            Text = "MGRemote — Série " + port; _status.Text = port + " • " + baud + " bauds • Ctrl+Maj+C / Ctrl+Maj+V. Le collage envoie des commandes au switch.";
        } catch (Exception ex) { session?.Dispose(); if (!IsDisposed) _status.Text = "Console série : " + ex.Message; }
        finally { _opening = false; if (!IsDisposed) _connect.Enabled = _terminal == null; }
    }
    private async Task Disconnect()
    {
        _generation++;
        var terminal = _terminal; _terminal = null;
        if (terminal != null) { terminal.Dispose(); await terminal.ShutdownCompletion; }
        if (!IsDisposed) { _connect.Enabled = !_opening; _status.Text = "Port série fermé."; }
    }
    protected override void Dispose(bool disposing) { if (disposing) _terminal?.Dispose(); base.Dispose(disposing); }
}
