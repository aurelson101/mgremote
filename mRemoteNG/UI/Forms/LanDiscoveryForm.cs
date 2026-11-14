using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MGRemote.Networking;
using mRemoteNG.App;

namespace mRemoteNG.UI.Forms;

internal sealed class LanDiscoveryForm : Form
{
    private readonly ComboBox _adapter = new() { Name = "DiscoveryAdapter", DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly ListView _devices = new() { Name = "DiscoveryDevices", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
    private readonly Label _status = new() { Name = "DiscoveryStatus", Dock = DockStyle.Fill, AutoEllipsis = true, Text = "IPv4 privé : au plus 256 voisins du réseau sélectionné. L’OS distant est une estimation, pas une identification certaine." };
    private CancellationTokenSource _cancellation;

    public LanDiscoveryForm()
    {
        Text = "MGRemote — Découverte réseau local"; ClientSize = new Size(1020, 530); StartPosition = FormStartPosition.CenterParent;
        DesktopAppearance.Configure(this, new Size(760, 400));
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var known = new Button { Text = "Voisins connus", AutoSize = true }; var scan = new Button { Name = "DiscoverLan", Text = "Rechercher", AutoSize = true };
        var stop = new Button { Text = "Arrêter", AutoSize = true, Enabled = false }; var copy = new Button { Text = "Copier IP", AutoSize = true };
        actions.Controls.AddRange(new Control[] { _adapter, known, scan, stop, copy });
        foreach (var (name, width) in new[] { ("Adresse IP", 140), ("Adresse MAC", 160), ("Nom d’hôte", 230), ("OS / indication", 210), ("Observation", 230) }) _devices.Columns.Add(name, width);
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_devices, 0, 1); root.Controls.Add(_status, 0, 2); Controls.Add(root);
        try { _adapter.Items.AddRange(LanDiscovery.Adapters().Cast<object>().ToArray()); }
        catch (Exception ex) { _status.Text = ex.Message; }
        if (_adapter.Items.Count > 0) _adapter.SelectedIndex = 0; else { known.Enabled = scan.Enabled = false; _status.Text = "Aucune interface Ethernet/Wi-Fi IPv4 privée active."; }
        async Task Discover(bool probe) {
            if (_cancellation != null || _adapter.SelectedItem is not LanAdapter selected) return;
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            known.Enabled = scan.Enabled = _adapter.Enabled = false; stop.Enabled = true; _devices.Items.Clear();
            try {
                var progress = new Progress<string>(text => { if (!IsDisposed) _status.Text = text; });
                var rows = await LanDiscovery.DiscoverAsync(selected, probe, progress, cancellation.Token);
                if (IsDisposed) return;
                foreach (var row in rows) _devices.Items.Add(new ListViewItem(new[] { row.Address, row.Mac, row.Hostname, row.OperatingSystem, row.Evidence }));
                _status.Text = rows.Length + " hôte(s). Les noms et TTL donnent des indices d’OS ; une adresse MAC absente peut être inconnue du cache ARP.";
            } catch (OperationCanceledException) { if (!IsDisposed) _status.Text = "Recherche arrêtée."; }
            catch (Exception ex) { if (!IsDisposed) _status.Text = ex.Message; }
            finally { _cancellation = null; if (!IsDisposed) { known.Enabled = scan.Enabled = _adapter.Enabled = true; stop.Enabled = false; } }
        }
        known.Click += async (_, _) => await Discover(false); scan.Click += async (_, _) => await Discover(true);
        stop.Click += (_, _) => _cancellation?.Cancel(); FormClosing += (_, _) => _cancellation?.Cancel();
        copy.Click += (_, _) => { if (_devices.SelectedItems.Count == 0) return; try { Clipboard.SetText(_devices.SelectedItems[0].Text); } catch (System.Runtime.InteropServices.ExternalException) { _status.Text = "Presse-papiers occupé."; } };
    }
}
