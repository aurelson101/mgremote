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
    public int ChildWindows { get; private set; }
    public int ProcessId => _process?.Id ?? 0;
    public bool ProcessRunning => !_disposed && _process != null && !_process.HasExited;
    public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;
    public EmbeddedSession(Func<uint, ProcessStartInfo> start, Action<string> notify)
    {
        _start = start; _notify = notify; Focusable = true;
        _resize.Tick += (_, _) => ResizeChildren();
        PointerPressed += (_, _) => FocusNative();
        SizeChanged += (_, _) => ResizeChildren();
    }
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsLinux() || parent.HandleDescriptor is not ("XID" or "X11")) {
            _notify("Les sessions intégrées de cette version nécessitent Linux X11/XWayland.");
            return base.CreateNativeControlCore(parent);
        }
        try {
            _connection = xcb_connect(IntPtr.Zero, out _);
            if (_connection == IntPtr.Zero || xcb_connection_has_error(_connection) != 0) throw new IOException("Connexion X11 indisponible.");
            _window = xcb_generate_id(_connection);
            _nativeOwned = true;
            xcb_create_window(_connection, 0, _window, (uint)parent.Handle.ToInt64(), 0, 0, 1, 1, 0, 1, 0, 2, new uint[] { 0x181a1f });
            xcb_map_window(_connection, _window); xcb_flush(_connection);
            _process = new Process { StartInfo = _start(_window), EnableRaisingEvents = true };
            _process.Exited += ProcessExited;
            if (!_process.Start()) throw new IOException("Impossible de lancer la session intégrée.");
            _resize.Start(); _notify("Session intégrée ouverte. Ctrl+Maj+C/V : copier/coller dans le terminal.");
            return new PlatformHandle((IntPtr)_window, parent.HandleDescriptor);
        } catch (Exception ex) {
            _notify("Échec de la session : " + ex.Message); Dispose(); _nativeOwned = false;
            return base.CreateNativeControlCore(parent);
        }
    }
    private void ProcessExited(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => {
        if (!_disposed) _notify("Session terminée. Fermez l’onglet ou reconnectez-vous ; vérifiez les messages du terminal si la connexion a échoué.");
    });
    private uint[] Children()
    {
        if (_connection == IntPtr.Zero || _window == 0) return [];
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
        if (_disposed || _connection == IntPtr.Zero || !IsEffectivelyVisible) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        uint width = (uint)Math.Clamp(Bounds.Width * scale, 1, 32767), height = (uint)Math.Clamp(Bounds.Height * scale, 1, 32767);
        var children = Children(); ChildWindows = children.Length;
        foreach (uint child in children) xcb_configure_window(_connection, child, 4 | 8, new[] { width, height });
        // Unchecked XCB requests cannot terminate the process on a disappearing child.
        IntPtr pending; while ((pending = xcb_poll_for_event(_connection)) != IntPtr.Zero) free(pending);
        xcb_flush(_connection);
    }
    private void FocusNative()
    {
        if (_disposed || _connection == IntPtr.Zero) return;
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
        var process = _process;
        if (process != null) {
            process.Exited -= ProcessExited;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            ShutdownCompletion = Task.Run(async () => { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { } finally { process.Dispose(); } });
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
