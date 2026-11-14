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
        if (args.Length > 1 && args[0] == "load-fixture") {
            for (int i = 0; i < 500; i++) Console.WriteLine(args[1] + "-" + i);
            Console.Out.Flush(); return;
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
            return;
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
                await TestSerialConsole(artifacts);
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
                await TestConcurrentTerminals(artifacts);
                await TestVirtualServers(host, artifacts);
                var report = new { Result = "PASS", PanelsDisposed = 35, RetainedMeasuredPanels = retained, GdiObjectDelta = gdiDelta,
                    SshLayouts = sshCycles, NewConnectionDialogValidated = true, ConnectionEditorValidated = true, MultiScreenGeometryValidated = true,
                    TerminalChildCleanupValidated = true, SerialConsoleValidated = true, AnyDeskOptionalDialogValidated = true, IllegalCrossThreadChecks = true, PluginInitializationOnUiThread = true,
                    TunnelProcessOwnershipValidated = true, OptionalSshKeyEditorValidated = true,
                    RdpSharingSettingsValidated = true, RdpActiveXRedirectionValidated = true, RdpReconnectUiDispatchValidated = true, SshClipboardTextValidated = true, ConPtyAnsiTerminalValidated = true, SftpTransferDialogValidated = true,
                    CatalogDoesNotReinitializePlugins = true, RealMixedDpiMonitorsTested = false, LiveRdpSshTested = false };
                string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(artifacts, "gui-verification.json"), json);
                Console.WriteLine(json);
                Console.Out.Flush();
                Environment.ExitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.Flush(); Environment.ExitCode = 1; }
            finally {
                try {
                    main.Dispose();
                    foreach (Form form in Application.OpenForms.Cast<Form>().Reverse().ToArray()) form.Dispose();
                } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                Application.ExitThread();
            }
        };
        Application.Run(host);
    }

    private static async Task TestVirtualServers(Form host, string artifacts)
    {
        string file = Environment.GetEnvironmentVariable("MGREMOTE_REMOTE_FIXTURE");
        if (string.IsNullOrEmpty(file)) return;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file)); var lab = json.RootElement;
        string marker = "MGREMOTESSH" + Guid.NewGuid().ToString("N");
        var type = app.GetType("mRemoteNG.UI.Controls.SshTerminal", true);
        var terminal = (Control)Activator.CreateInstance(type, true); terminal.Dock = DockStyle.Fill; host.Controls.Add(terminal); terminal.BringToFront();
        try {
            type.GetMethod("StartProcess").Invoke(terminal, new object[] { lab.GetProperty("Executable").GetString(), lab.GetProperty("Arguments").GetString() + " echo " + marker });
            await ((Task)type.GetProperty("Ready").GetValue(terminal)).WaitAsync(TimeSpan.FromSeconds(20));
            var deadline = DateTime.UtcNow.AddSeconds(30); bool received = false;
            while (!received && DateTime.UtcNow < deadline) {
                dynamic view = type.GetField("_view", Fields).GetValue(terminal);
                if (view.CoreWebView2 != null) {
                    string text = await (Task<string>)view.ExecuteScriptAsync("typeof term==='undefined'?'':Array.from({length:term.buffer.active.length},(_,i)=>term.buffer.active.getLine(i).translateToString(true)).join('\\n')");
                    received = text.Contains(marker);
                }
                if (!received) await Task.Delay(100);
            }
            Check(received, "The application SSH terminal did not authenticate and display the loopback server output.");
        } finally { terminal.Dispose(); await ((Task)type.GetProperty("ShutdownCompletion").GetValue(terminal)).WaitAsync(TimeSpan.FromSeconds(15)); }
        string root = lab.GetProperty("Root").GetString(); string source = Path.Combine(root, "binary.bin"), downloaded = Path.Combine(root, "downloaded.bin");
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32768); File.WriteAllBytes(source, bytes);
        string endpoint = lab.GetProperty("User").GetString() + "@127.0.0.1:" + lab.GetProperty("RemotePath").GetString();
        async Task<int> Scp(string from, string to) {
            var start = new ProcessStartInfo(lab.GetProperty("ScpExecutable").GetString()) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-F", Path.Combine(root,"client.conf"), "-i", Path.Combine(root,"client"), "-P", lab.GetProperty("SshPort").GetInt32().ToString(), "--", from, to }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); await Task.WhenAll(output,error); return process.ExitCode; }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        Check(await Scp(source, endpoint + "/binary.bin") == 0, "Loopback SFTP upload failed.");
        Check(await Scp(endpoint + "/binary.bin", downloaded) == 0 && File.ReadAllBytes(downloaded).SequenceEqual(bytes), "Loopback SFTP binary round trip failed.");
        Check(await Scp(source, endpoint + "/missing-parent/output.bin") != 0, "Loopback SFTP error was reported as success.");
        await TestSftpBrowser(artifacts, lab);
        bool rdp = lab.GetProperty("RdpAvailable").GetBoolean();
        if (rdp) await TestVirtualRdp(host, lab);
        File.WriteAllText(Path.Combine(artifacts,"network-verification.json"), System.Text.Json.JsonSerializer.Serialize(new {
            Result="PASS", ApplicationSshTerminalAuthenticated=true, LoopbackSftpBinaryRoundTrip=true, FailedTransferRejected=true,
            WindowsServerRdpDesktopLogin=rdp, RdpScope=rdp ? "Isolated Windows Server loopback, NLA and trusted localhost test certificate" : "Client Windows single-session image; RDP settings tested offline",
            ProductionEndpointsModified=false
        },new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine("PASS: application SSH terminal, pinned host key, real loopback SFTP binary round trip and transfer error; Windows Server RDP desktop=" + rdp);
    }
    private static async Task TestSftpBrowser(string artifacts, System.Text.Json.JsonElement lab)
    {
        var type = app.GetType("mRemoteNG.Security.SftpBrowserSession", true);
        string root = lab.GetProperty("Root").GetString(), remoteRoot = lab.GetProperty("RemotePath").GetString();
        byte[] hostKey = Convert.FromBase64String(File.ReadAllText(Path.Combine(root,"host.pub")).Split(' ')[1]);
        Func<string, byte[], bool> trust = (_, key) => key.SequenceEqual(hostKey);
        object Session(string password, string[] keys, Func<string,byte[],bool> verify) => Activator.CreateInstance(type, new object[] { "127.0.0.1", lab.GetProperty("SshPort").GetInt32(), lab.GetProperty("User").GetString(), password, keys, (Func<string,string>)(_ => throw new Exception("Unexpected encrypted fixture key.")), verify });
        async Task Call(object session, string method, params object[] args) => await ((Task)type.GetMethod(method).Invoke(session,args)).WaitAsync(TimeSpan.FromSeconds(60));
        using (var refused = (IDisposable)Session(null,new[]{Path.Combine(root,"client")},(_,_)=>false)) {
            bool rejected=false;
            try { await Call(refused,"ConnectAsync",CancellationToken.None); } catch (Exception) { rejected=true; }
            Check(rejected,"SFTP browser accepted an untrusted host key.");
        }
        if (lab.TryGetProperty("SftpPassword",out var password)) {
            using var passwordSession=(IDisposable)Session(password.GetString(),Array.Empty<string>(),trust);
            await Call(passwordSession,"ConnectAsync",CancellationToken.None);
        }
        var session=Session(null,new[]{Path.Combine(root,"client")},trust);
        await Call(session,"ConnectAsync",CancellationToken.None);
        var model=Activator.CreateInstance(app.GetType("mRemoteNG.Connection.ConnectionInfo",true));
        using var browser=(Form)Activator.CreateInstance(app.GetType("mRemoteNG.UI.Forms.SshFileTransferForm",true),new[]{model});
        var formType=browser.GetType(); formType.GetField("_session",Fields).SetValue(browser,session);
        browser.ShowInTaskbar=false; browser.Show();
        string source=Path.Combine(root,"browser-source"),target=Path.Combine(root,"browser-download");
        Directory.CreateDirectory(Path.Combine(source,"Lot été","sous dossier"));Directory.CreateDirectory(target);
        byte[] binary=new byte[4*1024*1024];Random.Shared.NextBytes(binary);
        File.WriteAllBytes(Path.Combine(source,"Lot été","sous dossier","binary.bin"),binary);
        File.WriteAllBytes(Path.Combine(source,"Lot été","vide.txt"),Array.Empty<byte>());
        File.WriteAllText(Path.Combine(source,"notes é.txt"),"SFTP multiple files — UTF-8");
        var local=(ListView)browser.Controls.Find("SftpLocalFiles",true)[0];var remote=(ListView)browser.Controls.Find("SftpRemoteFiles",true)[0];
        var queue=(ListView)browser.Controls.Find("SftpQueue",true)[0];
        Check(local.MultiSelect&&remote.MultiSelect&&local.AllowDrop&&remote.AllowDrop,"SFTP panes do not support multiple selection and mouse drops.");
        browser.Controls.Find("TransferRemotePath",true)[0].Text=remoteRoot;
        await ((Task)formType.GetMethod("BrowseLocalAsync",Fields).Invoke(browser,new object[]{source}));
        var onDrop=typeof(Control).GetMethod("OnDragDrop",Fields);
        int ticks=0;using var heartbeat=new System.Windows.Forms.Timer{Interval=25};heartbeat.Tick+=(_,_)=>ticks++;heartbeat.Start();
        async Task Drain(int count,bool success) {
            await ((Task)formType.GetField("_transfers",Fields).GetValue(browser)).WaitAsync(TimeSpan.FromSeconds(90));
            Check(queue.Items.Count==count,"SFTP mouse drop lost selected items.");
            Check(queue.Items[count-1].SubItems[3].Text.StartsWith(success?"Terminé":"Échec"),"Unexpected SFTP queue outcome: "+queue.Items[count-1].SubItems[3].Text);
        }
        string[] paths={Path.Combine(source,"Lot été"),Path.Combine(source,"notes é.txt")};
        var upload=new DataObject();upload.SetData(DataFormats.FileDrop,paths);
        onDrop.Invoke(remote,new object[]{new DragEventArgs(upload,1,-10000,-10000,DragDropEffects.Copy,DragDropEffects.Copy)});
        await Drain(2,true);Check(queue.Items[0].SubItems[3].Text=="Terminé","Recursive SFTP upload failed: "+queue.Items[0].SubItems[3].Text);
        browser.Controls.Find("TransferLocalPath",true)[0].Text=target;
        string[] remotePaths={remoteRoot.TrimEnd('/')+"/Lot été",remoteRoot.TrimEnd('/')+"/notes é.txt"};
        var selection=Activator.CreateInstance(formType.GetNestedType("RemoteSelection",BindingFlags.NonPublic),new object[]{browser,remotePaths});
        var download=new DataObject();download.SetData("MGRemote.Sftp.Selection",false,selection);
        onDrop.Invoke(local,new object[]{new DragEventArgs(download,1,-10000,-10000,DragDropEffects.Copy,DragDropEffects.Copy)});
        await Drain(4,true);
        Check(File.ReadAllBytes(Path.Combine(target,"Lot été","sous dossier","binary.bin")).SequenceEqual(binary),"SFTP browser binary folder round trip differs.");
        Check(new FileInfo(Path.Combine(target,"Lot été","vide.txt")).Length==0&&File.ReadAllText(Path.Combine(target,"notes é.txt"))==File.ReadAllText(paths[1]),"Empty/Unicode/multiple SFTP files differ.");
        var existing=new DataObject();existing.SetData(DataFormats.FileDrop,new[]{paths[1]});
        onDrop.Invoke(remote,new object[]{new DragEventArgs(existing,1,-10000,-10000,DragDropEffects.Copy,DragDropEffects.Copy)});await Drain(5,false);
        var child=type.GetMethod("LocalChild",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(string invalid in new[]{"../escape","..\\escape","stream:ads","CON.txt","trailing.","bad\nname"}) {
            bool rejected=false;try{child.Invoke(null,new object[]{target,invalid});}catch(TargetInvocationException){rejected=true;}
            Check(rejected,"Unsafe downloaded SFTP name accepted: "+invalid);
        }
        using(var cancelled=new CancellationTokenSource()) {
            cancelled.Cancel();bool rejected=false;
            try{await Call(session,"TransferAsync",paths[1],remoteRoot,true,false,null,cancelled.Token);}catch(Exception){rejected=true;}
            Check(rejected,"Cancelled SFTP transfer was executed.");
        }
        Check(ticks>0,"SFTP transfers blocked the GUI heartbeat.");
        using(var image=new Bitmap(browser.Width,browser.Height)){browser.DrawToBitmap(image,new Rectangle(Point.Empty,browser.Size));image.Save(Path.Combine(artifacts,"gui-sftp-browser.png"));}
        browser.Close();var deadline=DateTime.UtcNow.AddSeconds(10);while(!browser.IsDisposed&&DateTime.UtcNow<deadline)await Task.Delay(20);
        Check(browser.IsDisposed,"SFTP browser did not close and release its worker.");
        File.WriteAllText(Path.Combine(artifacts,"sftp-browser-verification.json"),System.Text.Json.JsonSerializer.Serialize(new{Result="PASS",TwoPanes=true,MultipleFilesAndRecursiveFolders=true,MouseDropHandlersBothDirections=true,ExternalExplorerUploadDrop=true,UnicodeAndBinaryAndEmptyFiles=true,HostKeyRejection=true,PasswordWithoutKey=lab.TryGetProperty("SftpPassword",out _),OverwriteRefusedByDefault=true,UnsafeNamesRejected=true,CancellationTested=true,UiHeartbeatTicks=ticks,PhysicalMouseTested=false},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("PASS: two-pane SFTP browser, multiple mouse-drop files/folders both ways, pinned host key, optional key, binary/Unicode/empty files, overwrite protection, cancellation and live GUI heartbeat.");
    }
    private static async Task TestVirtualRdp(Form host, System.Text.Json.JsonElement lab)
    {
        var modelType = app.GetType("mRemoteNG.Connection.ConnectionInfo",true); var model = Activator.CreateInstance(modelType);
        modelType.GetProperty("Protocol").SetValue(model,Enum.Parse(modelType.GetProperty("Protocol").PropertyType,"RDP"));
        modelType.GetProperty("Name").SetValue(model,"Isolated RDP fixture"); modelType.GetProperty("Hostname").SetValue(model,"localhost");
        modelType.GetProperty("Port").SetValue(model,lab.GetProperty("RdpPort").GetInt32());
        modelType.GetProperty("Username").SetValue(model,lab.GetProperty("User").GetString());
        modelType.GetProperty("Domain").SetValue(model,lab.GetProperty("RdpDomain").GetString());
        modelType.GetProperty("Password").SetValue(model,lab.GetProperty("RdpPassword").GetString());
        modelType.GetProperty("RDPAuthenticationLevel").SetValue(model,Enum.ToObject(modelType.GetProperty("RDPAuthenticationLevel").PropertyType,1));
        modelType.GetProperty("UseCredSsp").SetValue(model,true);
        modelType.GetProperty("UseConsoleSession").SetValue(model,true);
        modelType.GetProperty("ExternalCredentialProvider").SetValue(model,Enum.Parse(modelType.GetProperty("ExternalCredentialProvider").PropertyType,"None"));
        modelType.GetProperty("RDGatewayUsageMethod").SetValue(model,Enum.Parse(modelType.GetProperty("RDGatewayUsageMethod").PropertyType,"Never"));
        var inheritance=modelType.GetProperty("Inheritance").GetValue(model); inheritance.GetType().GetProperty("EverythingInherited").SetValue(inheritance,false);
        object rdp=Activator.CreateInstance(app.GetType("mRemoteNG.Connection.Protocol.RDP.RdpProtocol10",true));
        var rdpBase=app.GetType("mRemoteNG.Connection.Protocol.RDP.RdpProtocol",true);
        using var container=new Panel { Parent=host, Dock=DockStyle.Fill }; container.BringToFront();
        var bridge=(Control)Activator.CreateInstance(app.GetType("mRemoteNG.Connection.InterfaceControl",true),new object[]{container,rdp,model});
        bridge.GetType().GetProperty("OriginalInfo").SetValue(bridge,model);rdp.GetType().GetProperty("InterfaceControl").SetValue(rdp,bridge);
        string failure=null;
        Action<object,string,int?> disconnected=(_,message,code)=>{ failure="RDP fixture disconnected: "+code+" "+message;rdpBase.GetField("loginComplete",Fields).SetValue(rdp,true); };
        var evt=rdp.GetType().GetEvent("Disconnected");evt.AddEventHandler(rdp,Delegate.CreateDelegate(evt.EventHandlerType,disconnected.Target,disconnected.Method));
        try {
            Check((bool)rdp.GetType().GetMethod("Initialize").Invoke(rdp,null),"Virtual RDP control did not initialize.");
            Check((bool)rdp.GetType().GetMethod("Connect").Invoke(rdp,null),"Virtual RDP connection did not start.");
            var deadline=DateTime.UtcNow.AddSeconds(90);
            while (!(bool)rdpBase.GetField("loginComplete",Fields).GetValue(rdp) && failure==null && DateTime.UtcNow<deadline) await Task.Delay(100);
            Check(failure==null && (bool)rdpBase.GetField("loginComplete",Fields).GetValue(rdp),failure??"Virtual RDP desktop logon timed out.");
        } finally { rdp.GetType().GetMethod("Close").Invoke(rdp,null);((IDisposable)rdp).Dispose();modelType.GetProperty("Password").SetValue(model,""); }
    }

    private static async Task TestConcurrentTerminals(string artifacts)
    {
        const int count = 16;
        var type = app.GetType("mRemoteNG.UI.Controls.PseudoTerminal", true);
        var sessions = new List<object>(); var outputs = new List<System.Text.StringBuilder>();
        var ids = new List<int>(); var markers = new List<string>();
        int ticks = 0; using var heartbeat = new System.Windows.Forms.Timer { Interval = 50 };
        heartbeat.Tick += (_, _) => ticks++; heartbeat.Start(); var elapsed = Stopwatch.StartNew();
        try {
            for (int i = 0; i < count; i++) {
                var session = Activator.CreateInstance(type, true); sessions.Add(session);
                var output = new System.Text.StringBuilder(); outputs.Add(output);
                string marker = "MGREMOTELOAD" + Guid.NewGuid().ToString("N"); markers.Add(marker);
                Func<string, Task> display = text => { lock (output) { if (output.Length > 2000000) throw new Exception("Load fixture output exceeded its bound."); output.Append(text); } return Task.CompletedTask; };
                type.GetMethod("Start").Invoke(session, new object[] { Environment.ProcessPath, "load-fixture " + marker, display });
                ids.Add((int)type.GetProperty("ProcessId").GetValue(session));
                await Task.Delay(10);
            }
            var deadline = DateTime.UtcNow.AddSeconds(60);
            bool Complete() => outputs.Select((o, i) => { lock (o) return o.ToString().Contains(markers[i] + "-499"); }).All(v => v);
            while (!Complete() && DateTime.UtcNow < deadline) await Task.Delay(50);
            Check(Complete(), "Concurrent ConPTY sessions lost their final output or stalled.");
            Check(ticks >= 2, "The GUI message loop did not run during concurrent sessions.");
        } finally {
            foreach (var session in sessions) ((IDisposable)session).Dispose();
            await Task.WhenAll(sessions.Select(s => (Task)type.GetProperty("ShutdownCompletion").GetValue(s))).WaitAsync(TimeSpan.FromSeconds(30));
            heartbeat.Stop();
        }
        foreach (int id in ids) {
            bool alive; try { using var process = Process.GetProcessById(id); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
            Check(!alive, "Concurrent terminal cleanup left a child process.");
        }
        File.WriteAllText(Path.Combine(artifacts, "concurrency-verification.json"), System.Text.Json.JsonSerializer.Serialize(new {
            Result = "PASS", Sessions = count, UiHeartbeatTicks = ticks, ElapsedMilliseconds = elapsed.ElapsedMilliseconds,
            FinalOutputVerified = true, OwnedProcessesTerminated = true, Scope = "Local native ConPTY processes; not remote servers or a production throughput benchmark"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: 16 concurrent native ConPTY processes, final output, live GUI heartbeat and child cleanup.");
    }

    private static async Task TestSerialConsole(string artifacts)
    {
        var type = app.GetType("mRemoteNG.UI.Controls.SerialSession", true);
        var validate = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic);
        validate.Invoke(null, new object[] { "COM3", 9600, 8 });
        bool rejected = false;
        try { validate.Invoke(null, new object[] { "COM1 & command", 9600, 8 }); }
        catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { rejected = true; }
        Check(rejected, "Serial validation accepted a non-COM device.");
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("\u001b[31mswitch>é\u001b[0m"); int offset = 0, closed = 0;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var written = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        Func<byte[], int> read = buffer => { if (offset < bytes.Length) { buffer[0] = bytes[offset++]; return 1; } Thread.Sleep(20); throw new TimeoutException(); };
        Action<byte[]> write = data => written.Enqueue(data);
        Action close = () => Interlocked.Exchange(ref closed, 1);
        var session = Activator.CreateInstance(type, Fields, null, new object[] { read, write, close }, null);
        try {
            type.GetMethod("Start", Fields).Invoke(session, new object[] { new Func<string, Task>(text => { output.Enqueue(text); return Task.CompletedTask; }) });
            Check((bool)type.GetMethod("Send", Fields).Invoke(session, new object[] { "show version\r" }), "Serial input was refused.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while ((string.Concat(output) != System.Text.Encoding.UTF8.GetString(bytes) || written.IsEmpty) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(string.Concat(output) == System.Text.Encoding.UTF8.GetString(bytes), "Serial output lost ANSI or split UTF-8 characters.");
            Check(written.TryDequeue(out var value) && System.Text.Encoding.UTF8.GetString(value) == "show version\r", "Serial input was changed.");
        } finally { ((IDisposable)session).Dispose(); await ((Task)type.GetProperty("ShutdownCompletion").GetValue(session)).WaitAsync(TimeSpan.FromSeconds(3)); }
        Check(closed == 1 && !(bool)type.GetProperty("IsRunning").GetValue(session), "Serial shutdown retained its transport.");
        using var form = (Form)Activator.CreateInstance(app.GetType("mRemoteNG.UI.Forms.SerialConsoleForm", true));
        form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents();
        ((ComboBox)form.Controls.Find("SerialPort", true)[0]).Text = "INVALID";
        ((Button)form.Controls.Find("SerialConnect", true)[0]).PerformClick(); Application.DoEvents();
        Check(form.Controls.Find("SerialStatus", true)[0].Text.Contains("invalide"), "Serial console silently ignored an invalid device.");
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(artifacts, "gui-serial.png"));
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
            Check((int)client.AdvancedSettings5.AuthenticationLevel == 2 && (bool)client.AdvancedSettings7.EnableCredSspSupport,
                "Explicit certificate confirmation was not honored with CredSSP enabled.");
            Check(Equals(modelType.GetProperty("RDPAuthenticationLevel").GetValue(model), legacyAuthentication) &&
                !(bool)modelType.GetProperty("UseCredSsp").GetValue(model), "Session hardening modified the imported profile.");
            foreach (int configured in new[] { 0, 1, 2, 99 }) {
                modelType.GetProperty("RDPAuthenticationLevel").SetValue(model, Enum.ToObject(modelType.GetProperty("RDPAuthenticationLevel").PropertyType, configured));
                rdpBase.GetMethod("SetAuthenticationLevel", Fields).Invoke(rdp, null);
                Check((int)client.AdvancedSettings5.AuthenticationLevel == (configured == 2 ? 2 : 1) && (bool)client.AdvancedSettings7.EnableCredSspSupport,
                    "Certificate mode mapping weakened NLA or ignored explicit confirmation.");
            }
            Check((bool)client.AdvancedSettings6.RedirectClipboard && (bool)client.AdvancedSettings2.RedirectDrives,
                "RDP ActiveX did not enable clipboard and the custom drive channel.");
            modelType.GetProperty("RedirectClipboard").SetValue(model, false);
            modelType.GetProperty("RedirectDiskDrives").SetValue(model, Enum.Parse(modelType.GetProperty("RedirectDiskDrives").PropertyType, "None"));
            rdpBase.GetMethod("SetRedirection", Fields).Invoke(rdp, null);
            Check(!(bool)client.AdvancedSettings6.RedirectClipboard && !(bool)client.AdvancedSettings2.RedirectDrives, "RDP sharing cannot be disabled again.");
            using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) {
                listener.Start();
                modelType.GetProperty("Hostname").SetValue(model, "127.0.0.1");
                modelType.GetProperty("Port").SetValue(model, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
                using var group = (Control)Activator.CreateInstance(app.GetType("mRemoteNG.Tools.ReconnectGroup", true));
                group.Parent = bridge; group.CreateControl(); bridge.CreateControl();
                var groupType = group.GetType(); groupType.GetProperty("ReconnectWhenReady").SetValue(group, false);
                rdpBase.GetField("ReconnectGroup", Fields).SetValue(rdp, group);
                var poll = rdpBase.GetMethod("tmrReconnect_Elapsed", Fields);
                var worker = Task.Run(() => poll.Invoke(rdp, new object[] { null, null }));
                Check(worker.Wait(TimeSpan.FromSeconds(5)), "Reconnect poll blocked waiting for the UI thread.");
                Check(!(bool)groupType.GetProperty("ServerReady").GetValue(group), "Reconnect poll changed UI state on its worker thread.");
                Application.DoEvents();
                Check((bool)groupType.GetProperty("ServerReady").GetValue(group), "Reconnect result did not reach the UI thread.");
                groupType.GetProperty("ServerReady").SetValue(group, false);
                worker = Task.Run(() => poll.Invoke(rdp, new object[] { null, null }));
                Check(worker.Wait(TimeSpan.FromSeconds(5)), "Second reconnect probe blocked.");
                while (listener.Pending()) { using var accepted = listener.AcceptSocket(); }
                rdp.GetType().GetMethod("Close").Invoke(rdp, null);
                worker = Task.Run(() => poll.Invoke(rdp, new object[] { null, null }));
                Check(worker.Wait(TimeSpan.FromSeconds(5)), "Late timer callback blocked after close.");
                Check(!listener.Pending(), "A late timer callback probed the network after close.");
                Application.DoEvents();
                Check(!(bool)groupType.GetProperty("ServerReady").GetValue(group), "A queued reconnect modified a closed session.");
            }

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
