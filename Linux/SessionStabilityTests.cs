using Avalonia;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace MGRemote.Linux;

public sealed partial class MainWindow
{
    private static async Task Until(Func<bool> condition, string message, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
        if (!condition()) throw new Exception(message);
    }
    internal async Task VerifySessionStability()
    {
        foreach (int expected in new[] { 0, 1 }) {
            int completed = 0, retries = 0;
            using var native = new EmbeddedSession(parent => SessionLauncher.Terminal(parent, expected == 0 ? "/usr/bin/true" : "/usr/bin/false", []), _ => { });
            native.Completed += _ => completed++;
            var tools = SessionWorkspace(new Connection { Host = "fixture.example.invalid" }, native, new TextBlock(), () => { retries++; return Task.CompletedTask; });
            var tab = new SessionTab("Exit fixture", tools, native);
            try {
                AddSession(tab);
                await Until(() => native.ExitCode != null, "Held terminal hid the client result.");
                if (native.ExitCode != expected || completed != 1 || !native.ProcessRunning) throw new Exception("Client exit result was incorrect or its journal was lost.");
                var retry = tools.GetLogicalDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "Reconnecter");
                if (!retry.IsEnabled) throw new Exception("Reconnect action remained disabled after client exit.");
                retry.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                if (retries != 1) throw new Exception("Reconnect action did not run.");
            } finally { CloseSession(tab); await native.ShutdownCompletion; }
        }
        using (var external = new ExternalSession(new ProcessStartInfo("/usr/bin/false") { UseShellExecute = false })) {
            await Until(() => external.ExitCode != null, "External client completion was not observed.");
            if (external.ExitCode != 1) throw new Exception("External client failure was reported as success.");
        }
        Console.WriteLine("PASS: held-terminal success/failure codes, single completion event, retained journal, reconnect action and external exit status.");
        string? fixturePath = Environment.GetEnvironmentVariable("MGREMOTE_LIVE_FIXTURE");
        if (fixturePath == null) return;
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath)); var data = fixture.RootElement;
        string root = data.GetProperty("root").GetString()!;
        foreach (string marker in new[] { "drop-ssh", "ssh-restarted", "drop-rdp", "rdp-restarted" }) File.Delete(Path.Combine(root, marker));
        var connection = new Connection { Name = "Loopback SSH", Host = "127.0.0.1", Port = data.GetProperty("sshPort").GetInt32(), User = data.GetProperty("user").GetString()!, Identity = Path.Combine(root, "client") };
        using (var transfer = new FileTransferPane(connection)) {
            var transferTab = new SessionTab("Live SFTP fixture", transfer);
            try { AddSession(transferTab); await transfer.VerifyLiveTransfers(root); }
            finally { CloseSession(transferTab); await transfer.ShutdownCompletion; }
        }
        File.Delete(Path.Combine(root, "ssh-authenticated"));
        int initial = _tabs.Count;
        await OpenConnection(connection, false);
        var live = _tabs.Last();
        try {
            await Until(() => live.Host?.ChildWindows > 0, "Loopback SSH did not embed.");
            await Until(() => File.Exists(Path.Combine(root, "ssh-authenticated")), "OpenSSH fixture did not authenticate.");
            File.WriteAllText(Path.Combine(root, "drop-ssh"), "");
            await Until(() => live.Host?.ExitCode != null, "SSH server interruption was not observed.");
            if (live.Host!.ExitCode == 0) throw new Exception("SSH interruption was announced as success.");
            await Until(() => File.Exists(Path.Combine(root, "ssh-restarted")), "SSH fixture did not restart.");
            File.Delete(Path.Combine(root, "ssh-authenticated"));
            await ReconnectSession(live);
            var replacement = _tabs.Last();
            if (ReferenceEquals(replacement, live) || _tabs.Count != initial + 1 || _tabs.Contains(live)) throw new Exception("Reconnect duplicated tabs or retained the previous session.");
            await Until(() => replacement.Host?.ChildWindows > 0 && replacement.Host.ProcessRunning, "SSH reconnection did not start a new managed client.");
            await Until(() => File.Exists(Path.Combine(root, "ssh-authenticated")), "Reconnected SSH client did not authenticate.");
            CloseSession(replacement); await SessionShutdown(replacement);
        } finally { CloseSession(live); await SessionShutdown(live); }
        Console.WriteLine("PASS: real loopback OpenSSH authentication, server interruption, explicit reconnect, tab replacement and owned-client cleanup.");
        await VerifyExternalRecovery(connection, root);
        if (data.TryGetProperty("rdpFingerprint", out _)) await VerifyLiveRdp(data);
    }
    private async Task VerifyExternalRecovery(Connection connection, string root)
    {
        foreach (string marker in new[] { "drop-ssh", "ssh-restarted", "ssh-authenticated" }) File.Delete(Path.Combine(root, marker));
        await OpenConnection(connection, false, externalWindow: true); var first = _tabs.Last();
        try {
            if (first.Body is not ExternalSession external) throw new Exception("External SSH session was not created.");
            await Until(() => File.Exists(Path.Combine(root, "ssh-authenticated")), "External SSH did not authenticate.");
            File.WriteAllText(Path.Combine(root, "drop-ssh"), "");
            await Until(() => external.ExitCode != null, "External SSH interruption was not detected.");
            if (external.ExitCode == 0) throw new Exception("External SSH interruption was reported as success.");
            await Until(() => File.Exists(Path.Combine(root, "ssh-restarted")), "External SSH fixture did not restart.");
            File.Delete(Path.Combine(root, "ssh-authenticated"));
            await ReconnectSession(first); var second = _tabs.Last();
            try {
                if (ReferenceEquals(first, second) || second.Body is not ExternalSession || _tabs.Contains(first)) throw new Exception("External reconnect did not replace its tab.");
                await Until(() => File.Exists(Path.Combine(root, "ssh-authenticated")), "External SSH reconnection did not authenticate.");
                bool alive; try { using var old = Process.GetProcessById(external.ProcessId); alive = !old.HasExited; } catch (ArgumentException) { alive = false; }
                if (alive) throw new Exception("Previous external terminal survived reconnection.");
            } finally { CloseSession(second); await SessionShutdown(second); }
        } finally { CloseSession(first); await SessionShutdown(first); }
        Console.WriteLine("PASS: real external SSH server interruption/reconnect, fresh authentication, old terminal shutdown and tab replacement.");
    }
    private async Task VerifyLiveRdp(JsonElement data)
    {
        string root = data.GetProperty("root").GetString()!;
        var connection = new Connection { Name = "Loopback RDP", Protocol = "RDP", Host = "127.0.0.1", Port = data.GetProperty("rdpPort").GetInt32(), User = data.GetProperty("rdpUser").GetString()!, Domain = data.GetProperty("rdpDomain").GetString()!, Clipboard = true };
        async Task<SessionTab> Connect()
        {
            var broker = new CredentialBroker(connection, data.GetProperty("rdpPassword").GetString()!);
            var native = new EmbeddedSession(parent => {
                var start = SessionLauncher.Embedded(connection, false, parent);
                start.ArgumentList.Add("/cert:fingerprint:sha256:" + data.GetProperty("rdpFingerprint").GetString());
                broker.Configure(start, "RDP"); return start;
            }, _ => { });
            var tab = new SessionTab("Live RDP fixture", SessionWorkspace(connection, native, new TextBlock()), native, Credentials: broker);
            AddSession(tab);
            try {
                await Until(() => broker.Completion.IsCompleted, "RDP broker did not deliver the fixture credential.");
                await Until(() => native.ChildWindows > 0 && native.ProcessRunning, "RDP native client did not embed.");
                string pixel = "none"; bool received = false; var deadline = DateTime.UtcNow.AddSeconds(15);
                while (!received && DateTime.UtcNow < deadline) {
                    var point = native.PointToScreen(new Avalonia.Point(Math.Max(1, native.Bounds.Width / 2), Math.Max(1, native.Bounds.Height / 2)));
                    pixel = (await RunDesktopTool("python3", Path.Combine(root, "sample-rdp.py"), point.X.ToString(), point.Y.ToString())).Trim();
                    received = int.TryParse(pixel, System.Globalization.NumberStyles.HexNumber, null, out int rgb) &&
                        Math.Abs(((rgb >> 16) & 255) - 0x32) <= 8 && Math.Abs(((rgb >> 8) & 255) - 0x65) <= 8 && Math.Abs((rgb & 255) - 0x8a) <= 8;
                    if (!received) await Task.Delay(200);
                }
                if (!received || native.ExitCode != null) throw new Exception("Authenticated RDP did not display the synthetic server desktop; pixel=" + pixel + ", exit=" + native.ExitCode);
                return tab;
            } catch { CloseSession(tab); await SessionShutdown(tab); throw; }
        }
        var first = await Connect();
        try {
            File.WriteAllText(Path.Combine(root, "drop-rdp"), "");
            await Until(() => first.Host!.ExitCode != null, "RDP server interruption was not detected.");
            if (first.Host!.ExitCode == 0) throw new Exception("RDP network interruption was announced as success.");
            await Until(() => File.Exists(Path.Combine(root, "rdp-restarted")), "RDP fixture did not restart.");
        } finally { CloseSession(first); await SessionShutdown(first); }
        var second = await Connect(); CloseSession(second); await SessionShutdown(second);
        Console.WriteLine("PASS: real loopback RDP NLA, pinned TLS certificate, broker authentication, rendered desktop pixels, server interruption and fresh connection after restart; FreeRDP shadow server, not Windows.");
    }
}

public sealed partial class FileTransferPane
{
    internal async Task VerifyLiveTransfers(string root)
    {
        string source = Path.Combine(root, "source.txt"); File.WriteAllText(source, "MGRemote live SFTP fixture\n");
        async Task WaitForResult()
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (_session?.ExitCode == null && DateTime.UtcNow < deadline) await Task.Delay(100);
            if (_session?.ExitCode == null) throw new Exception("SFTP client did not report completion.");
        }
        _local.Text = source; _remote.Text = Path.Combine(root, "remote", "uploaded.txt"); _overwrite.IsChecked = true;
        await Start(); await WaitForResult();
        if (_session!.ExitCode != 0 || _status.Text?.Contains("succès") != true || File.ReadAllText(_remote.Text!) != File.ReadAllText(source) || !_start.IsEnabled) throw new Exception("Real SFTP upload did not report verified success.");
        _local.Text = root; _remote.Text = Path.Combine(root, "remote", "uploaded.txt"); _direction.SelectedIndex = 1;
        await Start(); await WaitForResult();
        if (_session!.ExitCode != 0 || _receivedFolder == null || File.ReadAllText(Path.Combine(_receivedFolder, "uploaded.txt")) != File.ReadAllText(source)) throw new Exception("Real SFTP download differs from its source.");
        string tree = Path.Combine(root, "fixture folder"), binary = Path.Combine(tree, "nested", "bytes.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!); File.WriteAllBytes(binary, Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray());
        _direction.SelectedIndex = 0; _local.Text = tree; _remote.Text = Path.Combine(root, "remote") + "/"; _recursive.IsChecked = true; _overwrite.IsChecked = true;
        await Start(); await WaitForResult();
        if (_session!.ExitCode != 0 || !File.ReadAllBytes(Path.Combine(root, "remote", "fixture folder", "nested", "bytes.bin")).SequenceEqual(File.ReadAllBytes(binary))) throw new Exception("Recursive SFTP upload changed the binary content.");
        _direction.SelectedIndex = 1; _local.Text = root; _remote.Text = Path.Combine(root, "remote", "fixture folder");
        await Start(); await WaitForResult();
        if (_session!.ExitCode != 0 || !File.ReadAllBytes(Path.Combine(_receivedFolder!, "fixture folder", "nested", "bytes.bin")).SequenceEqual(File.ReadAllBytes(binary))) throw new Exception("Recursive SFTP download changed the binary content.");
        _recursive.IsChecked = false;
        _direction.SelectedIndex = 0; _local.Text = source; _remote.Text = "/proc/mgremote-fixture-denied/output.txt"; _overwrite.IsChecked = true;
        await Start(); await WaitForResult();
        if (_session!.ExitCode == 0 || _status.Text?.Contains("Échec") != true || !_start.IsEnabled) throw new Exception("Failed SFTP transfer was reported as success or cannot be retried.");
        _local.Text = source; _remote.Text = Path.Combine(root, "remote", "canceled.txt"); _overwrite.IsChecked = true;
        await Start(); await Cancel();
        if (_session != null || _status.Text?.Contains("annulé") != true) throw new Exception("SFTP cancellation retained a client or announced success.");
        Console.WriteLine("PASS: real loopback SFTP file/folder upload/download and binary content verification, failure, retry without closing the tab and explicit cancellation.");
    }
}
