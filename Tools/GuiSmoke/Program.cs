// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using mRemoteNG.PluginContracts;

public sealed class ProbePlugin : IPlugin, IDisposable
{
    public static int Initializations, Thread, Disposals;
    public string Id => "mgremote.gui-smoke";
    public string DisplayName => "GUI smoke probe";
    public Version Version => new(1, 0);
    public Version MinimumHostVersion => new(0, 0);
    public void Initialize(IPluginContext context) { Initializations++; Thread = Environment.CurrentManagedThreadId; }
    public void Dispose() { Disposals++; }
}

internal static class Program
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Assembly app;
    private static object adder;
    private static MethodInfo add;
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDispose(int index)
    {
        var panel = (Form)add.Invoke(adder, new object[] { "Memory check " + index, true });
        Check(panel != null, "Panel creation failed.");
        var reference = new WeakReference(panel);
        panel.Dispose();
        return reference;
    }
    private static void Collect() { Application.DoEvents(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Application.DoEvents(); }
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("tunnel-fixture", StringComparison.Ordinal)) {
            if (args.Length > 2) File.WriteAllText(args[2], Environment.ProcessId.ToString());
            if (args[0] == "tunnel-fixture-no-listen") { Thread.Sleep(Timeout.Infinite); return; }
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, int.Parse(args[1]));
            listener.Start();
            while (true) {
                using var client = listener.AcceptTcpClient();
                using var stream = client.GetStream();
                var buffer = new byte[1024];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) stream.Write(buffer, 0, count);
            }
        }
        string payload = Path.GetFullPath(args[0]);
        string artifacts = Path.GetFullPath(args[1]);
        string fixture = Path.Combine(artifacts, "gui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        AssemblyLoadContext.Default.Resolving += (context, name) => {
            foreach (string folder in new[] { payload, Path.Combine(payload, "Assemblies") }) {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Control.CheckForIllegalCrossThreadCalls = true;
        app = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(payload, "MGRemote.dll"));
        if (args.Length > 2 && args[2] == "startup-metrics") {
            var loggerType = app.GetType("mRemoteNG.App.Initialization.StartupDataLogger", true);
            var collector = app.GetType("mRemoteNG.App.Runtime", true).GetProperty("MessageCollector").GetValue(null);
            var logger = Activator.CreateInstance(loggerType, new[] { collector });
            var timer = Stopwatch.StartNew();
            loggerType.GetMethod("LogSystemData", Fields).Invoke(logger, null);
            timer.Stop();
            Console.WriteLine("Startup system diagnostics elapsed ms: " + timer.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            Environment.Exit(0);
        }
        var mainType = app.GetType("mRemoteNG.UI.Forms.FrmMain", true);
        var main = (Form)mainType.GetProperty("Default").GetValue(null);
        var menu = (MenuStrip)mainType.GetField("msMain", Fields).GetValue(main);
        Check(menu.Parent == main && menu.Dock == DockStyle.Top, "Menu is not isolated from draggable toolbars.");
        var tools = (Control)mainType.GetField("tsContainer", Fields).GetValue(main);
        var host = new Form { Text = "MGRemote - GUI validation", Opacity = 0, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), Size = new Size(1100, 740), Font = main.Font };
        host.Controls.Add(tools);
        host.Controls.Add(menu);
        host.MainMenuStrip = menu;
        host.Shown += async (_, _) => {
            try {
                adder = Activator.CreateInstance(app.GetType("mRemoteNG.UI.Panels.PanelAdder", true));
                add = adder.GetType().GetMethod("AddPanel");
                var general = (Form)add.Invoke(adder, new object[] { "General", true });
                Check(general != null, "General panel creation failed.");
                Application.DoEvents();
                var tabMenu = (ContextMenuStrip)general.GetType().GetProperty("TabPageContextMenuStrip").GetValue(general);
                var pane = (Control)general.GetType().GetProperty("Pane").GetValue(general);
                tabMenu.Show(pane, Point.Empty);
                Application.DoEvents();
                Check(ReferenceEquals(tabMenu.Items[0].Tag, general), "Tab menu targets the wrong panel.");
                tabMenu.Close();
                Check(menu.Top == 0 && tools.Top >= menu.Bottom, "Menu/toolbars overlap.");
                using (var image = new Bitmap(host.Width, host.Height)) {
                    host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                    image.Save(Path.Combine(artifacts, "gui-main.png"));
                }
                var references = new List<WeakReference>();
                for (int i = 0; i < 5; i++) CreateAndDispose(i);
                Collect();
                using var process = Process.GetCurrentProcess();
                int before = GetGuiResources(process.Handle, 0);
                for (int i = 0; i < 30; i++) references.Add(CreateAndDispose(i));
                Collect();
                int retained = references.FindAll(reference => reference.IsAlive).Count;
                int gdiDelta = GetGuiResources(process.Handle, 0) - before;
                Check(retained <= 1, "Disposed connection panels remain rooted: " + retained);
                Check(gdiDelta <= 8, "Repeated panel disposal retained GDI objects: " + gdiDelta);
                TestNewConnectionDialog(artifacts);
                TestConnectionEditor(main, host, artifacts);
                await TestDesktopAndTerminal(host, artifacts);
                await TestTunnelLifecycle(artifacts);
                TestRdpSharing(main, host, artifacts);
                int sshCycles = 0;
                for (int i = 0; i < 3; i++) {
                    using var ssh = (Form)Activator.CreateInstance(app.GetType("mRemoteNG.UI.Forms.SshProfileForm", true));
                    ssh.Opacity = 0; ssh.ShowInTaskbar = false; ssh.StartPosition = FormStartPosition.Manual; ssh.Location = new Point(-32000, -32000);
                    ssh.Size = i == 0 ? new Size(720, 560) : i == 1 ? new Size(640, 480) : new Size(1000, 700);
                    ssh.Show(); Application.DoEvents();
                    var viewport = (ScrollableControl)ssh.Controls[0];
                    Check(!viewport.HorizontalScroll.Visible, "SSH form requires horizontal scrolling at a supported size.");
                    if (i == 0) using (var image = new Bitmap(ssh.Width, ssh.Height)) {
                        ssh.DrawToBitmap(image, new Rectangle(Point.Empty, ssh.Size));
                        image.Save(Path.Combine(artifacts, "gui-ssh.png"));
                    }
                    sshCycles++;
                }
                await TestPlugins(fixture);
                var report = new { Result = "PASS", PanelsDisposed = 35, RetainedMeasuredPanels = retained, GdiObjectDelta = gdiDelta,
                    SshLayouts = sshCycles, NewConnectionDialogValidated = true, ConnectionEditorValidated = true, MultiScreenGeometryValidated = true,
                    TerminalChildCleanupValidated = true, AnyDeskOptionalDialogValidated = true, IllegalCrossThreadChecks = true, PluginInitializationOnUiThread = true,
                    TunnelProcessOwnershipValidated = true, OptionalSshKeyEditorValidated = true,
                    RdpSharingSettingsValidated = true, RdpActiveXRedirectionValidated = true, SshClipboardTextValidated = true, ConPtyAnsiTerminalValidated = true, SftpTransferDialogValidated = true,
                    CatalogDoesNotReinitializePlugins = true, RealMixedDpiMonitorsTested = false, LiveRdpSshTested = false };
                string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(artifacts, "gui-verification.json"), json);
                Console.WriteLine(json);
                Console.Out.Flush();
                Environment.Exit(0); // No real application's close/save handlers are invoked.
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.Flush(); Environment.Exit(1); }
        };
        Application.Run(host);
    }

    private static void TestRdpSharing(Form main, Form host, string artifacts)
    {
        var modelType = app.GetType("mRemoteNG.Connection.ConnectionInfo", true);
        var model = Activator.CreateInstance(modelType);
        modelType.GetProperty("RedirectClipboard").SetValue(model, false);
        modelType.GetProperty("RedirectDiskDrives").SetValue(model, Enum.Parse(modelType.GetProperty("RedirectDiskDrives").PropertyType, "None"));
        var dialogType = app.GetType("mRemoteNG.UI.Forms.RdpSharingForm", true);
        using (var dialog = (Form)Activator.CreateInstance(dialogType, new[] { model })) {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(); Application.DoEvents();
            ((CheckBox)dialog.Controls.Find("RdpFiles", true)[0]).Checked = true;
            Check(((CheckBox)dialog.Controls.Find("RdpClipboard", true)[0]).Checked, "Enabling RDP file copying did not enable the clipboard.");
            ((Button)dialog.Controls.Find("ApplyRdpSharing", true)[0]).PerformClick();
            Check((bool)modelType.GetProperty("RedirectClipboard").GetValue(model) && modelType.GetProperty("RedirectDiskDrives").GetValue(model).ToString() == "Custom" &&
                modelType.GetProperty("RedirectDiskDrivesCustom").GetValue(model).ToString() == "", "Clipboard file sharing exposed whole drives without selection.");
        }
        using (var dialog = (Form)Activator.CreateInstance(dialogType, new[] { model })) {
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.Show(); Application.DoEvents();
            ((CheckBox)dialog.Controls.Find("RdpClipboard", true)[0]).Checked = false;
            ((Button)dialog.CancelButton).PerformClick();
            Check((bool)modelType.GetProperty("RedirectClipboard").GetValue(model), "Cancelling RDP sharing settings changed the connection.");
        }
        // Exercise the real Windows RDP ActiveX settings without ever calling Connect.
        var inheritance = modelType.GetProperty("Inheritance").GetValue(model);
        inheritance.GetType().GetProperty("EverythingInherited").SetValue(inheritance, false);
        modelType.GetProperty("Hostname").SetValue(model, "rdp.example.invalid");
        modelType.GetProperty("Username").SetValue(model, ""); modelType.GetProperty("Password").SetValue(model, "");
        var legacyAuthentication = Enum.ToObject(modelType.GetProperty("RDPAuthenticationLevel").PropertyType, 2);
        modelType.GetProperty("RDPAuthenticationLevel").SetValue(model, legacyAuthentication);
        modelType.GetProperty("UseCredSsp").SetValue(model, false);
        modelType.GetProperty("ExternalCredentialProvider").SetValue(model, Enum.Parse(modelType.GetProperty("ExternalCredentialProvider").PropertyType, "None"));
        modelType.GetProperty("RDGatewayUsageMethod").SetValue(model, Enum.Parse(modelType.GetProperty("RDGatewayUsageMethod").PropertyType, "Never"));
        var rdp = Activator.CreateInstance(app.GetType("mRemoteNG.Connection.Protocol.RDP.RdpProtocol10", true));
        using (var container = new Panel { Parent = host, Size = new Size(400, 300) }) {
            var bridge = (Control)Activator.CreateInstance(app.GetType("mRemoteNG.Connection.InterfaceControl", true), new object[] { container, rdp, model });
            bridge.GetType().GetProperty("OriginalInfo").SetValue(bridge, model);
            rdp.GetType().GetProperty("InterfaceControl").SetValue(rdp, bridge);
            Check((bool)rdp.GetType().GetMethod("Initialize").Invoke(rdp, null), "Windows RDP ActiveX did not initialize offline.");
            var rdpBase = app.GetType("mRemoteNG.Connection.Protocol.RDP.RdpProtocol", true);
            dynamic client = rdpBase.GetField("_rdpClient", Fields).GetValue(rdp);
            Check((int)client.AdvancedSettings5.AuthenticationLevel == 1 && (bool)client.AdvancedSettings7.EnableCredSspSupport,
                "Legacy imported RDP preferences did not become a strict-authentication, CredSSP-enabled session.");
            Check(Equals(modelType.GetProperty("RDPAuthenticationLevel").GetValue(model), legacyAuthentication) &&
                !(bool)modelType.GetProperty("UseCredSsp").GetValue(model), "Session hardening modified the imported profile.");
            Check((bool)client.AdvancedSettings6.RedirectClipboard && (bool)client.AdvancedSettings2.RedirectDrives,
                "RDP ActiveX did not enable clipboard and the custom drive channel.");
            modelType.GetProperty("RedirectClipboard").SetValue(model, false);
            modelType.GetProperty("RedirectDiskDrives").SetValue(model, Enum.Parse(modelType.GetProperty("RedirectDiskDrives").PropertyType, "None"));
            rdpBase.GetMethod("SetRedirection", Fields).Invoke(rdp, null);
            Check(!(bool)client.AdvancedSettings6.RedirectClipboard && !(bool)client.AdvancedSettings2.RedirectDrives, "RDP sharing cannot be disabled again.");
        }
        ((IDisposable)rdp).Dispose();
        modelType.GetProperty("Protocol").SetValue(model, Enum.Parse(modelType.GetProperty("Protocol").PropertyType, "SSH2"));
        using var transfer = (Form)Activator.CreateInstance(app.GetType("mRemoteNG.UI.Forms.SshFileTransferForm", true), new[] { model });
        transfer.Opacity = 0; transfer.ShowInTaskbar = false; transfer.Show(); Application.DoEvents();
        string initialStatus = transfer.Controls.Find("TransferStatus", true)[0].Text;
        ((Button)transfer.Controls.Find("StartSshTransfer", true)[0]).PerformClick();
        Application.DoEvents();
        Check(transfer.Controls.Find("TransferStatus", true)[0].Text != initialStatus, "Invalid transfer input was not reported.");
        using var image = new Bitmap(transfer.Width, transfer.Height);
        transfer.DrawToBitmap(image, new Rectangle(Point.Empty, transfer.Size)); image.Save(Path.Combine(artifacts, "gui-sftp-transfer.png"));
    }

    private static async Task TestTunnelLifecycle(string artifacts)
    {
        var type = app.GetType("mRemoteNG.Connection.OpenSshTunnel", true);
        var reserve = type.GetMethod("ReserveLocalPort", BindingFlags.Static | BindingFlags.NonPublic);
        var start = type.GetMethod("StartProcessAndWaitAsync", BindingFlags.Static | BindingFlags.NonPublic);
        bool Alive(int id) { try { using var process = Process.GetProcessById(id); return !process.HasExited; } catch (ArgumentException) { return false; } }
        ProcessStartInfo Fixture(string mode, int port, string pidFile) {
            var info = new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(mode); info.ArgumentList.Add(port.ToString()); info.ArgumentList.Add(pidFile); return info;
        }
        string pidFile = Path.Combine(artifacts, "tunnel-" + Guid.NewGuid().ToString("N") + ".pid");
        try {
            int port = (int)reserve.Invoke(null, null);
            var task = (Task)start.Invoke(null, new object[] { Fixture("tunnel-fixture", port, pidFile), port, TimeSpan.FromSeconds(15), CancellationToken.None });
            await task;
            var tunnel = (IDisposable)task.GetType().GetProperty("Result").GetValue(task);
            int processId = (int)type.GetProperty("ProcessId").GetValue(tunnel);
            try {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                byte[] payload = { 77, 71, 82, 69 };
                await client.GetStream().WriteAsync(payload);
                var echoed = new byte[4];
                await client.GetStream().ReadExactlyAsync(echoed).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Check(payload.SequenceEqual(echoed), "Owned tunnel fixture did not carry data over loopback.");
            } finally { tunnel.Dispose(); tunnel.Dispose(); }
            for (int i = 0; i < 30 && Alive(processId); i++) await Task.Delay(100);
            Check(!Alive(processId), "Tunnel disposal left the owned SSH process alive.");
            File.Delete(pidFile);
            port = (int)reserve.Invoke(null, null);
            bool timedOut = false;
            try {
                var timeoutTask = (Task)start.Invoke(null, new object[] { Fixture("tunnel-fixture-no-listen", port, pidFile), port, TimeSpan.FromSeconds(2), CancellationToken.None });
                await timeoutTask;
            } catch (TimeoutException) { timedOut = true; }
            Check(timedOut, "Unready tunnel did not time out.");
            Check(File.Exists(pidFile), "Timeout fixture did not start.");
            processId = int.Parse(File.ReadAllText(pidFile));
            for (int i = 0; i < 30 && Alive(processId); i++) await Task.Delay(100);
            Check(!Alive(processId), "Failed tunnel startup left an orphan process.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool cancellationObserved = false;
            try {
                await (Task)start.Invoke(null, new object[] { Fixture("tunnel-fixture", port, pidFile), port, TimeSpan.FromSeconds(2), cancelled.Token });
            } catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved, "Cancelled tunnel startup proceeded.");
        } finally { if (File.Exists(pidFile)) File.Delete(pidFile); }
    }

    private static async Task TestDesktopAndTerminal(Form host, string artifacts)
    {
        var placement = app.GetType("mRemoteNG.UI.WindowPlacement", true);
        var clamp = placement.GetMethod("ClampToWorkingArea", BindingFlags.Static | BindingFlags.NonPublic);
        var cases = new[] {
            (new Rectangle(-3000, -100, 2500, 1800), new Rectangle(-1920, 40, 1920, 1040)),
            (new Rectangle(10000, 2000, 800, 600), new Rectangle(1920, 0, 2560, 1400)),
            (new Rectangle(4000, 3000, 800, 600), new Rectangle(0, 0, 1920, 1040)),
            (new Rectangle(0, -3000, 2000, 1200), new Rectangle(-1080, -1920, 1080, 1920)),
            (new Rectangle(100, 100, 640, 480), new Rectangle(0, 40, 1280, 680)),
            (new Rectangle(-100, -100, 1000, 1000), new Rectangle(0, 0, 320, 200)) };
        foreach (var (bounds, area) in cases) {
            var result = (Rectangle)clamp.Invoke(null, new object[] { bounds, area });
            Check(area.Contains(result) && result.Width == Math.Min(bounds.Width, area.Width) && result.Height == Math.Min(bounds.Height, area.Height),
                "Monitor working-area recovery failed: " + bounds + " -> " + result);
        }
        placement.GetMethod("MoveToScreen", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { host, Screen.PrimaryScreen });
        Check(Screen.FromControl(host).WorkingArea.Contains(host.Bounds), "Moved window overlaps the taskbar or leaves the screen.");
        var menuType = app.GetType("mRemoteNG.UI.Menu.AdvancedWindowMenu", true);
        using (var menu = (IDisposable)Activator.CreateInstance(menuType, new object[] { host })) {
            menuType.GetMethod("BuildAdditionalMenuItems").Invoke(menu, null);
            Check(menuType.GetMethod("GetScreenById").Invoke(menu, new object[] { 9999 }) == null, "Stale monitor selection was not ignored.");
            await Task.Run(() => menuType.GetMethod("OnDisplayChanged").Invoke(menu, new object[] { null, EventArgs.Empty }));
            Application.DoEvents();
        }
        using (var anyDesk = (Form)Activator.CreateInstance(app.GetType("mRemoteNG.UI.Forms.AnyDeskForm", true), new object[] { "rdp.example.invalid" })) {
            anyDesk.Opacity = 0; anyDesk.ShowInTaskbar = false; anyDesk.Show(); Application.DoEvents();
            var target = (TextBox)anyDesk.Controls.Find("AnyDeskTarget", true)[0];
            Check(target.Text == "", "AnyDesk incorrectly accepted a connection hostname as its ID.");
            target.Text = "--install";
            ((Button)anyDesk.Controls.Find("LaunchAnyDesk", true)[0]).PerformClick();
            Check(anyDesk.DialogResult != DialogResult.OK && anyDesk.Controls.Find("ValidationError", true)[0].Text.Length > 0,
                "AnyDesk dialog did not block an option instead of a destination.");
            using var image = new Bitmap(anyDesk.Width, anyDesk.Height);
            anyDesk.DrawToBitmap(image, new Rectangle(Point.Empty, anyDesk.Size));
            image.Save(Path.Combine(artifacts, "gui-anydesk.png"));
        }
        var terminalType = app.GetType("mRemoteNG.Connection.Protocol.Terminal.ProtocolTerminal", true);
        var model = Activator.CreateInstance(app.GetType("mRemoteNG.Connection.ConnectionInfo", true));
        var terminal = Activator.CreateInstance(terminalType, new object[] { model, true });
        var consoleField = terminalType.GetField("_consoleControl", Fields);
        var console = (Control)Activator.CreateInstance(consoleField.FieldType);
        console.Size = new Size(900, 500);
        host.Controls.Add(console);
        _ = console.Handle;
        consoleField.SetValue(terminal, console);
        terminalType.BaseType.GetProperty("Control", Fields).SetValue(terminal, console);
        try {
            console.GetType().GetMethod("StartProcess", new[] { typeof(string), typeof(string) }).Invoke(console,
                new object[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/D /Q /K" });

            await ((Task)console.GetType().GetProperty("Ready").GetValue(console)).WaitAsync(TimeSpan.FromSeconds(30));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)console.GetType().GetProperty("IsProcessRunning").GetValue(console) && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (!(bool)console.GetType().GetProperty("IsProcessRunning").GetValue(console)) {
                dynamic failedView = console.GetType().GetField("_view", Fields).GetValue(console);
                Console.WriteLine(await (Task<string>)failedView.ExecuteScriptAsync("JSON.stringify({url:location.href,html:document.documentElement.outerHTML.slice(-2800),Terminal:typeof Terminal,FitAddon:typeof FitAddon,term:typeof term})"));
            }
            Check((bool)console.GetType().GetProperty("IsProcessRunning").GetValue(console), "ConPTY process did not start: " + ((Control)console.GetType().GetField("_status", Fields).GetValue(console)).Text);
            dynamic view = console.GetType().GetField("_view", Fields).GetValue(console);
            var clipboardMenu = (ContextMenuStrip)console.GetType().GetField("_clipboardMenu", Fields).GetValue(console);
            for (int cycle = 0; cycle < 3; cycle++) {
                clipboardMenu.Show(host, Point.Empty);
                typeof(ToolStripDropDown).GetMethod("OnItemClicked", Fields).Invoke(clipboardMenu,
                    new object[] { new ToolStripItemClickedEventArgs(clipboardMenu.Items[0]) });
                Check(!clipboardMenu.IsDisposed, "SSH clipboard menu was disposed inside the item-click/close callback.");
                clipboardMenu.Close();
            }
            await (Task<string>)view.ExecuteScriptAsync("term.paste('echo MGREMOTE-%OS%\\r')");
            string roundtrip = "";
            deadline = DateTime.UtcNow.AddSeconds(10);
            do {
                await Task.Delay(100);
                roundtrip = await (Task<string>)view.ExecuteScriptAsync("Array.from({length:term.buffer.active.length},(_,i)=>term.buffer.active.getLine(i).translateToString(true)).join('\\n')");
            } while (!roundtrip.Contains("MGREMOTE-Windows_NT") && DateTime.UtcNow < deadline);
            Check(roundtrip.Contains("MGREMOTE-Windows_NT"), "Keyboard input/output did not round-trip through the real ConPTY child: " + roundtrip + " status=" + ((Control)console.GetType().GetField("_status", Fields).GetValue(console)).Text);
            // Real browser renderer: ANSI colors, cursor movement and alternate screen, no server or clipboard changes.
            await (Task<string>)view.ExecuteScriptAsync("term.write('\\x1b[2J\\x1b[H\\x1b[31mVT-fixture\\x1b[0m')");
            await Task.Delay(200);
            string rendered = await (Task<string>)view.ExecuteScriptAsync("term.buffer.active.getLine(0).translateToString(true)");
            Check(rendered.Contains("VT-fixture"), "ANSI terminal output was not rendered.");
            string color = await (Task<string>)view.ExecuteScriptAsync("term.buffer.active.getLine(0).getCell(0).getFgColor()");
            Check(color == "1", "ANSI foreground color was lost.");
            await (Task<string>)view.ExecuteScriptAsync("term.select(0,0,10)");
            string selection = await (Task<string>)view.ExecuteScriptAsync("term.getSelection()");
            Check(selection.Contains("VT-fixture"), "Terminal text selection is unavailable for copying.");
            await (Task<string>)view.ExecuteScriptAsync("window.probe=[];const probe=term.onData(t=>window.probe.push(t));term.write('\\x1b[?2004h')");
            await Task.Delay(100);
            await (Task)console.GetType().GetMethod("PasteTextAsync", Fields).Invoke(console, new object[] { "paste-fixture" });
            string pasted = await (Task<string>)view.ExecuteScriptAsync("JSON.stringify(window.probe)");
            Check(pasted.Contains("paste-fixture") && pasted.Contains("200~"), "Bracketed SSH paste was not delivered.");
            await (Task<string>)view.ExecuteScriptAsync("term.write('\\x1b[?1049hALT-screen')");
            await Task.Delay(100);
            string alternate = await (Task<string>)view.ExecuteScriptAsync("term.buffer.active.type");
            Check(alternate.Contains("alternate"), "Full-screen terminal applications cannot use the alternate screen.");
            console.Width = 800; console.Height = 450; await Task.Delay(100);
            await (Task<string>)view.ExecuteScriptAsync("term.write('\\x1b[?1049l');probe.dispose()");
            using (var screenshot = File.Create(Path.Combine(artifacts, "gui-ssh-terminal.png"))) {
                dynamic png = Enum.ToObject(Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat, Microsoft.Web.WebView2.Core", true), 0);
                await (Task)view.CoreWebView2.CapturePreviewAsync(png, screenshot);
            }
            int childId = (int)console.GetType().GetProperty("ProcessId").GetValue(console);
            terminalType.GetMethod("Close").Invoke(terminal, null);
            await ((Task)console.GetType().GetProperty("ShutdownCompletion").GetValue(console)).WaitAsync(TimeSpan.FromSeconds(10));
            Check(console.IsDisposed && !(bool)console.GetType().GetProperty("IsProcessRunning").GetValue(console), "Terminal close left its child process/control alive.");
            Check(clipboardMenu.IsDisposed, "SSH terminal disposal retained its clipboard menu.");
            bool childAlive;
            try { using var child = Process.GetProcessById(childId); childAlive = !child.HasExited; }
            catch (ArgumentException) { childAlive = false; }
            Check(!childAlive, "Terminal close left the owned process running.");
            await Task.Delay(100);
        } finally {
            if (!console.IsDisposed) {
                if ((bool)console.GetType().GetProperty("IsProcessRunning").GetValue(console)) console.GetType().GetMethod("StopProcess").Invoke(console, null);
                console.Dispose();
            }
            ((IDisposable)terminal).Dispose();
        }
    }

    private static void TestConnectionEditor(Form main, Form host, string artifacts)
    {
        var windows = app.GetType("mRemoteNG.App.AppWindows", true);
        var treeWindow = (Form)windows.GetProperty("TreeForm", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var editor = (Form)windows.GetProperty("ConfigForm", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var dock = main.GetType().GetField("pnlDock", Fields | BindingFlags.Public).GetValue(main);
        var stateType = treeWindow.GetType().GetProperty("DockState").PropertyType;
        treeWindow.GetType().GetMethod("Show", new[] { dock.GetType(), stateType }).Invoke(treeWindow, new[] { dock, Enum.Parse(stateType, "DockLeft") });
        var paneProperty = treeWindow.GetType().GetProperty("Pane");
        var pane = paneProperty.GetValue(treeWindow);
        var contentInterface = treeWindow.GetType().GetInterfaces().First(type => type.FullName == "WeifenLuo.WinFormsUI.Docking.IDockContent");
        editor.GetType().GetMethod("Show", new[] { paneProperty.PropertyType, contentInterface }).Invoke(editor, new[] { pane, null });
        Check(ReferenceEquals(editor.GetType().GetProperty("Pane").GetValue(editor), pane), "Could not reproduce the tabbed editor layout.");
        main.GetType().GetMethod("EnsureConfigurationPane", Fields).Invoke(main, null);
        Application.DoEvents();
        Check(!ReferenceEquals(editor.GetType().GetProperty("Pane").GetValue(editor), pane) && treeWindow.Visible && editor.Visible,
            "Configuration editor still shares the Connections tab pane.");

        var modelType = app.GetType("mRemoteNG.Connection.ConnectionInfo", true);
        var model = Activator.CreateInstance(modelType);
        modelType.GetProperty("Name").SetValue(model, "Serveur de test");
        modelType.GetProperty("Hostname").SetValue(model, "");
        var inheritance = modelType.GetProperty("Inheritance").GetValue(model);
        inheritance.GetType().GetProperty("EverythingInherited").SetValue(inheritance, false);
        var tree = treeWindow.GetType().GetProperty("ConnectionTree").GetValue(treeWindow);
        tree.GetType().GetProperty("Roots").SetValue(tree, new[] { model });
        Application.DoEvents();
        tree.GetType().GetProperty("SelectedObject").SetValue(tree, model);
        Check(ReferenceEquals(tree.GetType().GetProperty("SelectedObject").GetValue(tree), model), "Synthetic tree selection failed before editing.");
        var grid = (PropertyGrid)editor.GetType().GetField("_pGrid", Fields).GetValue(editor);
        var mode = grid.GetType().GetProperty("PropertyMode");
        mode.SetValue(grid, Enum.Parse(mode.PropertyType, "DefaultConnection"));
        var menu = (ContextMenuStrip)tree.GetType().GetProperty("ContextMenuStrip").GetValue(tree);
        var edit = menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Name == "EditConnection");
        Check(edit.ShortcutKeys == (Keys.Control | Keys.Enter), "Direct edit keyboard shortcut is missing.");
        edit.PerformClick();
        Application.DoEvents();
        Check(ReferenceEquals(grid.GetType().GetProperty("SelectedConnectionInfo").GetValue(grid), model) && mode.GetValue(grid).ToString() == "Connection", "Edit command did not select the real connection properties.");
        var properties = System.ComponentModel.TypeDescriptor.GetProperties(grid.SelectedObject);
        foreach (string property in new[] { "Hostname", "Username", "Protocol", "Port" })
            Check(properties[property] != null && !properties[property].IsReadOnly, "Connection property is not editable: " + property);
        properties["Username"].SetValue(grid.SelectedObject, "smoke-user");
        properties["Port"].SetValue(grid.SelectedObject, 3390);
        Check(modelType.GetProperty("Username").GetValue(model).ToString() == "smoke-user" &&
            (int)modelType.GetProperty("Port").GetValue(model) == 3390, "Property edits did not update the connection model.");
        grid.Refresh();
        Application.DoEvents();
        using var image = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
        image.Save(Path.Combine(artifacts, "gui-edit-connection.png"));
        var ssh = Activator.CreateInstance(modelType);
        modelType.GetProperty("Protocol").SetValue(ssh, Enum.Parse(modelType.GetProperty("Protocol").PropertyType, "SSH2"));
        var factory = Activator.CreateInstance(app.GetType("mRemoteNG.Connection.Protocol.ProtocolFactory", true));
        foreach (var backend in Enum.GetValues(modelType.GetProperty("TerminalBackend").PropertyType)) {
            modelType.GetProperty("TerminalBackend").SetValue(ssh, backend);
            using var protocol = (IDisposable)factory.GetType().GetMethod("CreateProtocol").Invoke(factory, new[] { ssh });
            Check(protocol.GetType().FullName == "mRemoteNG.Connection.Protocol.Terminal.ProtocolTerminal",
                "An imported SSH backend still launches PuTTY: " + backend);
            Check(Equals(modelType.GetProperty("TerminalBackend").GetValue(ssh), backend), "Protocol selection rewrote saved connection settings.");
        }
        modelType.GetProperty("Hostname").SetValue(ssh, "");
        modelType.GetProperty("TerminalBackend").SetValue(ssh, Enum.Parse(modelType.GetProperty("TerminalBackend").PropertyType, "WindowsTerminal"));
        editor.GetType().GetMethod("EditConnection").Invoke(editor, new[] { ssh });
        var keyProperty = System.ComponentModel.TypeDescriptor.GetProperties(grid.SelectedObject)["SshPrivateKeyPath"];
        Check(keyProperty != null && keyProperty.GetEditor(typeof(System.Drawing.Design.UITypeEditor)) != null, "Optional SSH private-key browser is missing from the real property grid.");
        string identity = Path.Combine(artifacts, "gui-private-" + Guid.NewGuid().ToString("N"));
        try {
            File.WriteAllText(identity, "-----BEGIN OPENSSH PRIVATE KEY-----\nSYNTHETIC UI FIXTURE\n-----END OPENSSH PRIVATE KEY-----\n");
            var security = new System.Security.AccessControl.FileSecurity();
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(user, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(identity), security);
            keyProperty.SetValue(grid.SelectedObject, identity);
            Check(modelType.GetProperty("SSHOptions").GetValue(ssh).ToString().Contains("-i \"" + identity + "\""), "Key browser did not update the serialized SSH options.");
            Check((string)keyProperty.GetValue(grid.SelectedObject) == identity, "Key browser cannot read the selected identity back.");
            File.Delete(identity);
            Check((string)keyProperty.GetValue(grid.SelectedObject) == identity, "Deleted identity path disappeared instead of remaining editable.");
            keyProperty.ResetValue(grid.SelectedObject);
            Check(!modelType.GetProperty("SSHOptions").GetValue(ssh).ToString().Contains("-i"), "Private key cannot be made optional again.");
        } finally { File.Delete(identity); }
    }

    private static void TestNewConnectionDialog(string artifacts)
    {
        var modelType = app.GetType("mRemoteNG.Connection.ConnectionInfo", true);
        var dialogType = app.GetType("mRemoteNG.UI.Forms.NewConnectionForm", true);
        object NewModel() {
            var model = Activator.CreateInstance(modelType);
            modelType.GetProperty("Name").SetValue(model, "Unchanged");
            modelType.GetProperty("Password").SetValue(model, "synthetic-default");
            var inheritance = modelType.GetProperty("Inheritance").GetValue(model);
            inheritance.GetType().GetProperty("EverythingInherited").SetValue(inheritance, true);
            return model;
        }
        string Value(object model, string property) => modelType.GetProperty(property).GetValue(model)?.ToString();
        T Field<T>(Form dialog, string name) where T : Control => (T)dialog.Controls.Find(name, true)[0];
        Form Open(object model) {
            var dialog = (Form)Activator.CreateInstance(dialogType, new[] { model });
            dialog.Opacity = 0; dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-32000, -32000); dialog.Show(); Application.DoEvents(); return dialog;
        }
        var rdp = NewModel();
        using (var dialog = Open(rdp)) {
            var protocol = Field<ComboBox>(dialog, "Protocol");
            var host = Field<TextBox>(dialog, "Hostname");
            var create = Field<Button>(dialog, "CreateConnection");
            Check(protocol.Items.Count == 3 && protocol.SelectedIndex == 0 && Field<NumericUpDown>(dialog, "Port").Value == 3389,
                "Creation dialog lacks explicit secure protocols/default RDP port.");
            create.PerformClick();
            Check(dialog.DialogResult != DialogResult.OK && Value(rdp, "Name") == "Unchanged", "Empty host committed a connection.");
            host.Text = "https://invalid.example/"; create.PerformClick();
            Check(dialog.DialogResult != DialogResult.OK, "RDP accepted a URL as hostname.");
            host.Text = "rdp.example.invalid";
            Field<TextBox>(dialog, "Username").Text = "alice";
            Field<TextBox>(dialog, "Domain").Text = "DEMO";
            using (var image = new Bitmap(dialog.Width, dialog.Height)) {
                dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size));
                image.Save(Path.Combine(artifacts, "gui-new-connection.png"));
            }
            create.PerformClick();
            Check(dialog.DialogResult == DialogResult.OK && Value(rdp, "Protocol") == "RDP" && Value(rdp, "Port") == "3389" &&
                Value(rdp, "Name") == "rdp.example.invalid" && Value(rdp, "Domain") == "DEMO" && Value(rdp, "RDPAuthenticationLevel") == "AuthRequired" &&
                Value(rdp, "UseCredSsp") == "True" && Value(rdp, "Password") == "" && Value(rdp, "RedirectClipboard") == "True", "RDP creation did not apply secure explicit settings and clipboard.");
        }
        var ssh = NewModel();
        using (var dialog = Open(ssh)) {
            var protocol = Field<ComboBox>(dialog, "Protocol");
            var port = Field<NumericUpDown>(dialog, "Port");
            protocol.SelectedIndex = 1;
            Check(port.Value == 22 && !Field<TextBox>(dialog, "Domain").Enabled, "SSH selection did not change protocol fields.");
            port.Value = 2222; protocol.SelectedIndex = 0; protocol.SelectedIndex = 1;
            Check(port.Value == 2222, "Protocol switch discarded a custom port.");
            var host = Field<TextBox>(dialog, "Hostname");
            host.Text = "ssh-profile";
            Field<TextBox>(dialog, "SshPrivateKey").Text = Path.Combine(artifacts, "missing-private-key");
            Field<Button>(dialog, "CreateConnection").PerformClick();
            Check(dialog.DialogResult != DialogResult.OK && Value(ssh, "Name") == "Unchanged", "Missing private key committed a connection.");
            Field<TextBox>(dialog, "SshPrivateKey").Clear();
            host.Text = "-oProxyCommand=bad";
            Field<Button>(dialog, "CreateConnection").PerformClick();
            Check(dialog.DialogResult != DialogResult.OK && Value(ssh, "Name") == "Unchanged", "Unsafe SSH host changed the model.");
            host.Text = "ssh-profile";
            Field<Button>(dialog, "CreateConnection").PerformClick();
            Check(dialog.DialogResult == DialogResult.OK && Value(ssh, "Protocol") == "SSH2" && Value(ssh, "TerminalBackend") == "WindowsTerminal" &&
                Value(ssh, "Port") == "2222" && Value(ssh, "Username") == "" && Value(ssh, "Password") == "", "SSH alias creation did not preserve secure choices.");
            var inheritance = modelType.GetProperty("Inheritance").GetValue(ssh);
            foreach (string property in new[] { "Protocol", "Port", "Username", "Domain", "Password", "TerminalBackend", "RDPAuthenticationLevel", "UseCredSsp", "SSHOptions" })
                Check(!(bool)inheritance.GetType().GetProperty(property).GetValue(inheritance), "Explicit choice still inherits " + property);
        }
        var cancelled = NewModel();
        using (var dialog = Open(cancelled)) {
            Field<TextBox>(dialog, "Hostname").Text = "cancel.example.invalid";
            ((Button)dialog.CancelButton).PerformClick();
            Check(dialog.DialogResult == DialogResult.Cancel && Value(cancelled, "Name") == "Unchanged" && Value(cancelled, "Password") == "synthetic-default",
                "Cancelling connection creation modified the model.");
        }
        var vnc = NewModel();
        using (var dialog = Open(vnc)) {
            Field<ComboBox>(dialog, "Protocol").SelectedIndex = 2;
            Field<TextBox>(dialog, "Hostname").Text = "vnc.example.invalid";
            Field<Button>(dialog, "CreateConnection").PerformClick();
            Check(dialog.DialogResult != DialogResult.OK, "New VNC connection silently bypassed its SSH tunnel.");
            var tunnel = Field<ComboBox>(dialog, "SshTunnel");
            tunnel.Items.Add("Synthetic SSH bastion"); tunnel.SelectedItem = "Synthetic SSH bastion";
            Field<Button>(dialog, "CreateConnection").PerformClick();
            Check(dialog.DialogResult == DialogResult.OK && Value(vnc, "Protocol") == "VNC" && Value(vnc, "Port") == "5900" &&
                Value(vnc, "SSHTunnelConnectionName") == "Synthetic SSH bastion", "VNC creation did not retain its selected SSH route.");
        }
    }

    private static async Task TestPlugins(string fixture)
    {
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(fixture, "Probe.dll"));
        File.Copy(typeof(Program).Assembly.Location, Path.Combine(fixture, "Duplicate.dll"));
        File.WriteAllText(Path.Combine(fixture, "Native.dll"), "NOT A MANAGED DLL");
        var settingsType = app.GetType("mRemoteNG.Properties.Settings", true);
        object settings = settingsType.GetProperty("Default").GetValue(null);
        var folder = settingsType.GetProperty("PluginFolderPath");
        object previous = folder.GetValue(settings);
        try {
            folder.SetValue(settings, fixture);
            var type = app.GetType("mRemoteNG.Plugins.PluginService", true);
            object service = Activator.CreateInstance(type);
            var catalog = type.GetMethod("GetPluginCatalog");
            catalog.Invoke(service, null);
            catalog.Invoke(service, null);
            Check(ProbePlugin.Initializations == 0, "Reading the plugin catalog initialized plugins.");
            int uiThread = Environment.CurrentManagedThreadId;
            await (Task)type.GetMethod("LoadPluginsAsync").Invoke(service, new object[] { CancellationToken.None });
            Check(ProbePlugin.Initializations == 1 && ProbePlugin.Thread == uiThread, "Plugins did not initialize once on the UI thread.");
            int disposals = ProbePlugin.Disposals;
            catalog.Invoke(service, null);
            Check(ProbePlugin.Initializations == 1 && ProbePlugin.Disposals == disposals, "Catalog disposed or reinitialized a live plugin.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            bool cancelled = false;
            try { await (Task)type.GetMethod("LoadPluginsAsync").Invoke(service, new object[] { cancellation.Token }); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Plugin discovery cancellation was ignored.");
        } finally {
            folder.SetValue(settings, previous);
            foreach (string name in new[] { "Probe.dll", "Duplicate.dll", "Native.dll" }) File.Delete(Path.Combine(fixture, name));
            Directory.Delete(fixture, false);
        }
    }
}
