// Native X11 session host. GPL-2.0; see ../COPYING.txt.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MGRemote.Linux;

public sealed class EmbeddedSession : NativeControlHost, IDisposable
{
    private readonly Func<uint, ProcessStartInfo> _start;
    private readonly Action<string> _notify;
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IntPtr _connection;
    private uint _window;
    private Process? _process;
    private bool _disposed;
    private bool _nativeOwned;
    private readonly Dictionary<uint, (uint Width, uint Height)> _configured = [];
    private bool _layoutDirty = true, _childrenDirty = true;
    private long _probeUntil;
    private bool _ready;
    private int _processId;
    public int ResizeRequests { get; private set; }
    public int TreeQueries { get; private set; }
    public int ChildWindows { get; private set; }
    public int ProcessId => _processId;
    public bool StartupFailed { get; private set; }
    public event Action<string>? Failed;
    public bool ProcessRunning => !_disposed && _process != null && !_process.HasExited;
    public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    public EmbeddedSession(Func<uint, ProcessStartInfo> start, Action<string> notify)
    {
        _start = start; _notify = notify; Focusable = true;
        _resize.Tick += (_, _) => ResizeChildren();
        PointerPressed += (_, _) => FocusNative();
        SizeChanged += (_, _) => ResizeChildren();
        GotFocus += (_, _) => FocusNative();
    }
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_disposed || !OperatingSystem.IsLinux() || parent.HandleDescriptor is not ("XID" or "X11")) {
            _nativeOwned = false;
            Fail("Les sessions intégrées de cette version nécessitent Linux X11/XWayland.");
            return base.CreateNativeControlCore(parent);
        }
        try {
            _connection = xcb_connect(IntPtr.Zero, out _);
            if (_connection == IntPtr.Zero || xcb_connection_has_error(_connection) != 0) throw new IOException("Connexion X11 indisponible.");
            _window = xcb_generate_id(_connection);
            _nativeOwned = true;
            xcb_create_window(_connection, 0, _window, (uint)parent.Handle.ToInt64(), 0, 0, 1, 1, 0, 1, 0, 2 | 2048, new uint[] { 0x181a1f, (1u << 17) | (1u << 19) });
            xcb_map_window(_connection, _window); xcb_flush(_connection);
            _process = new Process { StartInfo = _start(_window), EnableRaisingEvents = true };
            _process.Exited += ProcessExited;
            if (!_process.Start()) throw new IOException("Impossible de lancer la session intégrée.");
            _processId = _process.Id; _probeUntil = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            _resize.Start(); _notify("Démarrage du client…");
            return new PlatformHandle((IntPtr)_window, parent.HandleDescriptor);
        } catch (Exception ex) {
            Fail("Échec de la session : " + ex.Message); Dispose(); _nativeOwned = false;
            return base.CreateNativeControlCore(parent);
        }
    }
    private void ProcessExited(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => {
        if (!_disposed) {
            _resize.Stop();
            if (!_ready) Fail("Le client s’est arrêté avant l’affichage de la session (code " + _process!.ExitCode + "). Vérifiez ses dépendances Linux ou essayez une fenêtre externe.");
            else _notify("Session terminée. Fermez l’onglet ou reconnectez-vous ; vérifiez les messages du terminal si la connexion a échoué.");
        }
    });
    private void Fail(string message)
    {
        if (StartupFailed) return;
        StartupFailed = true; _resize.Stop(); _notify(message);
        // Defer visual changes until CreateNativeControlCore has returned its handle.
        Dispatcher.UIThread.Post(() => Failed?.Invoke(message));
    }
    private uint[] Children()
    {
        if (_connection == IntPtr.Zero || _window == 0) return [];
        TreeQueries++;
        IntPtr reply = xcb_query_tree_reply(_connection, xcb_query_tree(_connection, _window), out IntPtr error);
        try {
            if (reply == IntPtr.Zero || error != IntPtr.Zero) return [];
            int count = xcb_query_tree_children_length(reply);
            if (count < 0 || count > 256) return [];
            IntPtr children = xcb_query_tree_children(reply); var result = new uint[count];
            for (int i = 0; i < count; i++) result[i] = unchecked((uint)Marshal.ReadInt32(children, i * 4));
            return result;
        } finally { if (reply != IntPtr.Zero) free(reply); if (error != IntPtr.Zero) free(error); }
    }
    private void ResizeChildren()
    {
        if (_disposed || StartupFailed || _connection == IntPtr.Zero) return;
        IntPtr pending;
        while ((pending = xcb_poll_for_event(_connection)) != IntPtr.Zero) {
            try {
                int type = Marshal.ReadByte(pending) & 127;
                if (type is 16 or 17 or 18 or 19 or 21) _childrenDirty = true;
                if (type == 22) {
                    uint child = unchecked((uint)Marshal.ReadInt32(pending, 8));
                    if (child == _window) _layoutDirty = true;
                    else if (_configured.TryGetValue(child, out var size) &&
                        (unchecked((ushort)Marshal.ReadInt16(pending, 20)) != size.Width || unchecked((ushort)Marshal.ReadInt16(pending, 22)) != size.Height)) {
                        _configured.Remove(child); _childrenDirty = true;
                    }
                }
            } finally { free(pending); }
        }
        if (!_ready && Stopwatch.GetTimestamp() >= _probeUntil) {
            Fail("Le client n’a pas affiché sa fenêtre intégrée après 10 secondes. Fermez cet onglet puis essayez une fenêtre externe.");
            Dispose(); return;
        }
        if (!IsEffectivelyVisible && _ready) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        uint width = (uint)Math.Clamp(Bounds.Width * scale, 1, 32767), height = (uint)Math.Clamp(Bounds.Height * scale, 1, 32767);
        if (!_layoutDirty && !_childrenDirty && ChildWindows != 0 && _configured.Values.All(s => s.Width == width && s.Height == height)) return;
        var children = Children(); ChildWindows = children.Length;
        if (!_ready && ChildWindows > 0) {
            _ready = true; _resize.Interval = TimeSpan.FromMilliseconds(500);
            _notify("Client intégré prêt. Ctrl+Maj+C/V : copier/coller dans le terminal.");
        }
        foreach (uint child in children) {
            if (_configured.TryGetValue(child, out var size) && size == (width, height)) continue;
            xcb_configure_window(_connection, child, 4 | 8, new[] { width, height });
            _configured[child] = (width, height); ResizeRequests++;
        }
        foreach (uint old in _configured.Keys.Where(k => !children.Contains(k)).ToArray()) _configured.Remove(old);
        _layoutDirty = _childrenDirty = false;
        xcb_flush(_connection);
    }
    private void FocusNative()
    {
        if (_disposed || _connection == IntPtr.Zero || !IsEffectivelyVisible) return;
        var child = Children().LastOrDefault();
        if (child != 0) { xcb_set_input_focus(_connection, 2, child, 0); xcb_flush(_connection); }
    }
    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        Dispose();
        if (!_nativeOwned) base.DestroyNativeControlCore(control);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _resize.Stop();
        _configured.Clear();
        var process = _process;
        if (process != null) {
            process.Exited -= ProcessExited;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ShutdownCompletion = Task.Run(async () => { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) when (_processId == 0) { } finally { process.Dispose(); } });
        }
        if (_connection != IntPtr.Zero) { if (_window != 0) xcb_destroy_window(_connection, _window); xcb_flush(_connection); xcb_disconnect(_connection); _connection = IntPtr.Zero; _window = 0; }
    }
    [DllImport("libxcb.so.1")] private static extern IntPtr xcb_connect(IntPtr display, out int screen);
    [DllImport("libxcb.so.1")] private static extern int xcb_connection_has_error(IntPtr connection);
    [DllImport("libxcb.so.1")] private static extern uint xcb_generate_id(IntPtr connection);
    [DllImport("libxcb.so.1")] private static extern uint xcb_create_window(IntPtr c, byte depth, uint wid, uint parent, short x, short y, ushort width, ushort height, ushort border, ushort windowClass, uint visual, uint mask, uint[] values);
    [DllImport("libxcb.so.1")] private static extern uint xcb_map_window(IntPtr c, uint window);
    [DllImport("libxcb.so.1")] private static extern uint xcb_configure_window(IntPtr c, uint window, ushort mask, uint[] values);
    [DllImport("libxcb.so.1")] private static extern uint xcb_set_input_focus(IntPtr c, byte revert, uint focus, uint time);
    [DllImport("libxcb.so.1")] private static extern uint xcb_query_tree(IntPtr c, uint window);
    [DllImport("libxcb.so.1")] private static extern IntPtr xcb_query_tree_reply(IntPtr c, uint cookie, out IntPtr error);
    [DllImport("libxcb.so.1")] private static extern int xcb_query_tree_children_length(IntPtr reply);
    [DllImport("libxcb.so.1")] private static extern IntPtr xcb_query_tree_children(IntPtr reply);
    [DllImport("libxcb.so.1")] private static extern IntPtr xcb_poll_for_event(IntPtr c);
    [DllImport("libxcb.so.1")] private static extern uint xcb_destroy_window(IntPtr c, uint window);
    [DllImport("libxcb.so.1")] private static extern int xcb_flush(IntPtr c);
    [DllImport("libxcb.so.1")] private static extern void xcb_disconnect(IntPtr c);
    [DllImport("libc.so.6")] private static extern void free(IntPtr pointer);
}
