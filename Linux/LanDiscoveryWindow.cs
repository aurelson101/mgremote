using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MGRemote.Networking;

namespace MGRemote.Linux;

internal sealed class LanDiscoveryWindow : Window
{
    private CancellationTokenSource? _cancellation;
    public LanDiscoveryWindow()
    {
        Title = "MGRemote — Découverte réseau local"; Width = 1000; Height = 540; MinWidth = 720; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var adapter = new ComboBox { MinWidth = 330 };
        var known = new Button { Content = "Voisins connus" }; var scan = new Button { Content = "Rechercher" }; var stop = new Button { Content = "Arrêter", IsEnabled = false };
        var status = new TextBlock { Text = "IPv4 privé, au plus 256 adresses. Les indications d’OS distant restent des estimations.", TextWrapping = TextWrapping.Wrap };
        var rows = new StackPanel { Spacing = 3 }; var table = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12), RowSpacing = 10 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 }; actions.Children.Add(adapter); actions.Children.Add(known); actions.Children.Add(scan); actions.Children.Add(stop);
        root.Children.Add(actions); Grid.SetRow(table, 1); root.Children.Add(table); Grid.SetRow(status, 2); root.Children.Add(status); Content = root;
        try { var adapters = LanDiscovery.Adapters(); adapter.ItemsSource = adapters; adapter.SelectedIndex = adapters.Length > 0 ? 0 : -1; if (adapters.Length == 0) { known.IsEnabled = scan.IsEnabled = false; status.Text = "Aucune interface Ethernet/Wi-Fi IPv4 privée active."; } }
        catch (Exception ex) { status.Text = ex.Message; known.IsEnabled = scan.IsEnabled = false; }
        async Task Discover(bool probe) {
            if (_cancellation != null || adapter.SelectedItem is not LanAdapter selected) return;
            using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
            adapter.IsEnabled = known.IsEnabled = scan.IsEnabled = false; stop.IsEnabled = true; rows.Children.Clear();
            try {
                var progress = new Progress<string>(text => { if (!cancellation.IsCancellationRequested) status.Text = text; });
                var devices = await LanDiscovery.DiscoverAsync(selected, probe, progress, cancellation.Token);
                foreach (var device in devices) {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("145,165,230,230,240"), Margin = new Thickness(4) };
                    string[] values = { device.Address, device.Mac, device.Hostname, device.OperatingSystem, device.Evidence };
                    for (int i = 0; i < values.Length; i++) { var text = new TextBlock { Text = values[i], TextTrimming = TextTrimming.CharacterEllipsis }; ToolTip.SetTip(text, values[i]); Grid.SetColumn(text, i); row.Children.Add(text); }
                    rows.Children.Add(row);
                }
                status.Text = devices.Length + " hôte(s). Colonnes : IP, MAC, nom, OS indicatif, observation. Une MAC absente est inconnue du cache ARP.";
            } catch (OperationCanceledException) { status.Text = "Recherche arrêtée."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { _cancellation = null; adapter.IsEnabled = known.IsEnabled = scan.IsEnabled = true; stop.IsEnabled = false; }
        }
        known.Click += async (_, _) => await Discover(false); scan.Click += async (_, _) => await Discover(true);
        stop.Click += (_, _) => _cancellation?.Cancel(); Closing += (_, _) => _cancellation?.Cancel();
    }
}
